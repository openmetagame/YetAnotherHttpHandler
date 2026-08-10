using System.Runtime.CompilerServices;
using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Verifies the third lifecycle property: <b>no finalizer hang</b>.
///
/// <c>~RequestContext</c> blocks on <c>_fullyCompleted.Wait()</c> (RequestContext.cs:372), and that
/// event is only ever set by <c>RequestContext.Release()</c>, which only runs from
/// <c>NativeHttpHandlerCore.OnComplete</c>. So if any terminal path fails to deliver
/// <c>on_complete</c>, the CLR finalizer thread wedges permanently - which stops <i>all</i>
/// finalization process-wide, not just this handler's.
///
/// Each test abandons requests without disposing them, so <c>~RequestContext</c> genuinely runs,
/// then asserts the finalizer queue drains within a timeout.
/// </summary>
[Collection(nameof(FinalizerLifecycleTest))]
[CollectionDefinition(nameof(FinalizerLifecycleTest), DisableParallelization = true)]
public class FinalizerLifecycleTest
{
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Forces finalization on a watchdog thread. If <c>~RequestContext</c> is blocked on
    /// <c>_fullyCompleted.Wait()</c> this never returns, and the wait times out.
    /// </summary>
    private static void AssertFinalizationCompletes()
    {
        var drained = Task.Run(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
        });

