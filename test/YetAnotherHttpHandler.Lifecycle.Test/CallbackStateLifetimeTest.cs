using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Covers the lifetime of callback-state <see cref="GCHandle"/>s - the DNS resolver and the server
/// certificate verifier.
///
/// These are handed to the native side as raw <see cref="IntPtr"/>s and dereferenced with
/// <see cref="GCHandle.FromIntPtr"/> every time a callback fires, so freeing one while the native
/// side can still invoke the callback is a use-after-free: dereferencing a freed handle is
/// undefined, and a recycled handle slot can hand back an unrelated object.
///
/// They used to be freed in <c>NativeHttpHandlerCore.Dispose</c>, which is too early - disposing
/// the handler while a request was still connecting left the native side holding a freed handle.
/// Ownership now belongs to <c>YahaContextSafeHandle</c>, whose ref-count already tracks exactly
/// the right lifetime because every in-flight request holds a reference to it.
/// </summary>
public class CallbackStateLifetimeTest
{
    /// <summary>
    /// Allocates a strong <see cref="GCHandle"/> over a fresh object and returns a weak reference to
    /// that object alongside it.
    /// </summary>
    /// <remarks>
    /// Reachability is the only sound way to observe whether the handle was freed.
    /// <see cref="GCHandle.IsAllocated"/> cannot be used: <see cref="GCHandle"/> is a struct, and
    /// <see cref="GCHandle.Free"/> only clears the copy it is called on - the copy held here would
    /// keep reporting <see langword="true"/> long after the handle was released.
    /// <para>
    /// Non-inlined so the target is unreachable from this frame once the method returns.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (GCHandle State, WeakReference Tracker) AllocateTrackedState()
    {
        var target = new object();
        return (GCHandle.Alloc(target), new WeakReference(target));
    }

    private static bool IsStateStillHeld(WeakReference tracker)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return tracker.IsAlive;
    }

    private static unsafe YahaContextSafeHandle CreateContextHandle(YahaRuntimeSafeHandle runtimeHandle, int instanceId)
    {
        var ctx = NativeMethods.yaha_init_context(runtimeHandle.DangerousGet(), null, null, null);
        var contextHandle = new YahaContextSafeHandle(ctx, instanceId);
        contextHandle.SetParent(runtimeHandle);
        return contextHandle;
    }

    /// <summary>
    /// With no outstanding references, disposing the context handle frees the callback state.
    /// </summary>
    [Fact]
    public void CallbackState_IsFreedWhenTheContextHandleIsDisposed()
    {
        var runtimeHandle = NativeRuntime.Instance.Acquire();
        try
        {
            var contextHandle = CreateContextHandle(runtimeHandle, instanceId: 1);

            var (state, tracker) = AllocateTrackedState();
            contextHandle.AddCallbackState(state);
            Assert.True(IsStateStillHeld(tracker), "the callback state should be held while the context is alive");

            contextHandle.Dispose();

            Assert.False(IsStateStillHeld(tracker), "the callback state should be freed once the context is released");
        }
        finally
        {
            NativeRuntime.Instance.Release();
        }
    }

    /// <summary>
    /// The regression test for the actual bug: an in-flight request holds a reference to the context
    /// handle, so disposing the handler must NOT free the callback state yet.
    /// </summary>
    [Fact]
    public void CallbackState_SurvivesDisposeWhileAReferenceIsOutstanding()
    {
        var runtimeHandle = NativeRuntime.Instance.Acquire();
        try
        {
            var contextHandle = CreateContextHandle(runtimeHandle, instanceId: 2);

            var (state, tracker) = AllocateTrackedState();
            contextHandle.AddCallbackState(state);

            // Stands in for an in-flight request: RequestContext holds a reference to the request
            // context handle, which in turn holds one to this handle.
            var addRef = false;
            contextHandle.DangerousAddRef(ref addRef);
            Assert.True(addRef);

            // The owner is finished with the handler...
            contextHandle.Dispose();

            // ...but the native side can still invoke the callback, so the state must stay alive.
            Assert.True(IsStateStillHeld(tracker),
                "the callback state was freed while a reference to the context was still outstanding - " +
                "a native callback would now dereference a freed GCHandle");

            // The request finishes and drops its reference.
            contextHandle.DangerousRelease();

            Assert.False(IsStateStillHeld(tracker),
                "the callback state should be freed once the last reference goes away");
        }
        finally
        {
            NativeRuntime.Instance.Release();
        }
    }

    /// <summary>Several callback states on one context are all released together.</summary>
    [Fact]
    public void MultipleCallbackStates_AreAllFreed()
    {
        var runtimeHandle = NativeRuntime.Instance.Acquire();
        try
        {
            var contextHandle = CreateContextHandle(runtimeHandle, instanceId: 3);

            var (firstState, firstTracker) = AllocateTrackedState();
            var (secondState, secondTracker) = AllocateTrackedState();
            contextHandle.AddCallbackState(firstState);
            contextHandle.AddCallbackState(secondState);

            contextHandle.Dispose();

            Assert.False(IsStateStillHeld(firstTracker));
            Assert.False(IsStateStillHeld(secondTracker));
        }
        finally
        {
            NativeRuntime.Instance.Release();
        }
    }

    /// <summary>
    /// End to end: disposing a handler that has both callbacks registered, while a request is still
    /// running, must not disturb it.
    /// </summary>
    [Fact]
    public async Task DisposingTheHandlerMidFlight_LeavesInFlightRequestsWorking()
    {
        using var server = RawTestServer.HeadersThenStall();

        var handler = new YetAnotherHttpHandler
        {
            OnResolveDns = _ => [System.Net.IPAddress.Loopback],
            SkipCertificateVerification = true,
        };
        var client = new HttpClient(handler);

        var response = await client.GetAsync(
            $"http://yaha-callback-state.invalid:{server.BaseUri.Port}/",
            HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadAsync(new byte[8]);

        // Dispose out from under the live exchange. Previously this freed both callback states.
        handler.Dispose();
        client.Dispose();

        // The request still terminates cleanly rather than crashing on a freed handle.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            while (await stream.ReadAsync(new byte[1024]) > 0) { }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
