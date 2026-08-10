//! Lifecycle tests for the native -> managed callback contract.
//!
//! These exercise the real exported C ABI (`yaha_request_begin` and friends) with instrumented
//! callbacks, so they verify the same contract `NativeHttpHandlerCore` depends on:
//!
//!  1. **`on_complete` fires exactly once per request, on every terminal path.** If it never
//!     fires, `~RequestContext` blocks forever on `_fullyCompleted.Wait()` (RequestContext.cs:372).
//!  2. **No callback arrives after the state handle has been released.** `OnComplete` calls
//!     `RequestContext.Release()` -> `GCHandle.Free()` (RequestContext.cs:82), so a later
//!     `on_receive`/`on_complete` with the same `state` would hit
//!     `GCHandle.FromIntPtr(freed).Target` and corrupt the managed heap.
//!
//! The registry below models the managed side: `on_complete` marks the state *released*, exactly
//! as `RequestContext.Release()` does. Any callback that arrives afterwards is recorded as a
//! violation instead of dereferencing freed memory, so the test can observe the bug rather than
//! crash on it.

use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::num::NonZeroIsize;
use std::ptr::null;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

use crate::binding::*;
use crate::context::{
    YahaNativeContext, YahaNativeRequestContext, YahaNativeRuntimeContext,
    YahaNativeRuntimeContextInternal,
};
use crate::interop::StringBuffer;
use crate::primitives::{CompletionReason, YahaHttpVersion};

// ---------------------------------------------------------------------------------------------
// Released-handle registry (models the managed GCHandle lifecycle)
// ---------------------------------------------------------------------------------------------

const REASON_SUCCESS: i32 = CompletionReason::Success as i32;
const REASON_ERROR: i32 = CompletionReason::Error as i32;
const REASON_ABORTED: i32 = CompletionReason::Aborted as i32;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Event {
    Status,
    Receive,
    Complete,
}

#[derive(Default)]
struct StateRecord {
    events: Vec<Event>,
    /// Set the moment `on_complete` is observed, mirroring `RequestContext.Release()`.
    /// From here on the managed `state` would be a freed GCHandle.
    released: bool,
    /// Callbacks that arrived with an already-released state. Any entry here is a
    /// use-after-free against the managed heap.
    violations: Vec<String>,
    complete_count: usize,
    receive_count: usize,
    status_count: usize,
    reason: Option<i32>,
    h2_error_code: u32,
    /// Native pointers, so a callback can abort its own request re-entrantly.
    ctx: usize,
    req_ctx: usize,
    /// When set, `on_receive` calls `yaha_request_abort` before completing the task.
    abort_on_receive: bool,
    /// When set, `on_receive` reports failure back to the native side.
    fail_on_receive: bool,
}

fn registry() -> &'static Mutex<HashMap<isize, StateRecord>> {
    static REGISTRY: OnceLock<Mutex<HashMap<isize, StateRecord>>> = OnceLock::new();
    REGISTRY.get_or_init(|| Mutex::new(HashMap::new()))
}

fn next_state() -> NonZeroIsize {
    static NEXT: AtomicIsize = AtomicIsize::new(1);
    NonZeroIsize::new(NEXT.fetch_add(1, Ordering::SeqCst)).unwrap()
}

fn with_record<R>(state: NonZeroIsize, f: impl FnOnce(&mut StateRecord) -> R) -> R {
    let mut reg = registry().lock().unwrap();
    let rec = reg
        .get_mut(&state.get())
        .expect("callback arrived with a state that was never registered");
    f(rec)
}

// --- the three callbacks handed to `yaha_init_context` -----------------------------------------

extern "C" fn on_status_code_and_headers_receive(
    _seq: i32,
    state: NonZeroIsize,
    _status_code: i32,
    _version: YahaHttpVersion,
) {
    with_record(state, |rec| {
        if rec.released {
            rec.violations
                .push("on_status_code_and_headers_receive after release".to_string());
            return;
        }
        rec.status_count += 1;
        rec.events.push(Event::Status);
    });
}

