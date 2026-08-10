use std::{
    error::Error, num::NonZeroIsize, ptr::null, sync::{Arc, Mutex}, time::Duration
};

use http_body_util::{combinators::BoxBody, BodyExt};
use hyper::{
    body::{Body, Bytes, Frame},
    http::{HeaderName, HeaderValue},
    Request, StatusCode, Uri, Version,
};
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio::{select, sync::oneshot};
use tokio_util::sync::CancellationToken;

use crate::interop::{ByteBuffer, StringBuffer};
use crate::resolver::YahaSocketAddress;
use crate::primitives::{CompletionReason, YahaHttpVersion};
use crate::{
    context::{
        OnComplete, YahaNativeContext, YahaNativeContextInternal, YahaNativeRequestContext,
        YahaNativeRequestContextInternal, YahaNativeRuntimeContext,
        YahaNativeRuntimeContextInternal,
    },
    primitives::WriteResult,
};
use futures_util::StreamExt;


#[no_mangle]
pub extern "C" fn yaha_get_last_error(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext
) -> *const ByteBuffer {
    let req_ctx = crate::context::to_internal(req_ctx);
    let req_ctx = req_ctx.lock().unwrap();

    match req_ctx.last_error.as_ref() {
        Some(e) => {
            let buf = ByteBuffer::from_vec(e.clone().into_bytes());
            Box::into_raw(Box::new(buf))
        }
        None => null(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn yaha_free_byte_buffer(s: *mut ByteBuffer) {
    let buf = Box::from_raw(s);
    buf.destroy();
}

#[no_mangle]
pub extern "C" fn yaha_init_runtime(worker_threads: i32) -> *mut YahaNativeRuntimeContext {
    let runtime = Box::new(YahaNativeRuntimeContextInternal::new(worker_threads));

    Box::into_raw(runtime) as *mut YahaNativeRuntimeContext
}
#[no_mangle]
pub extern "C" fn yaha_dispose_runtime(ctx: *mut YahaNativeRuntimeContext) {
    let ctx = unsafe { Box::from_raw(ctx as *mut YahaNativeRuntimeContextInternal) };
}

#[no_mangle]
pub extern "C" fn yaha_init_context(
    runtime_ctx: *mut YahaNativeRuntimeContext,
    on_status_code_and_headers_receive: extern "C" fn(
        req_seq: i32,
        state: NonZeroIsize,
        status_code: i32,
        version: YahaHttpVersion,
    ),
    on_receive: extern "C" fn(req_seq: i32, state: NonZeroIsize, length: usize, buf: *const u8, task_handle: usize),
    on_complete: extern "C" fn(req_seq: i32, state: NonZeroIsize, reason: CompletionReason, h2_error_code: u32),
) -> *mut YahaNativeContext {
    let runtime_ctx = YahaNativeRuntimeContextInternal::from_raw_context(runtime_ctx);
    let ctx = Box::new(YahaNativeContextInternal::new(
        runtime_ctx.runtime.handle().clone(),
        on_status_code_and_headers_receive,
        on_receive,
        on_complete,
    ));
    Box::into_raw(ctx) as *mut YahaNativeContext
}

#[no_mangle]
pub extern "C" fn yaha_dispose_context(ctx: *mut YahaNativeContext) {
    // Reclaims the context allocation. This is safe to call with requests still in flight: each
    // request task owns a `RequestClient` snapshot (see `context.rs`) instead of borrowing the
    // context, so nothing reads this memory once it is freed.
    //
    // This used to overwrite the three callbacks with panicking sentinels first. That was dead
    // code - the `Box` is dropped on return, so the sentinels were written into memory that is
    // freed microseconds later - and it disguised the real hazard, which was that in-flight tasks
    // held a `&'static mut` into this very allocation.
    drop(unsafe { Box::from_raw(ctx as *mut YahaNativeContextInternal) });
}

/// Whether the calling thread belongs to the native tokio runtime (a worker or blocking thread).
///
/// The managed side must never release native handles from a runtime thread. It used to detect
/// that by comparing the OS thread name, which only ever worked on Windows and was therefore inert
/// on Android/IL2CPP - the platform where the mistake is most costly. Asking tokio directly works
/// everywhere and also catches blocking-pool threads, which are named differently.
///
/// See `UnsafeUtilities.IsRunningOnNativeRuntimeThread`.
#[no_mangle]
pub extern "C" fn yaha_is_runtime_thread() -> bool {
    tokio::runtime::Handle::try_current().is_ok()
}

#[no_mangle]
pub extern "C" fn yaha_client_config_add_root_certificates(
    ctx: *mut YahaNativeContext,
    root_certs: *const StringBuffer,
) -> usize {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    let root_certificates = ctx
        .root_certificates
        .get_or_insert(rustls::RootCertStore::empty());
    let valid: usize = unsafe {
        rustls_pemfile::certs(&mut (*root_certs).to_bytes())
            .filter_map(Result::ok)
            .map(|cert| root_certificates.add(cert))
            .filter_map(|result| result.is_ok().then(|| 1))
            .sum()
    };

    valid
}

#[no_mangle]
pub extern "C" fn yaha_client_config_add_override_server_name(
    ctx: *mut YahaNativeContext,
    override_server_name: *const StringBuffer,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    let server_name = unsafe { (*override_server_name).to_str() };
    ctx.override_server_name.get_or_insert(server_name.to_string());
}

#[no_mangle]
pub extern "C" fn yaha_client_config_add_client_auth_certificates(
    ctx: *mut YahaNativeContext,
    auth_certs: *const StringBuffer,
) -> usize {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    let certs: Vec<CertificateDer> = unsafe {
        rustls_pemfile::certs(&mut (*auth_certs).to_bytes())
            .filter_map(Result::ok)
            .map(CertificateDer::from)
            .collect()
    };

    let count = certs.len();

    if count > 0 {
        ctx.client_auth_certificates.get_or_insert(certs);
    }

    count
}

#[no_mangle]
pub extern "C" fn yaha_client_config_add_client_auth_key(
    ctx: *mut YahaNativeContext,
    auth_key: *const StringBuffer,
) -> usize {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    let keys: Vec<PrivateKeyDer> = unsafe {
        rustls_pemfile::pkcs8_private_keys(&mut (*auth_key).to_bytes())
            .filter_map(Result::ok)
            .map(PrivateKeyDer::from)
            .collect()
    };

    let count = keys.len();

    if count > 0 {
        ctx.client_auth_key.get_or_insert(keys[0].clone_key());
    }

    count
}

#[no_mangle]
pub extern "C" fn yaha_client_config_skip_certificate_verification(
    ctx: *mut YahaNativeContext,
    val: bool,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.skip_certificate_verification = Some(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_set_server_certificate_verification_handler(
    ctx: *mut YahaNativeContext,
    handler: Option<extern "C" fn(state: NonZeroIsize, server_name: *const u8, server_name_len: usize, certificate_der: *const u8, certificate_der_len: usize, now: u64) -> bool>,
    callback_state: NonZeroIsize
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.server_certificate_verification_handler = handler.map(|x| (x, callback_state));
}

/// Installs an external name resolver, replacing the platform one (`getaddrinfo`).
///
/// Pass `None` to go back to the platform resolver. The handler runs on a blocking thread, so it is
/// free to make a synchronous platform call - on Android that means resolving against the currently
/// active `Network`, which is the only way to avoid a stale process-wide network binding making
/// every lookup fail after the app returns from the background.
///
/// Must be called before `yaha_build_client`.
#[no_mangle]
pub extern "C" fn yaha_client_config_set_dns_resolver(
    ctx: *mut YahaNativeContext,
    // NOTE: spelled out rather than using the `OnResolveDns` alias so that csbindgen emits a
    // delegate type and the `YahaSocketAddress` struct for the managed side.
    handler: Option<
        extern "C" fn(
            callback_state: NonZeroIsize,
            host: *const u8,
            host_len: usize,
            addresses: *mut YahaSocketAddress,
            addresses_capacity: i32,
        ) -> i32,
    >,
    callback_state: NonZeroIsize,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.dns_resolver_handler = handler.map(|handler| (handler, callback_state));
}

/// How long a previously resolved address stays usable after a lookup fails. `0` disables the
/// fallback entirely.
///
/// Must be called before `yaha_build_client`.
#[no_mangle]
pub extern "C" fn yaha_client_config_dns_cache_fallback_duration(
    ctx: *mut YahaNativeContext,
    duration_milliseconds: u64,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.dns_cache_fallback_duration = if duration_milliseconds == 0 {
        None
    } else {
        Some(Duration::from_millis(duration_milliseconds))
    };
}

#[no_mangle]
pub extern "C" fn yaha_client_config_pool_idle_timeout(
    ctx: *mut YahaNativeContext,
    val_milliseconds: u64,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .pool_idle_timeout(Duration::from_millis(val_milliseconds));
}

#[no_mangle]
pub extern "C" fn yaha_client_config_pool_max_idle_per_host(
    ctx: *mut YahaNativeContext,
    max_idle: usize,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .pool_max_idle_per_host(max_idle);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_only(ctx: *mut YahaNativeContext, val: bool) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder.as_mut().unwrap().http2_only(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_initial_stream_window_size(
    ctx: *mut YahaNativeContext,
    val: u32,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_initial_stream_window_size(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_initial_connection_window_size(
    ctx: *mut YahaNativeContext,
    val: u32,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_initial_connection_window_size(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_adaptive_window(ctx: *mut YahaNativeContext, val: bool) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_adaptive_window(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_max_frame_size(ctx: *mut YahaNativeContext, val: u32) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_max_frame_size(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_max_header_list_size(
    ctx: *mut YahaNativeContext,
    val: u32,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_max_header_list_size(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_keep_alive_interval(
    ctx: *mut YahaNativeContext,
    interval_milliseconds: u64,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_keep_alive_interval(Duration::from_millis(interval_milliseconds));
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_keep_alive_timeout(
    ctx: *mut YahaNativeContext,
    timeout_milliseconds: u64,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_keep_alive_timeout(Duration::from_millis(timeout_milliseconds));
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_keep_alive_while_idle(
    ctx: *mut YahaNativeContext,
    val: bool,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_keep_alive_while_idle(val);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_connect_timeout(
    ctx: *mut YahaNativeContext,
    timeout_milliseconds: u64,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.connect_timeout.get_or_insert(Duration::from_millis(timeout_milliseconds));
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_max_concurrent_reset_streams(
    ctx: *mut YahaNativeContext,
    max: usize,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_max_concurrent_reset_streams(max.try_into().unwrap());
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_max_send_buf_size(
    ctx: *mut YahaNativeContext,
    max: usize,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_max_send_buf_size(max);
}

#[no_mangle]
pub extern "C" fn yaha_client_config_http2_initial_max_send_streams(
    ctx: *mut YahaNativeContext,
    initial: usize,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.client_builder
        .as_mut()
        .unwrap()
        .http2_initial_max_send_streams(initial);
}

#[cfg(unix)]
#[no_mangle]
pub extern "C" fn yaha_client_config_unix_domain_socket_path(
    ctx: *mut YahaNativeContext,
    uds_path: *const StringBuffer,
) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);

    let uds_socket_path = unsafe { (*uds_path).to_str() };
    ctx.uds_socket_path.get_or_insert(uds_socket_path.into());
}

#[no_mangle]
pub extern "C" fn yaha_build_client(ctx: *mut YahaNativeContext) {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);
    ctx.build_client();
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_new(
    ctx: *const YahaNativeContext,
    seq: i32,
) -> *const YahaNativeRequestContext {
    let builder = Request::builder();

    let req_ctx = Arc::new(Mutex::new(YahaNativeRequestContextInternal {
        seq: seq,
        builder: Some(builder),
        sender: None,
        has_body: false,
        completed: false,
        cancellation_token: CancellationToken::new(),
        last_error: None,

        response_version: YahaHttpVersion::Http10,
        response_trailers: None,
        response_headers: None,
        response_status: StatusCode::OK,
    }));
    Arc::into_raw(req_ctx) as *const YahaNativeRequestContext
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_set_method(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    value: *const StringBuffer,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    assert!(req_ctx.builder.is_some());

    let builder = req_ctx.builder.take().unwrap();
    req_ctx.builder = Some(builder.method((*value).to_str()));
    true
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_set_has_body(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    value: bool,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    assert!(req_ctx.builder.is_some());

    req_ctx.has_body = value;
    true
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_set_uri(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    value: *const StringBuffer,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    assert!(req_ctx.builder.is_some());

    let builder = req_ctx.builder.take().unwrap();
    match Uri::try_from((*value).to_str()) {
        Ok(uri) => {
            req_ctx.builder = Some(builder.uri(uri));
            true
        }
        Err(err) => {
            req_ctx.last_error = Some(err.to_string());
            false
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_set_version(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    value: YahaHttpVersion,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    assert!(req_ctx.builder.is_some());

    let builder = req_ctx.builder.take().unwrap();
    req_ctx.builder = Some(builder.version(match value {
        YahaHttpVersion::Http09 => Version::HTTP_09,
        YahaHttpVersion::Http10 => Version::HTTP_10,
        YahaHttpVersion::Http11 => Version::HTTP_11,
        YahaHttpVersion::Http2 => Version::HTTP_2,
        YahaHttpVersion::Http3 => Version::HTTP_3,
    }));
    true
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_set_header(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    key: *const StringBuffer,
    value: *const StringBuffer,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    assert!(req_ctx.builder.is_some());

    // TODO: Handle invalid header values
    let builder = req_ctx.builder.take().unwrap();
    req_ctx.builder = Some(builder.header(
        HeaderName::from_bytes((*key).to_bytes()).unwrap(),
        HeaderValue::from_bytes((*value).to_bytes()).unwrap(),
    ));

    true
}

/// Guarantees that exactly one `on_complete` is delivered for every request.
///
/// The managed `~RequestContext` blocks on `_fullyCompleted.Wait()`, and only `on_complete` ever
/// releases it. So a request that ends without calling back wedges the CLR finalizer thread for
/// the lifetime of the process - which stops *all* finalization, not just this handler's.
///
/// Every terminal path calls [`CompletionGuard::complete`]. If the task instead unwinds (a panic
/// anywhere in the request pipeline) or is dropped without completing (the tokio runtime being shut
/// down under an in-flight request), `Drop` delivers `on_complete(Error)` on the way out. The
/// `completed` flag makes the call idempotent, so the "exactly once" half of the contract holds
/// even if a terminal path and the drop both run.
struct CompletionGuard {
    on_complete: OnComplete,
    seq: i32,
    state: NonZeroIsize,
    req_ctx: Arc<Mutex<YahaNativeRequestContextInternal>>,
    completed: bool,
}

impl CompletionGuard {
    fn new(
        on_complete: OnComplete,
        seq: i32,
        state: NonZeroIsize,
        req_ctx: Arc<Mutex<YahaNativeRequestContextInternal>>,
    ) -> Self {
        CompletionGuard { on_complete, seq, state, req_ctx, completed: false }
    }

    /// Records an error message for `yaha_get_last_error` to hand back to the managed side.
    ///
    /// `NativeHttpHandlerCore.OnComplete` dereferences that buffer unconditionally when the reason
    /// is `Error`, so every error completion must leave one behind.
    fn set_last_error(&self, message: String) {
        if let Ok(mut req_ctx) = self.req_ctx.lock() {
            req_ctx.last_error = Some(message);
        }
    }

    fn complete(&mut self, reason: CompletionReason, h2_error_code: u32) {
        if self.completed {
            return;
        }
        self.completed = true;
        (self.on_complete)(self.seq, self.state, reason, h2_error_code);
    }

    fn complete_with_message(&mut self, reason: CompletionReason, message: &str) {
        self.set_last_error(message.to_string());
        self.complete(reason, 0);
    }
}

impl Drop for CompletionGuard {
    fn drop(&mut self) {
        if self.completed {
            return;
        }

        // The task is going away without having completed: either it panicked, or the runtime was
        // shut down and dropped it. Report an error rather than leaving the managed side waiting
        // forever. `last_error` may already be set by whoever tore us down; only fill in a generic
        // message if it is not, since it must never be `None` for an error completion.
        if let Ok(mut req_ctx) = self.req_ctx.lock() {
            if req_ctx.last_error.is_none() {
                req_ctx.last_error = Some(
                    "The request was terminated before it completed. The native request task was \
                     dropped or panicked."
                        .to_string(),
                );
            }
        }

        self.completed = true;
        (self.on_complete)(self.seq, self.state, CompletionReason::Error, 0);
    }
}

#[no_mangle]
pub extern "C" fn yaha_request_begin(
    ctx: *mut YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    state: NonZeroIsize
) -> bool {
    let ctx = YahaNativeContextInternal::from_raw_context(ctx);

    // Take everything the task needs from the context *by value*, before spawning. The task must
    // not borrow the context: `yaha_dispose_context` frees it, and the managed side is free to call
    // that while this request is still running.
    let client = ctx.request_client();
    let runtime = ctx.runtime.clone();

    // Begin request on async runtime.
    let body;
    let seq;

    let req_ctx = crate::context::to_internal_arc(req_ctx); // NOTE: we must call `Arc::into_raw` at last of the method.

    {
        let mut req_ctx = req_ctx.lock().unwrap();
        seq = req_ctx.seq;

        if req_ctx.has_body {
            let (tx, rx) = futures_channel::mpsc::channel::<Bytes>(0);
            body = BoxBody::new(http_body_util::StreamBody::new(rx.map(|data| Result::Ok(Frame::data(data)))));
            req_ctx.sender = Some(tx);
        } else {
            // Use a genuinely empty body rather than an immediately-closed stream.
            //
            // A StreamBody reports `is_end_stream() == false` and an unbounded `size_hint()`, so hyper
            // cannot tell the request has no body: it omits END_STREAM from the HEADERS frame and has
            // to terminate the stream afterwards with an empty DATA frame, or with RST_STREAM if the
            // response completed first. That doubles the frames per bodyless request and leaves the
            // stream open longer, which trips HTTP/2 servers that bound how many streams they track
            // (Kestrel refuses new streams with ENHANCE_YOUR_CALM past MaxTrackedStreams).
            //
            // `Empty` reports end-of-stream up front, so hyper sets END_STREAM on HEADERS and the
            // request is a single frame, matching SocketsHttpHandler.
            // `Empty`'s error type is `Infallible`; the empty match widens it to `hyper::Error`
            // without introducing a panic path, since `Infallible` has no variants.
            body = BoxBody::new(http_body_util::Empty::<Bytes>::new().map_err(|never| match never {}));
        }
    }
    {
        let req_ctx = req_ctx.clone();
        runtime.spawn(async move {
            // From here on, every exit path - including a panic or the runtime dropping this task -
            // delivers exactly one `on_complete`. See `CompletionGuard`.
            let mut guard = CompletionGuard::new(client.on_complete, seq, state, req_ctx.clone());

            let cancellation_token = {
                let req_ctx = req_ctx.lock().unwrap();
                req_ctx.cancellation_token.clone()
            };

            // Prepare for begin request.
            //
            // `Builder::body` fails if any earlier `yaha_request_set_*` call stored an error (an
            // unparsable method, for instance). Report that instead of unwrapping: a panic here
            // used to skip `on_complete` entirely.
            let req = {
                let mut req_ctx = req_ctx.lock().unwrap();
                let Some(builder) = req_ctx.builder.take() else {
                    drop(req_ctx);
                    guard.complete_with_message(
                        CompletionReason::Error,
                        "The request has already been started.",
                    );
                    return;
                };
                match builder.body(body) {
                    Ok(req) => req,
                    Err(err) => {
                        drop(req_ctx);
                        guard.complete_with_message(
                            CompletionReason::Error,
                            &format!("Failed to build the request: {err}"),
                        );
                        return;
                    }
                }
            };

            if !client.has_client() {
                guard.complete_with_message(
                    CompletionReason::Error,
                    "The client has not been built. You need to build it before sending the request.",
                );
                return;
            }

            // Send a request and wait for response status and headers.
            let res = select! {
                _ = cancellation_token.cancelled() => {
                    guard.complete(CompletionReason::Aborted, 0);
                    return;
                }
                res = client.request(req) => {
                    match res {
                        Err(err) => {
                            complete_with_error(&mut guard, err);
                            return;
                        }
                        Ok(res) => res,
                    }
                }
            };

            // Status code and response headers are received.
            let mut res = res;
            {
                let mut req_ctx = req_ctx.lock().unwrap();
                req_ctx.response_headers = Some(
                    res.headers()
                        .iter()
                        .map(|x| {
                            (
                                x.0.to_string(),
                                x.1.to_str().unwrap_or_default().to_string(),
                            )
                        })
                        .collect::<Vec<(String, String)>>(),
                );
                req_ctx.response_status = res.status();
                req_ctx.response_version = YahaHttpVersion::from(res.version());
            }
            (client.on_status_code_and_headers_receive)(
                seq,
                state,
                res.status().as_u16() as i32,
                YahaHttpVersion::from(res.version()),
            );

            // Read the response body stream.
            let body = res.body_mut();

            let mut trailer_received = false;

            while !body.is_end_stream() {
                select! {
                    _ = cancellation_token.cancelled() => {
                        guard.complete(CompletionReason::Aborted, 0);
                        return;
                    }
                    received = body.frame() => {
                        match received {
                            Some(x) => {
                                match x {
                                    Ok(frame) => {
                                        if frame.is_data() {
                                            let data = frame.into_data().unwrap();
                                            let (tx, rx) = oneshot::channel::<Result<(), String>>();
                                            let tx = Box::into_raw(Box::new(tx)) as usize;

                                            (client.on_receive)(seq, state, data.len(), data.as_ptr(), tx);

                                            // Wait for the managed side to finish consuming the
                                            // frame - but stay cancellable while we do.
                                            //
                                            // This is the one await in the request that used to be
                                            // outside the cancellation `select!`, which made an
                                            // aborted request depend entirely on managed code
                                            // reaching `yaha_complete_task`. If cancellation wins,
                                            // `rx` is dropped; the managed side still owns the
                                            // `Sender` and `yaha_complete_task` simply finds the
                                            // receiver gone and does nothing.
                                            //
                                            // Dropping the frame here is safe: `OnReceive` copies
                                            // the buffer into the response pipe synchronously
                                            // before returning, and only the flush is deferred.
                                            let received = select! {
                                                _ = cancellation_token.cancelled() => {
                                                    guard.complete(CompletionReason::Aborted, 0);
                                                    return;
                                                }
                                                result = rx => result,
                                            };

                                            match received {
                                                Ok(result) => {
                                                    if let Err(err) = result {
                                                        // the sender reports an error
                                                        guard.set_last_error(err);
                                                        guard.complete(CompletionReason::Error, 0);
                                                        return;
                                                    }
                                                },
                                                Err(_) => {
                                                    // the sender is dropped without sending
                                                    guard.complete_with_message(
                                                        CompletionReason::Error,
                                                        "on_receive() has not completed correctly.",
                                                    );
                                                    return;
                                                }
                                            }
                                        } else if frame.is_trailers() {

                                            trailer_received = true;

                                            {
                                                let mut req_ctx = req_ctx.lock().unwrap();
                                                req_ctx.try_complete();
                                            }

                                            let trailers = frame.into_trailers().unwrap();
                                            let mut req_ctx = req_ctx.lock().unwrap();
                                            req_ctx.response_trailers = Some(
                                                trailers
                                                    .iter()
                                                    .map(|x| {
                                                        (
                                                            x.0.to_string(),
                                                            x.1.to_str().unwrap_or_default().to_string(),
                                                        )
                                                    })
                                                    .collect::<Vec<(String, String)>>(),
                                            );
                                        }
                                    }
                                    Err(err) => {
                                        //println!("body.data: on_complete_error");
                                        guard.set_last_error(err.to_string());

                                        // If the `hyper::Error` has `h2::Error` as inner error, the error has HTTP/2 error code.
                                        let reason = err.source()
                                            .and_then(|e| e.downcast_ref::<h2::Error>())
                                            .and_then(|e| e.reason());

                                        let rc = reason.map(|r| u32::from(r));

                                        guard.complete(CompletionReason::Error, rc.unwrap_or_default());
                                        return;
                                    }
                                }
                            }
                            None => {
                                //println!("body.data: None; is_end_stream={}", body.is_end_stream());
                                break;
                            }
                        }
                    }
                }
            }

            if !trailer_received {
                let mut req_ctx = req_ctx.lock().unwrap();
                req_ctx.try_complete();
            }

            guard.complete(CompletionReason::Success, 0);

            {
                let mut req_ctx = req_ctx.lock().unwrap();
                req_ctx.completed = true;
            }
        });
    }

    _ = Arc::into_raw(req_ctx);
    true
}

fn complete_with_error(guard: &mut CompletionGuard, err: hyper_util::client::legacy::Error) {
    let mut h2_error_code = None;

    // If the error has the inner error, use its error message instead.
    if let Some(error_inner) = err.source() {
        guard.set_last_error(format!("{}: {}", err.to_string(), error_inner.to_string()));

        // If the Error has `h2::Error` as inner error, the error has HTTP/2 error code.
        h2_error_code = error_inner.source()
            .and_then(|e| e.downcast_ref::<h2::Error>())
            .and_then(|e| e.reason())
            .map(|e| u32::from(e));
    } else {
        guard.set_last_error(err.to_string());
    }

    guard.complete(CompletionReason::Error, h2_error_code.unwrap_or_default());
}

#[no_mangle]
pub extern "C" fn yaha_request_abort(ctx: *const YahaNativeContext, req_ctx: *const YahaNativeRequestContext) {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    req_ctx.cancellation_token.cancel()
}

#[no_mangle]
pub extern "C" fn yaha_request_write_body(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    buf: *const u8,
    len: usize,
) -> WriteResult {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    let slice = unsafe { std::slice::from_raw_parts(buf, len) };

    match req_ctx.sender.as_mut() {
        Some(sender) => {
            let result = sender.try_send(Bytes::copy_from_slice(slice));
            match result {
                Ok(_) => WriteResult::Success,
                Err(_) => WriteResult::Full,
            }
        }

        // The request has been completed.
        None => WriteResult::AlreadyCompleted
    }
}

#[no_mangle]
pub extern "C" fn yaha_request_complete_body(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
) -> bool {
    let mut req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    //debug_assert!(!req_ctx.completed);

    req_ctx.try_complete();
    true
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_headers_count(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
) -> i32 {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    match req_ctx.response_headers.as_ref() {
        Some(headers) => headers.len() as i32,
        None => 0,
    }
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_header_key(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    index: i32,
) -> *const ByteBuffer {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    let headers = req_ctx.response_headers.as_ref().unwrap();
    let key_value = headers.get(index as usize).unwrap();
    let buf = ByteBuffer::from_vec(key_value.0.clone().into_bytes());
    Box::into_raw(Box::new(buf))
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_header_value(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    index: i32,
) -> *const ByteBuffer {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    let headers = req_ctx.response_headers.as_ref().unwrap();
    let key_value = headers.get(index as usize).unwrap();
    let buf = ByteBuffer::from_vec(key_value.1.clone().into_bytes());
    Box::into_raw(Box::new(buf))
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_trailers_count(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
) -> i32 {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    match req_ctx.response_trailers.as_ref() {
        Some(trailers) => trailers.len() as i32,
        None => 0,
    }
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_trailers_key(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    index: i32,
) -> *const ByteBuffer {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    let trailers = req_ctx.response_trailers.as_ref().unwrap();
    let key_value = trailers.get(index as usize).unwrap();
    let buf = ByteBuffer::from_vec(key_value.0.clone().into_bytes());
    Box::into_raw(Box::new(buf))
}

#[no_mangle]
pub unsafe extern "C" fn yaha_request_response_get_trailers_value(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
    index: i32,
) -> *const ByteBuffer {
    let req_ctx = crate::context::to_internal(req_ctx).lock().unwrap();
    debug_assert!(!req_ctx.completed);

    let trailers = req_ctx.response_trailers.as_ref().unwrap();
    let key_value = trailers.get(index as usize).unwrap();
    let buf = ByteBuffer::from_vec(key_value.1.clone().into_bytes());
    Box::into_raw(Box::new(buf))
}

#[no_mangle]
pub extern "C" fn yaha_request_destroy(
    ctx: *const YahaNativeContext,
    req_ctx: *const YahaNativeRequestContext,
) -> bool {
    let req_ctx = crate::context::to_internal_arc(req_ctx);
    true
}

#[no_mangle]
pub extern "C" fn yaha_complete_task(task_handle: usize, error: *const StringBuffer) {
    if task_handle == 0 {
        return;
    }

    let tx = unsafe { Box::from_raw(task_handle as *mut oneshot::Sender<Result<(), String>>) };
    let result = if error.is_null() {
        Ok(())
    } else {
        Err(unsafe { (*error).to_str().to_string() })
    };

    // Ignore the send result. `send` fails only when the receiver is already gone, which is a
    // normal outcome: the request may have been cancelled while the managed side was still
    // consuming the frame, or the runtime may have dropped the task. This used to be `.unwrap()`,
    // and a panic out of an `extern "C"` function aborts the whole process.
    let _ = tx.send(result);
}
