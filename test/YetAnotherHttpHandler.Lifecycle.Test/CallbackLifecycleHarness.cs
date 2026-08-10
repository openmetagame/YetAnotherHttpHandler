using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Drives the raw native API with instrumented callbacks so the two lifecycle invariants become
/// directly observable from managed code:
///
/// <list type="number">
/// <item><c>on_complete</c> fires exactly once per request, on every terminal path. If it never
/// fires, <c>~RequestContext</c> blocks forever on <c>_fullyCompleted.Wait()</c>.</item>
/// <item>No callback arrives with a state that has already been released. The real
/// <c>NativeHttpHandlerCore.OnComplete</c> calls <c>RequestContext.Release()</c> -> <c>GCHandle.Free()</c>,
/// so a later callback would evaluate <c>GCHandle.FromIntPtr(freed).Target</c> and corrupt the
/// managed heap.</item>
/// </list>
///
/// This goes through the real reverse-P/Invoke marshalling, unlike the Rust-side tests, so it also
/// covers the delegate/marshalling layer that Unity IL2CPP replaces with <c>MonoPInvokeCallback</c>.
/// </summary>
internal sealed unsafe class CallbackLifecycleHarness : IDisposable
{
    // Kept alive for the lifetime of the process: the Rust side stores raw function pointers.
    private static readonly NativeMethods.yaha_init_context_on_status_code_and_headers_receive_delegate OnStatusCallback = OnStatus;
    private static readonly NativeMethods.yaha_init_context_on_receive_delegate OnReceiveCallback = OnReceive;
    private static readonly NativeMethods.yaha_init_context_on_complete_delegate OnCompleteCallback = OnComplete;

    private static readonly ConcurrentDictionary<nint, StateRecord> Registry = new();
    private static int _sequence;

    /// <summary>
    /// Invoked at the top of every callback, on the native thread that delivered it. Lets a test
    /// inspect the calling thread. Not thread-affine; set it before starting a request and clear it
    /// afterwards.
    /// </summary>
    public static Action? OnCallbackThreadProbe;

    private readonly YahaNativeRuntimeContext* _runtime;
    private readonly YahaNativeContext* _ctx;
    private readonly List<TrackedRequest> _requests = new();

    public CallbackLifecycleHarness(TimeSpan? connectTimeout = null, bool http2Only = false, bool buildClient = true)
    {
        _runtime = NativeMethods.yaha_init_runtime(2);
        _ctx = NativeMethods.yaha_init_context(_runtime, OnStatusCallback, OnReceiveCallback, OnCompleteCallback);

        if (connectTimeout is { } timeout)
        {
            NativeMethods.yaha_client_config_connect_timeout(_ctx, (ulong)timeout.TotalMilliseconds);
        }
        if (http2Only)
        {
            NativeMethods.yaha_client_config_http2_only(_ctx, true);
        }
        if (buildClient)
        {
            NativeMethods.yaha_build_client(_ctx);
        }
    }

    public TrackedRequest Begin(string uri, bool abortOnReceive = false, bool failOnReceive = false)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var reqCtx = NativeMethods.yaha_request_new(_ctx, seq);

        // A real GCHandle, exactly as RequestContext.Allocate() does, so the IntPtr round-trip
        // through native code is the real thing.
        var record = new StateRecord(seq);
        var handle = GCHandle.Alloc(record);
        var state = GCHandle.ToIntPtr(handle);

        record.Handle = handle;
        record.Ctx = (nint)_ctx;
        record.ReqCtx = (nint)reqCtx;
        record.AbortOnReceive = abortOnReceive;
        record.FailOnReceive = failOnReceive;
        Registry[state] = record;

        SetString(NativeMethods.yaha_request_set_method, reqCtx, "GET");
        if (!SetString(NativeMethods.yaha_request_set_uri, reqCtx, uri))
        {
            throw new InvalidOperationException($"yaha_request_set_uri rejected '{uri}'");
        }
        NativeMethods.yaha_request_set_has_body(_ctx, reqCtx, false);

        var tracked = new TrackedRequest(_ctx, reqCtx, state, record);
        _requests.Add(tracked);

        if (!NativeMethods.yaha_request_begin(_ctx, reqCtx, state))
        {
            throw new InvalidOperationException("yaha_request_begin failed");
        }