extern "C" fn on_receive(
    _seq: i32,
    state: NonZeroIsize,
    _length: usize,
    _buf: *const u8,
    task_handle: usize,
) {
    // Snapshot what we need, then drop the registry lock before re-entering native code.
    let (violated, abort, fail, ctx, req_ctx) = with_record(state, |rec| {
        if rec.released {
            rec.violations.push("on_receive after release".to_string());
            return (true, false, false, 0usize, 0usize);
        }
        rec.receive_count += 1;
        rec.events.push(Event::Receive);
        (
            false,
            rec.abort_on_receive,
            rec.fail_on_receive,
            rec.ctx,
            rec.req_ctx,
        )
    });

    if !violated && abort {
        // Abort re-entrantly, from inside the callback, while the native task is parked on the
        // oneshot below. This is what `ResponseContext.Cancel()` -> `RequestContext.TryAbort()`
        // does when the caller cancels mid-body.
        yaha_request_abort(ctx as *const YahaNativeContext, req_ctx as *const YahaNativeRequestContext);
    }

    // The native task is parked on `rx.await` (binding.rs:615) until this is called. Always
    // complete it -- including on a violation -- or the request can never finish.
    if !violated && fail {
        let msg = "test: on_receive reported failure";
        let sb = StringBuffer::new(msg.as_ptr(), msg.len() as i32);
        yaha_complete_task(task_handle, &sb);
    } else {
        yaha_complete_task(task_handle, null());
    }
}

extern "C" fn on_complete(
    _seq: i32,
    state: NonZeroIsize,
    reason: CompletionReason,
    h2_error_code: u32,
) {
    with_record(state, |rec| {
        if rec.released {
            rec.violations.push(format!(
                "on_complete after release (reason={})",
                reason as i32
            ));
            return;
        }
        rec.complete_count += 1;
        rec.reason = Some(reason as i32);
        rec.h2_error_code = h2_error_code;
        rec.events.push(Event::Complete);

        // Mirror `NativeHttpHandlerCore.OnComplete` -> `RequestContext.Release()`:
        // the GCHandle is freed here, so `state` is dangling from now on.
        rec.released = true;
    });
}

// ---------------------------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------------------------

struct TestClient {
    runtime: *mut YahaNativeRuntimeContext,
    ctx: *mut YahaNativeContext,
}

// The native context is shared with tokio worker threads by design.
unsafe impl Send for TestClient {}

impl TestClient {
    fn new(configure: impl FnOnce(*mut YahaNativeContext)) -> Self {
        let runtime = yaha_init_runtime(2);
        let ctx = yaha_init_context(
            runtime,
            on_status_code_and_headers_receive,
            on_receive,
            on_complete,
        );
        configure(ctx);
        yaha_build_client(ctx);
        TestClient { runtime, ctx }
    }

    /// Registers a state, builds a GET request for `uri` and begins it.
    fn begin(&self, uri: &str) -> Request {
        self.begin_with(uri, |_| {})
    }

    fn begin_with(&self, uri: &str, configure: impl FnOnce(&mut StateRecord)) -> Request {
        let state = next_state();
        let req_ctx = unsafe { yaha_request_new(self.ctx, state.get() as i32) };

        {
            let mut reg = registry().lock().unwrap();
            let mut rec = StateRecord {
                ctx: self.ctx as usize,
                req_ctx: req_ctx as usize,
                ..Default::default()
            };
            configure(&mut rec);
            reg.insert(state.get(), rec);
        }

        unsafe {
            let method = "GET";
            let mb = StringBuffer::new(method.as_ptr(), method.len() as i32);
            assert!(yaha_request_set_method(self.ctx, req_ctx, &mb));

            let ub = StringBuffer::new(uri.as_ptr(), uri.len() as i32);
            assert!(yaha_request_set_uri(self.ctx, req_ctx, &ub), "invalid uri: {uri}");

            assert!(yaha_request_set_has_body(self.ctx, req_ctx, false));
        }

        assert!(yaha_request_begin(self.ctx, req_ctx, state));

        Request {
            ctx: self.ctx,
            req_ctx,
            state,
            destroyed: false,
        }
    }

    /// Dispose in the order the managed SafeHandles do. Only safe once every request that
    /// referenced this context has completed -- see the note in `dispose_context_after_completion`.
    fn dispose(self) {
        yaha_dispose_context(self.ctx);
        yaha_dispose_runtime(self.runtime);
    }
}

struct Request {
    ctx: *mut YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    state: NonZeroIsize,
    destroyed: bool,
}

