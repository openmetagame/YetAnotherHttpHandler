using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Invariant tests over the raw native callback contract, observed through the real
/// reverse-P/Invoke marshalling. Each test asserts:
/// (a) <c>on_complete</c> fires exactly once, and (b) no callback arrives with a released state.
/// </summary>
public class NativeCallbackLifecycleTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void Success_CompletesExactlyOnce()
    {
        using var server = RawTestServer.Ok();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Success, request.Record.Reason);
        Assert.Equal(1, request.Record.StatusCount);
    }

    /// <summary>
    /// DNS resolution failure. <c>.invalid</c> is reserved by RFC 2606 and can never resolve, so
    /// this needs no network. The failure must surface as exactly one <c>on_complete(Error)</c>.
    /// </summary>
    [Fact]
    public void DnsResolutionFailure_CompletesExactlyOnce()
    {
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin("http://yaha-lifecycle-nonexistent.invalid/");
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Error, request.Record.Reason);
        Assert.Equal(0, request.Record.StatusCount);
        Assert.Equal(0, request.Record.ReceiveCount);
    }

    /// <summary>A failed DNS lookup that is also aborted must still complete exactly once.</summary>
    [Fact]
    public void DnsResolutionFailure_WithConcurrentAbort_CompletesExactlyOnce()
    {
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin("http://yaha-lifecycle-nonexistent-2.invalid/");
        request.Abort();
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.True(request.Record.Reason is CompletionReason.Error or CompletionReason.Aborted);
    }

    /// <summary>Connect failure/timeout against a non-routable address (RFC 5737 TEST-NET-1).</summary>
    [Fact]
    public void ConnectTimeout_CompletesExactlyOnce()
    {
        using var harness = new CallbackLifecycleHarness(connectTimeout: TimeSpan.FromSeconds(1));

        var request = harness.Begin("http://192.0.2.1:81/");
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Error, request.Record.Reason);
        Assert.Equal(0, request.Record.StatusCount);
    }

    [Fact]
    public void ConnectionRefused_CompletesExactlyOnce()
    {
        Uri baseUri;
        using (var closed = RawTestServer.Ok())
        {
            baseUri = closed.BaseUri;
        }

        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(baseUri.ToString());
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Error, request.Record.Reason);
    }

    /// <summary>Cancellation after headers and part of the body have arrived.</summary>
    [Fact]
    public void CancelMidResponse_CompletesExactlyOnce()
    {
        using var server = RawTestServer.HeadersThenStall();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        Assert.True(request.WaitForReceive(Timeout), "server did not start streaming the body");

        request.Abort();
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Aborted, request.Record.Reason);
    }

    /// <summary>
    /// Abort raised from inside <c>on_receive</c>, while the native task is parked waiting for
    /// <c>yaha_complete_task</c>.
    /// </summary>
    [Fact]
    public void AbortWhileReceiving_CompletesExactlyOnce()
    {
        using var server = RawTestServer.HeadersThenStall();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString(), abortOnReceive: true);
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Aborted, request.Record.Reason);
        Assert.True(request.Record.ReceiveCount >= 1);
    }

    /// <summary>The managed side reports a write failure back through <c>yaha_complete_task</c>.</summary>
    [Fact]
    public void ReceiveFailure_CompletesExactlyOnce()
    {
        using var server = RawTestServer.HeadersThenStall();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString(), failOnReceive: true);
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Error, request.Record.Reason);
        Assert.Equal(1, request.Record.ReceiveCount);
    }

    /// <summary>Server closes the connection part-way through an announced body.</summary>
    [Fact]
    public void ServerResetMidStream_CompletesExactlyOnce()
    {
        using var server = RawTestServer.TruncatedBody();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        request.AssertCompletedExactlyOnce(Timeout);

        Assert.Equal(CompletionReason.Error, request.Record.Reason);
    }

    /// <summary>
    /// Dispose-while-in-flight at the request-handle level: abort then <c>yaha_request_destroy</c>,
    /// in the same order as <c>RequestContext.Dispose</c>. The in-flight task holds its own Arc
    /// clone, so destroying the handle must not stop <c>on_complete</c> from firing.
    /// </summary>
    [Fact]
    public void DestroyRequestWhileInFlight_StillCompletesExactlyOnce()
    {
        using var server = RawTestServer.NeverResponds();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        Thread.Sleep(200);

        request.Abort();
        request.Destroy();

        request.AssertCompletedExactlyOnce(Timeout);
        Assert.Equal(CompletionReason.Aborted, request.Record.Reason);
    }

    [Fact]
    public void AbortBeforeResponse_CompletesExactlyOnce()
    {
        using var server = RawTestServer.NeverResponds();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        Thread.Sleep(200);
        request.Abort();

        request.AssertCompletedExactlyOnce(Timeout);
        Assert.Equal(CompletionReason.Aborted, request.Record.Reason);
        Assert.Equal(0, request.Record.StatusCount);
    }

    /// <summary>Aborting repeatedly, including after completion, must not produce a second callback.</summary>
    [Fact]
    public void RepeatedAbort_DoesNotProduceCallbackWithReleasedState()
    {
        using var server = RawTestServer.HeadersThenStall();
        using var harness = new CallbackLifecycleHarness();

        var request = harness.Begin(server.BaseUri.ToString());
        Thread.Sleep(200);
        request.Abort();
        request.Abort();

        request.AssertCompletedExactlyOnce(Timeout);

        // Abort after completion: the managed state is released by now, so any callback here
        // would be a use-after-free of the GCHandle.
        request.Abort();
        Thread.Sleep(200);

        lock (request.Record.Gate)
        {
            Assert.Empty(request.Record.Violations);
            Assert.Equal(1, request.Record.CompleteCount);
        }
    }

    /// <summary>Many concurrent requests aborted at staggered points.</summary>
    [Fact]
    public void ConcurrentAborts_EachCompleteExactlyOnce()
    {
        using var server = RawTestServer.HeadersThenStall();
        using var harness = new CallbackLifecycleHarness();

        var requests = Enumerable.Range(0, 16)
            .Select(_ => harness.Begin(server.BaseUri.ToString()))
            .ToList();

        for (var i = 0; i < requests.Count; i++)
        {
            Thread.Sleep(i % 5);
            requests[i].Abort();
        }

        foreach (var request in requests)
        {
            request.AssertCompletedExactlyOnce(Timeout);
            Assert.True(request.Record.Reason is CompletionReason.Aborted or CompletionReason.Error);
        }
    }
}