        return tracked;
    }

    private delegate bool SetStringFn(YahaNativeContext* ctx, YahaNativeRequestContext* reqCtx, StringBuffer* value);

    private bool SetString(SetStringFn fn, YahaNativeRequestContext* reqCtx, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = bytes)
        {
            var buf = new StringBuffer { ptr = p, length = bytes.Length };
            return fn(_ctx, reqCtx, &buf);
        }
    }

    // --- callbacks -----------------------------------------------------------------------------

    private static StateRecord? Lookup(nint state)
    {
        OnCallbackThreadProbe?.Invoke();
        return Registry.TryGetValue(state, out var record) ? record : null;
    }

    private static void OnStatus(int reqSeq, nint state, int statusCode, YahaHttpVersion version)
    {
        var record = Lookup(state);
        if (record is null) return;

        lock (record.Gate)
        {
            if (record.Released)
            {
                record.Violations.Add("on_status_code_and_headers_receive after release");
                return;
            }
            record.StatusCount++;
            record.Events.Add("status");
        }
    }

    private static void OnReceive(int reqSeq, nint state, nuint length, byte* buf, nuint taskHandle)
    {
        var record = Lookup(state);
        var violated = false;
        var abort = false;
        var fail = false;
        nint ctx = 0, reqCtx = 0;

        if (record is not null)
        {
            lock (record.Gate)
            {
                if (record.Released)
                {
                    record.Violations.Add("on_receive after release");
                    violated = true;
                }
                else
                {
                    record.ReceiveCount++;
                    record.BytesReceived += (int)length;
                    record.Events.Add("receive");
                    abort = record.AbortOnReceive;
                    fail = record.FailOnReceive;
                    ctx = record.Ctx;
                    reqCtx = record.ReqCtx;
                }
            }
        }

        if (!violated && abort)
        {
            // Abort re-entrantly from inside the callback, while the native task is parked on the
            // oneshot. This is the shape of ResponseContext.Cancel() -> RequestContext.TryAbort().
            NativeMethods.yaha_request_abort((YahaNativeContext*)ctx, (YahaNativeRequestContext*)reqCtx);
        }

        // The native task is blocked until this is called - always complete it, even on a
        // violation, or the request can never finish.
        if (!violated && fail)
        {
            var bytes = Encoding.UTF8.GetBytes("test: on_receive reported failure");
            fixed (byte* p = bytes)
            {
                var sb = new StringBuffer { ptr = p, length = bytes.Length };
                NativeMethods.yaha_complete_task(taskHandle, &sb);
            }
        }
        else
        {
            NativeMethods.yaha_complete_task(taskHandle, (StringBuffer*)0);
        }
    }

    private static void OnComplete(int reqSeq, nint state, CompletionReason reason, uint h2ErrorCode)
    {
        var record = Lookup(state);
        if (record is null) return;

        lock (record.Gate)
        {
            if (record.Released)
            {
                record.Violations.Add($"on_complete after release (reason={reason})");
                return;
            }

            record.CompleteCount++;
            record.Reason = reason;
            record.H2ErrorCode = h2ErrorCode;
            record.Events.Add("complete");

            // Mirror NativeHttpHandlerCore.OnComplete -> RequestContext.Release().
            //
            // NOTE: the real code calls GCHandle.Free() here. We only *mark* the state released and
            // defer the Free to Dispose(), so that a stray later callback is recorded as an
            // assertable violation instead of dereferencing a freed handle (which would be
            // undefined behaviour and would crash the test run rather than fail it).
            record.Released = true;
            record.Completed.Set();
        }
    }

    public void Dispose()
    {
        foreach (var request in _requests)
        {
            request.Destroy();
            Registry.TryRemove(request.State, out _);
            if (request.Record.Handle.IsAllocated)
            {
                request.Record.Handle.Free();
            }
        }

        // Only safe because every request above has been destroyed: yaha_dispose_context frees the
        // native context box, so disposing it with a request in flight would be a use-after-free.
        NativeMethods.yaha_dispose_context(_ctx);
        NativeMethods.yaha_dispose_runtime(_runtime);
    }

    internal sealed class StateRecord(int sequence)
    {
        public readonly object Gate = new();
        public readonly ManualResetEventSlim Completed = new(false);
        public readonly List<string> Events = new();
        public readonly List<string> Violations = new();

        public int Sequence { get; } = sequence;
        public GCHandle Handle { get; set; }
        public nint Ctx { get; set; }
        public nint ReqCtx { get; set; }
        public bool AbortOnReceive { get; set; }
        public bool FailOnReceive { get; set; }

        public bool Released { get; set; }
        public int CompleteCount { get; set; }
        public int ReceiveCount { get; set; }
        public int StatusCount { get; set; }
        public int BytesReceived { get; set; }
        public CompletionReason? Reason { get; set; }
        public uint H2ErrorCode { get; set; }
    }

    internal sealed class TrackedRequest(
        YahaNativeContext* ctx,
        YahaNativeRequestContext* reqCtx,
        nint state,
        StateRecord record)
    {
        private readonly YahaNativeContext* _ctx = ctx;
        private readonly YahaNativeRequestContext* _reqCtx = reqCtx;
        private bool _destroyed;

        public nint State { get; } = state;
        public StateRecord Record { get; } = record;

        public void Abort() => NativeMethods.yaha_request_abort(_ctx, _reqCtx);

        public void Destroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            NativeMethods.yaha_request_destroy(_ctx, _reqCtx);
        }

        public bool WaitForReceive(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (Record.Gate)
                {
                    if (Record.ReceiveCount > 0) return true;
                }
                Thread.Sleep(5);
            }
            return false;
        }

        /// <summary>
        /// Asserts the pair of invariants: exactly one <c>on_complete</c>, and no callback after
        /// the state was released.
        /// </summary>
        public void AssertCompletedExactlyOnce(TimeSpan timeout)
        {
            var fired = Record.Completed.Wait(timeout);

            string Events()
            {
                lock (Record.Gate) return string.Join(",", Record.Events);
            }

            Assert.True(fired,
                $"on_complete never fired within {timeout}. The managed finalizer thread would " +
                $"block forever on _fullyCompleted.Wait(). events=[{Events()}]");

            // Give any stray callback a chance to land after the release.
            Thread.Sleep(150);

            lock (Record.Gate)
            {
                Assert.Empty(Record.Violations);
                Assert.Equal(1, Record.CompleteCount);
                Assert.Equal("complete", Record.Events[^1]);
            }
        }
    }
}