impl Request {
    fn abort(&self) {
        yaha_request_abort(self.ctx, self.req_ctx);
    }

    /// Drops the native request handle, as `YahaRequestContextSafeHandle.ReleaseHandle` does.
    fn destroy(&mut self) {
        if !self.destroyed {
            yaha_request_destroy(self.ctx, self.req_ctx);
            self.destroyed = true;
        }
    }

    /// Waits for `on_complete`. Returns false on timeout -- which is exactly the condition that
    /// wedges the managed finalizer thread on `_fullyCompleted.Wait()`.
    fn wait_for_complete(&self, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        loop {
            let done = with_record(self.state, |rec| rec.complete_count > 0);
            if done {
                return true;
            }
            if Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
    }

    fn snapshot(&self) -> Snapshot {
        with_record(self.state, |rec| Snapshot {
            complete_count: rec.complete_count,
            receive_count: rec.receive_count,
            status_count: rec.status_count,
            reason: rec.reason,
            h2_error_code: rec.h2_error_code,
            violations: rec.violations.clone(),
            events: rec.events.clone(),
        })
    }

    /// The core assertion pair: exactly one `on_complete`, and nothing after the release.
    fn assert_completed_once(&self, timeout: Duration) -> Snapshot {
        assert!(
            self.wait_for_complete(timeout),
            "on_complete never fired within {timeout:?}. \
             The managed finalizer would block forever on _fullyCompleted.Wait(). \
             events={:?}",
            self.snapshot().events
        );

        // Give any stray callback a chance to land after the state was released.
        std::thread::sleep(Duration::from_millis(150));

        let snap = self.snapshot();
        assert_eq!(
            snap.complete_count, 1,
            "on_complete must fire exactly once; events={:?}",
            snap.events
        );
        assert!(
            snap.violations.is_empty(),
            "callback(s) arrived with a released state -- this is a use-after-free of the \
             managed GCHandle: {:?}",
            snap.violations
        );
        assert_eq!(
            snap.events.last(),
            Some(&Event::Complete),
            "on_complete must be the final callback; events={:?}",
            snap.events
        );
        snap
    }
}

impl Drop for Request {
    fn drop(&mut self) {
        self.destroy();
    }
}

#[derive(Debug)]
struct Snapshot {
    complete_count: usize,
    receive_count: usize,
    status_count: usize,
    reason: Option<i32>,
    h2_error_code: u32,
    violations: Vec<String>,
    events: Vec<Event>,
}

// ---------------------------------------------------------------------------------------------
// Raw HTTP/1.1 test servers (full control over mid-stream teardown)
// ---------------------------------------------------------------------------------------------

fn spawn_raw_server<F>(handler: F) -> SocketAddr
where
    F: Fn(TcpStream) + Send + Sync + 'static,
{
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let handler = Arc::new(handler);
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            match stream {
                Ok(s) => {
                    let handler = handler.clone();
                    std::thread::spawn(move || handler(s));
                }
                Err(_) => break,
            }
        }
    });
    addr
}

fn read_request_head(stream: &mut TcpStream) {
    let mut buf = [0u8; 1024];
    let mut seen = Vec::new();
    while !seen.windows(4).any(|w| w == b"\r\n\r\n") {
        match stream.read(&mut buf) {
            Ok(0) => return,
            Ok(n) => seen.extend_from_slice(&buf[..n]),
            Err(_) => return,
        }
    }
}

/// Sends headers plus a little body, then stalls forever with the stream still open.
fn server_headers_then_stall() -> SocketAddr {
    spawn_raw_server(|mut s| {
        read_request_head(&mut s);
        let _ = s.write_all(
            b"HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\n\r\n0123456789ABCDEF",
        );
        let _ = s.flush();
        std::thread::sleep(Duration::from_secs(120));
    })
}

/// Announces a body then closes the connection part-way through it -- an incomplete message.
fn server_truncated_body() -> SocketAddr {
    spawn_raw_server(|mut s| {
        read_request_head(&mut s);
        let _ = s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\nshort");
        let _ = s.flush();
        std::thread::sleep(Duration::from_millis(50));
        let _ = s.shutdown(std::net::Shutdown::Both);
    })
}

