using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Covers <see cref="UnsafeUtilities.IsRunningOnNativeRuntimeThread"/>, which decides whether it is
/// safe to release native handles inline.
///
/// This replaced a <c>GetThreadDescription</c>-based thread-name comparison that only existed on
/// Windows, so on Android/IL2CPP - the platform where releasing handles from a tokio thread is most
/// damaging - the check silently did nothing at all.
/// </summary>
public class RuntimeThreadDetectionTest
{
    [Fact]
    public void ManagedThread_IsNotReportedAsRuntimeThread()
    {
        Assert.False(UnsafeUtilities.IsRunningOnNativeRuntimeThread());
    }

    [Fact]
    public async Task ThreadPoolThread_IsNotReportedAsRuntimeThread()
    {
        // This is where NativeHttpHandlerCore.OnComplete queues RequestContext.Dispose, so it must
        // read as a managed thread.
        Assert.False(await Task.Run(UnsafeUtilities.IsRunningOnNativeRuntimeThread));
    }

    /// <summary>
    /// The load-bearing case: a native callback thread must be reported as a runtime thread.
    /// A callback fires from inside a tokio task, so <c>Handle::try_current()</c> succeeds there.
    /// </summary>
    [Fact]
    public void NativeCallbackThread_IsReportedAsRuntimeThread()
    {
        using var server = RawTestServer.Ok();
        using var harness = new CallbackLifecycleHarness();

        bool? onCallbackThread = null;
        CallbackLifecycleHarness.OnCallbackThreadProbe = () =>
            onCallbackThread ??= UnsafeUtilities.IsRunningOnNativeRuntimeThread();

        try
        {
            var request = harness.Begin(server.BaseUri.ToString());
            request.AssertCompletedExactlyOnce(TimeSpan.FromSeconds(30));
        }
        finally
        {
            CallbackLifecycleHarness.OnCallbackThreadProbe = null;
        }

        Assert.True(onCallbackThread, "a native callback must be detected as running on a runtime thread");
    }

    /// <summary>
    /// Sanity check that the detection is not simply always true or always false, which is how the
    /// old thread-name check failed on non-Windows platforms.
    /// </summary>
    [Fact]
    public void Detection_DistinguishesManagedFromRuntimeThreads()
    {
        using var server = RawTestServer.Ok();
        using var harness = new CallbackLifecycleHarness();

        bool? onCallbackThread = null;
        CallbackLifecycleHarness.OnCallbackThreadProbe = () =>
            onCallbackThread ??= UnsafeUtilities.IsRunningOnNativeRuntimeThread();

        try
        {
            var request = harness.Begin(server.BaseUri.ToString());
            request.AssertCompletedExactlyOnce(TimeSpan.FromSeconds(30));
        }
        finally
        {
            CallbackLifecycleHarness.OnCallbackThreadProbe = null;
        }

        Assert.True(onCallbackThread);
        Assert.False(UnsafeUtilities.IsRunningOnNativeRuntimeThread());
    }
}