        Assert.True(
            drained.Wait(FinalizationTimeout),
            $"The finalizer queue did not drain within {FinalizationTimeout}. " +
            "~RequestContext is blocked on _fullyCompleted.Wait(), which means on_complete was " +
            "never delivered for an abandoned request. In Unity this wedges the finalizer thread " +
            "for the lifetime of the process.");
    }

    /// <summary>
    /// Starts a request and abandons every reference to it without disposing, so the
    /// RequestContext becomes eligible for finalization while the exchange is still in flight.
    /// Kept non-inlined so the locals are genuinely unreachable on return.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartAndAbandon(Uri uri, Action<YetAnotherHttpHandler>? configure = null, int settleMs = 300)
    {
        var handler = new YetAnotherHttpHandler();
        configure?.Invoke(handler);

        var client = new HttpClient(handler);
        // Fire and forget: never awaited, never disposed.
        _ = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);

        Thread.Sleep(settleMs);
    }

    [Fact]
    public void AbandonedRequest_StalledMidResponse_DoesNotHangFinalizer()
    {
        using var server = RawTestServer.HeadersThenStall();
        StartAndAbandon(server.BaseUri);
        AssertFinalizationCompletes();
    }

    [Fact]
    public void AbandonedRequest_ServerNeverResponds_DoesNotHangFinalizer()
    {
        using var server = RawTestServer.NeverResponds();
        StartAndAbandon(server.BaseUri);
        AssertFinalizationCompletes();
    }

    [Fact]
    public void AbandonedRequest_ServerResetMidStream_DoesNotHangFinalizer()
    {
        using var server = RawTestServer.TruncatedBody();
        StartAndAbandon(server.BaseUri);
        AssertFinalizationCompletes();
    }

    [Fact]
    public void AbandonedRequest_DnsResolutionFailure_DoesNotHangFinalizer()
    {
        StartAndAbandon(new Uri("http://yaha-lifecycle-finalizer.invalid/"), settleMs: 1000);
        AssertFinalizationCompletes();
    }

    [Fact]
    public void AbandonedRequest_ConnectTimeout_DoesNotHangFinalizer()
    {
        StartAndAbandon(
            new Uri("http://192.0.2.1:81/"),
            handler => handler.ConnectTimeout = TimeSpan.FromSeconds(1),
            settleMs: 300);
        AssertFinalizationCompletes();
    }

    // --- observable outcomes: the request must terminate, not hang -------------------------------

    [Fact]
    public async Task CancelMidResponse_FaultsAndDoesNotHangFinalizer()
    {
        using var server = RawTestServer.HeadersThenStall();

        await RunAsync();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task RunAsync()
        {
            using var handler = new YetAnotherHttpHandler();
            using var client = new HttpClient(handler);
            using var cts = new CancellationTokenSource();

            var response = await client.GetAsync(server.BaseUri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var stream = await response.Content.ReadAsStreamAsync(cts.Token);

            // Read the first chunk so we are genuinely mid-body, then cancel.
            var buffer = new byte[8];
            await stream.ReadAsync(buffer, cts.Token);
            cts.Cancel();

            // NOTE: the exception type here is racy by design, and either outcome is correct.
            // ResponseContext.Cancel() completes the response pipe with an
            // OperationCanceledException, but it also calls TryAbort(), and the resulting
            // on_complete may arrive as CompletionReason.Error (the connection was torn down
            // mid-body) rather than Aborted - in which case CompleteAsFailed wins the race and the
            // consumer sees an IOException instead. What matters for this test is that the read
            // terminates rather than hanging, and that finalization still drains afterwards.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                while (await stream.ReadAsync(new byte[1024], CancellationToken.None) > 0) { }
            });
        }
    }

    [Fact]
    public async Task ServerResetMidStream_ThrowsAndDoesNotHangFinalizer()
    {
        using var server = RawTestServer.TruncatedBody();

        await RunAsync();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task RunAsync()
        {
            using var handler = new YetAnotherHttpHandler();
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var response = await client.GetAsync(server.BaseUri, HttpCompletionOption.ResponseHeadersRead);
                await response.Content.ReadAsByteArrayAsync();
            });
        }
    }

    [Fact]
    public async Task DnsResolutionFailure_ThrowsAndDoesNotHangFinalizer()
    {
        await RunAsync();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task RunAsync()
        {
            using var handler = new YetAnotherHttpHandler();
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => client.GetAsync("http://yaha-lifecycle-dns-throw.invalid/"));
        }
    }

    [Fact]
    public async Task ConnectTimeout_ThrowsAndDoesNotHangFinalizer()
    {
        await RunAsync();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task RunAsync()
        {
            using var handler = new YetAnotherHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(1) };
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => client.GetAsync("http://192.0.2.1:81/"));
        }
    }

    /// <summary>
    /// Disposing the handler while a request is still in flight. The native context box is freed by
    /// <c>yaha_dispose_context</c>, so the only thing standing between this and a use-after-free on
    /// the tokio worker thread is the SafeHandle ref-count that <c>RequestContext</c> holds. This
    /// asserts the request still terminates and the finalizer still drains.
    /// </summary>
    [Fact]
    public async Task DisposeHandlerWhileRequestInFlight_DoesNotHangOrCrash()
    {
        using var server = RawTestServer.HeadersThenStall();

        await RunAsync();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task RunAsync()
        {
            var handler = new YetAnotherHttpHandler();
            var client = new HttpClient(handler);

            var response = await client.GetAsync(server.BaseUri, HttpCompletionOption.ResponseHeadersRead);
            var stream = await response.Content.ReadAsStreamAsync();
            await stream.ReadAsync(new byte[8]);

            // Dispose out from under the in-flight exchange.
            client.Dispose();
            handler.Dispose();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                while (await stream.ReadAsync(new byte[1024]) > 0) { }
            });
        }
    }

    /// <summary>
    /// Many overlapping abandoned requests: the finalizer must still drain. This is the closest
    /// analogue of a gRPC channel doing socket I/O on background threads while the app forces a
    /// <c>GC.Collect()</c>.
    /// </summary>
    [Fact]
    public void ManyAbandonedRequests_DoNotHangFinalizer()
    {
        using var server = RawTestServer.HeadersThenStall();

        StartMany();
        AssertFinalizationCompletes();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void StartMany()
        {
            var handler = new YetAnotherHttpHandler();
            var client = new HttpClient(handler);

            for (var i = 0; i < 16; i++)
            {
                _ = client.GetAsync(server.BaseUri, HttpCompletionOption.ResponseHeadersRead);
            }

            Thread.Sleep(500);
        }
    }
}