/// Accepts the connection and never writes a response.
fn server_never_responds() -> SocketAddr {
    spawn_raw_server(|mut s| {
        read_request_head(&mut s);
        std::thread::sleep(Duration::from_secs(120));
    })
}

// ---------------------------------------------------------------------------------------------
// HTTP/2 (h2c) servers: RST_STREAM and GOAWAY mid-stream
// ---------------------------------------------------------------------------------------------

#[derive(Clone, Copy)]
enum H2Teardown {
    ResetStream,
    GoAway,
}

fn spawn_h2_server(teardown: H2Teardown) -> SocketAddr {
    use hyper::body::Bytes;

    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    listener.set_nonblocking(true).unwrap();

    std::thread::spawn(move || {
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        rt.block_on(async move {
            let listener = tokio::net::TcpListener::from_std(listener).unwrap();
            loop {
                let Ok((socket, _)) = listener.accept().await else {
                    break;
                };
                tokio::spawn(async move {
                    let Ok(mut conn) = h2::server::handshake(socket).await else {
                        return;
                    };
                    if let Some(Ok((_req, mut respond))) = conn.accept().await {
                        let response = hyper::http::Response::new(());
                        let Ok(mut send) = respond.send_response(response, false) else {
                            return;
                        };
                        send.reserve_capacity(16);
                        let _ = send.send_data(Bytes::from_static(b"partial-body"), false);
                        tokio::time::sleep(Duration::from_millis(50)).await;

                        match teardown {
                            H2Teardown::ResetStream => {
                                // RST_STREAM mid-body.
                                send.send_reset(h2::Reason::INTERNAL_ERROR);
                            }
                            H2Teardown::GoAway => {
                                // GOAWAY mid-body: the whole connection goes down while the
                                // stream is still open. This is the path the fork's
                                // "fixed GOAWAY bug" commit (bca04c3) touches.
                                conn.abrupt_shutdown(h2::Reason::ENHANCE_YOUR_CALM);
                            }
                        }
                    }
                    let _ = futures_util::future::poll_fn(|cx| conn.poll_closed(cx)).await;
                });
            }
        });
    });

    addr
}

fn h2_client() -> TestClient {
    TestClient::new(|ctx| {
        // h2c: no TLS, so ALPN cannot negotiate -- force HTTP/2.
        yaha_client_config_http2_only(ctx, true);
    })
}

const TIMEOUT: Duration = Duration::from_secs(20);

// ---------------------------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------------------------

/// Baseline: a normal request completes once with Success and nothing follows.
#[test]
fn success_completes_exactly_once() {
    let addr = spawn_raw_server(|mut s| {
        read_request_head(&mut s);
        let _ = s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        let _ = s.flush();
        std::thread::sleep(Duration::from_millis(200));
    });

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_SUCCESS));
    assert_eq!(snap.status_count, 1);
    assert!(snap.receive_count >= 1);
}

/// DNS resolution failure. `.invalid` is reserved by RFC 2606 and can never resolve, so this
/// needs no network. The lookup failure surfaces through `ctx.request(req)` as a
/// `hyper_util::client::legacy::Error` and must land in `complete_with_error` (binding.rs:560).
#[test]
fn dns_resolution_failure_completes_exactly_once() {
    let client = TestClient::new(|_| {});
    let req = client.begin("http://yaha-nonexistent-host.invalid/");
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(
        snap.reason,
        Some(REASON_ERROR),
        "a failed DNS lookup must complete as Error, not silently hang"
    );
    // The request never got a response, so no status/body callbacks may have fired.
    assert_eq!(snap.status_count, 0);
    assert_eq!(snap.receive_count, 0);
}

/// A DNS failure on a request that was *also* aborted: still exactly one `on_complete`.
#[test]
fn dns_failure_with_concurrent_abort_completes_exactly_once() {
    let client = TestClient::new(|_| {});
    let req = client.begin("http://yaha-nonexistent-host-2.invalid/");
    // Race the abort against the resolver failing.
    req.abort();
    let snap = req.assert_completed_once(TIMEOUT);

    assert!(
        snap.reason == Some(REASON_ERROR) || snap.reason == Some(REASON_ABORTED),
        "unexpected reason {:?}",
        snap.reason
    );
}

/// Connect failure/timeout against a non-routable address (RFC 5737 TEST-NET-1).
#[test]
fn connect_timeout_completes_exactly_once() {
    let client = TestClient::new(|ctx| {
        yaha_client_config_connect_timeout(ctx, 1_000);
    });
    let req = client.begin("http://192.0.2.1:81/");
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
    assert_eq!(snap.status_count, 0);
}

/// Connection refused: nothing is listening on the port.
#[test]
fn connection_refused_completes_exactly_once() {
    // Bind then drop, so the port is almost certainly free.
    let addr = {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        l.local_addr().unwrap()
    };

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
}

/// Cancellation mid-response: headers and part of the body have arrived, then the caller aborts.
/// The select! arm at binding.rs:600 must produce exactly one `on_complete(Aborted)`.
#[test]
fn cancel_mid_response_completes_exactly_once() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));

    // Wait until the response body is actually streaming before aborting.
    let deadline = Instant::now() + TIMEOUT;
    while with_record(req.state, |r| r.receive_count) == 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    assert!(
        with_record(req.state, |r| r.receive_count) > 0,
        "server did not start streaming the body"
    );

    req.abort();
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ABORTED));
}

/// Abort raised from *inside* `on_receive`, i.e. while the native task is parked on the oneshot
/// at binding.rs:615. This is the shape of `ResponseContext.Cancel()` firing on a callback thread.
#[test]
fn abort_while_receiving_completes_exactly_once() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let req = client.begin_with(&format!("http://{addr}/"), |rec| {
        rec.abort_on_receive = true;
    });

    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ABORTED));
    assert!(snap.receive_count >= 1);
}

/// The managed side reports a write failure back through `yaha_complete_task`. The native task
/// must turn that into exactly one `on_complete(Error)` (binding.rs:623) and stop.
#[test]
fn receive_failure_completes_exactly_once() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let req = client.begin_with(&format!("http://{addr}/"), |rec| {
        rec.fail_on_receive = true;
    });

    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
    assert_eq!(snap.receive_count, 1);
}

/// Server closes the connection part-way through an announced body.
#[test]
fn truncated_body_completes_exactly_once() {
    let addr = server_truncated_body();

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
}

/// HTTP/2 RST_STREAM mid-body. The error must be classified through the `h2::Error` downcast
/// at binding.rs:669 and produce one `on_complete(Error)`.
#[test]
fn h2_reset_stream_mid_body_completes_exactly_once() {
    let addr = spawn_h2_server(H2Teardown::ResetStream);

    let client = h2_client();
    let req = client.begin(&format!("http://{addr}/"));
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
    assert_eq!(
        snap.h2_error_code,
        u32::from(h2::Reason::INTERNAL_ERROR),
        "the HTTP/2 error code must be propagated to the managed side"
    );
}

/// HTTP/2 GOAWAY mid-body -- the connection dies under an open stream. This is the teardown path
/// the fork's GOAWAY commit touches.
#[test]
fn h2_goaway_mid_body_completes_exactly_once() {
    let addr = spawn_h2_server(H2Teardown::GoAway);

    let client = h2_client();
    let req = client.begin(&format!("http://{addr}/"));
    let snap = req.assert_completed_once(TIMEOUT);

    assert_eq!(snap.reason, Some(REASON_ERROR));
}

/// Dispose-while-in-flight, at the request-handle level: the managed `RequestContext` is being
/// torn down (abort + `yaha_request_destroy`) while the server is still holding the stream open.
/// The in-flight task keeps its own `Arc` clone (binding.rs:522), so destroying the handle must
/// not stop `on_complete` from firing -- otherwise the finalizer wedges.
#[test]
fn destroy_request_while_in_flight_still_completes() {
    let addr = server_never_responds();

    let client = TestClient::new(|_| {});
    let mut req = client.begin(&format!("http://{addr}/"));

    std::thread::sleep(Duration::from_millis(200));

    // Same order as `RequestContext.Dispose` -> `TryAbort` then `TryReleaseNativeHandles`.
    req.abort();
    req.destroy();

    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(snap.reason, Some(REASON_ABORTED));
}

/// Abort before the response ever arrives (connection established, server silent).
#[test]
fn abort_before_response_completes_exactly_once() {
    let addr = server_never_responds();

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));

    std::thread::sleep(Duration::from_millis(200));
    req.abort();

    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(snap.reason, Some(REASON_ABORTED));
    assert_eq!(snap.status_count, 0);
}

/// Aborting twice must not produce a second `on_complete`.
#[test]
fn double_abort_does_not_double_complete() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let req = client.begin(&format!("http://{addr}/"));

    std::thread::sleep(Duration::from_millis(200));
    req.abort();
    req.abort();

    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(snap.reason, Some(REASON_ABORTED));

    // And an abort *after* completion must stay silent -- the managed state is freed by now.
    req.abort();
    std::thread::sleep(Duration::from_millis(150));
    let after = req.snapshot();
    assert_eq!(after.complete_count, 1);
    assert!(
        after.violations.is_empty(),
        "abort after completion produced a callback with a released state: {:?}",
        after.violations
    );
}

/// Many concurrent requests, each aborted at a random-ish point: every one completes exactly once
/// and none produces a post-release callback.
#[test]
fn concurrent_aborts_each_complete_exactly_once() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let uri = format!("http://{addr}/");

    let requests: Vec<Request> = (0..16).map(|_| client.begin(&uri)).collect();

    for (i, req) in requests.iter().enumerate() {
        std::thread::sleep(Duration::from_millis((i % 5) as u64));
        req.abort();
    }

    for req in &requests {
        let snap = req.assert_completed_once(TIMEOUT);
        assert!(
            snap.reason == Some(REASON_ABORTED) || snap.reason == Some(REASON_ERROR),
            "unexpected reason {:?}",
            snap.reason
        );
    }
}

/// A request begun on a context whose client was never built must still complete
/// (binding.rs:543-550) rather than leave the managed side waiting.
#[test]
fn request_without_built_client_completes_exactly_once() {
    let runtime = yaha_init_runtime(1);
    let ctx = yaha_init_context(
        runtime,
        on_status_code_and_headers_receive,
        on_receive,
        on_complete,
    );
    // NOTE: deliberately no `yaha_build_client`.
    let client = TestClient { runtime, ctx };

    let req = client.begin("http://127.0.0.1:1/");
    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(snap.reason, Some(REASON_ERROR));
}

/// Disposing the context *after* every request has completed is clean.
#[test]
fn dispose_context_after_completion() {
    let addr = spawn_raw_server(|mut s| {
        read_request_head(&mut s);
        let _ = s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        let _ = s.flush();
        std::thread::sleep(Duration::from_millis(100));
    });

    let client = TestClient::new(|_| {});
    {
        let req = client.begin(&format!("http://{addr}/"));
        req.assert_completed_once(TIMEOUT);
    } // Request dropped -> yaha_request_destroy

    client.dispose();
}

/// Disposing the context **while a request is in flight**.
///
/// This used to be undefined behaviour: the task held a `&'static mut YahaNativeContextInternal`
/// and `yaha_dispose_context` freed that allocation, so reading `ctx.on_complete` afterwards was a
/// use-after-free jumping into managed code. The task now owns a `RequestClient` snapshot instead,
/// so the request completes normally and nothing reads the freed context.
#[test]
fn dispose_context_while_in_flight_still_completes() {
    let addr = server_headers_then_stall();

    let client = TestClient::new(|_| {});
    let mut req = client.begin(&format!("http://{addr}/"));

    // Let the exchange get properly under way.
    std::thread::sleep(Duration::from_millis(300));

    // Free the context out from under the running task.
    yaha_dispose_context(client.ctx);

    // The request must still terminate, and it must still call back exactly once.
    req.abort();
    let snap = req.assert_completed_once(TIMEOUT);
    assert!(
        snap.reason == Some(REASON_ABORTED) || snap.reason == Some(REASON_ERROR),
        "unexpected reason {:?}",
        snap.reason
    );

    req.destroy();
    yaha_dispose_runtime(client.runtime);
    std::mem::forget(client); // the context and runtime were disposed by hand above
}

/// Shutting the tokio runtime down under an in-flight request must still deliver `on_complete`.
///
/// Dropping the `Runtime` drops every pending task. Without `CompletionGuard`, the task simply
/// vanished and the managed `~RequestContext` waited on `_fullyCompleted` forever.
#[test]
fn dispose_runtime_while_in_flight_still_completes() {
    let addr = server_never_responds();

    let client = TestClient::new(|_| {});
    let mut req = client.begin(&format!("http://{addr}/"));

    std::thread::sleep(Duration::from_millis(300));
    assert_eq!(
        req.snapshot().complete_count,
        0,
        "the request should still be in flight"
    );

    // Drop the runtime, which drops the in-flight task.
    yaha_dispose_runtime(client.runtime);

    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(
        snap.reason,
        Some(REASON_ERROR),
        "a dropped task must report an error rather than going silent"
    );

    req.destroy();
    yaha_dispose_context(client.ctx);
    std::mem::forget(client);
}

/// A request whose method never parsed leaves an error inside the `http::request::Builder`, and
/// `Builder::body` then fails. That used to `.unwrap()` inside the spawned task: the panic skipped
/// `on_complete` entirely and wedged the managed finalizer. It must complete as an error instead.
#[test]
fn unbuildable_request_completes_exactly_once() {
    let client = TestClient::new(|_| {});
    let state = next_state();
    let req_ctx = unsafe { yaha_request_new(client.ctx, state.get() as i32) };

    {
        let mut reg = registry().lock().unwrap();
        reg.insert(
            state.get(),
            StateRecord {
                ctx: client.ctx as usize,
                req_ctx: req_ctx as usize,
                ..Default::default()
            },
        );
    }

    unsafe {
        // A method containing a space is not a valid token, so `Builder::method` stores an error.
        let method = "BAD METHOD";
        let mb = StringBuffer::new(method.as_ptr(), method.len() as i32);
        yaha_request_set_method(client.ctx, req_ctx, &mb);

        let uri = "http://127.0.0.1:1/";
        let ub = StringBuffer::new(uri.as_ptr(), uri.len() as i32);
        yaha_request_set_uri(client.ctx, req_ctx, &ub);

        yaha_request_set_has_body(client.ctx, req_ctx, false);
    }

    assert!(yaha_request_begin(client.ctx, req_ctx, state));

    let req = Request { ctx: client.ctx, req_ctx, state, destroyed: false };
    let snap = req.assert_completed_once(TIMEOUT);
    assert_eq!(snap.reason, Some(REASON_ERROR));

    // An error completion must always leave a message behind: the managed `OnComplete` calls
    // `yaha_get_last_error` and dereferences the result without a null check.
    let err = yaha_get_last_error(client.ctx, req_ctx);
    assert!(!err.is_null(), "on_complete(Error) must set last_error");
    unsafe { yaha_free_byte_buffer(err as *mut _) };
}

/// `yaha_complete_task` used to `.unwrap()` the send, so completing a task whose receiver is gone
/// aborted the process. It must be a silent no-op.
#[test]
fn complete_task_after_receiver_dropped_does_not_abort() {
    let (tx, rx) = tokio::sync::oneshot::channel::<Result<(), String>>();
    drop(rx);
    let handle = Box::into_raw(Box::new(tx)) as usize;

    yaha_complete_task(handle, null());

    // And the error variant, plus the null-handle guard.
    let (tx, rx) = tokio::sync::oneshot::channel::<Result<(), String>>();
    drop(rx);
    let handle = Box::into_raw(Box::new(tx)) as usize;
    let msg = "boom";
    let sb = StringBuffer::new(msg.as_ptr(), msg.len() as i32);
    yaha_complete_task(handle, &sb);

    yaha_complete_task(0, null());
}

/// `yaha_is_runtime_thread` must distinguish a tokio thread from an ordinary one, on every
/// platform. This is what the managed side now uses instead of comparing OS thread names.
#[test]
fn is_runtime_thread_detects_tokio_threads() {
    assert!(
        !yaha_is_runtime_thread(),
        "the test thread is not a runtime thread"
    );

    let runtime = yaha_init_runtime(1);
    {
        let rt = YahaNativeRuntimeContextInternal::from_raw_context(runtime);
        let on_worker = rt.runtime.block_on(async { yaha_is_runtime_thread() });
        assert!(on_worker, "a tokio worker thread must be detected");

        let on_blocking = rt
            .runtime
            .block_on(async { tokio::task::spawn_blocking(|| yaha_is_runtime_thread()).await.unwrap() });
        assert!(
            on_blocking,
            "a tokio blocking-pool thread must be detected too -- the old thread-name check missed these"
        );
    }
    yaha_dispose_runtime(runtime);

    assert!(!yaha_is_runtime_thread());
}
