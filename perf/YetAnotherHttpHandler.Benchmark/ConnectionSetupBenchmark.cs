using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace YahaBenchmark;

/// <summary>
/// Cold path: construct a handler, establish a connection (including the TLS handshake) and complete
/// one request, then tear it down.
///
/// This is the scenario where the two handlers are least alike. YetAnotherHttpHandler has to build a
/// native client and a rustls configuration, and the very first handler in the process also starts
/// the shared tokio runtime — that one-off cost lands in the warmup rather than the measurement.
///
/// Deliberately runs few iterations: each operation opens and closes a real connection, and hammering
/// it would just pile up sockets in TIME_WAIT and measure the OS instead of the handlers.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 8, invocationCount: 16)]
public class ConnectionSetupBenchmark
{
    private BenchmarkServer? _server;
    private string _url = default!;

    [Params(TransportMode.Http2Tls, TransportMode.Http1Tls, TransportMode.Http2Cleartext)]
    public TransportMode Transport { get; set; }

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _server = await BenchmarkServer.LaunchAsync(Transport);
        _url = $"{_server.BaseUri}/small";

        // Pay the process-wide one-off costs (tokio runtime startup, JIT, rustls provider init) here
        // so they are not attributed to whichever handler happens to be measured first.
        Func<TransportMode, (HttpClient, HttpMessageHandler)>[] factories =
        [
            HandlerBenchmarkBase.CreateDefaultClient,
            HandlerBenchmarkBase.CreateSocketsClient,
            HandlerBenchmarkBase.CreateYahaClient,
        ];

        foreach (var factory in factories)
        {
            var (client, handler) = factory(Transport);
            using (handler)
            using (client)
            {
                (await client.GetAsync(_url)).EnsureSuccessStatusCode();
            }
        }
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private async Task<int> ColdRequestAsync(Func<TransportMode, (HttpClient Client, HttpMessageHandler Handler)> factory)
    {
        var (client, handler) = factory(Transport);
        using (handler)
        using (client)
        {
            using var response = await client.GetAsync(_url);
            response.EnsureSuccessStatusCode();
            return (int)response.StatusCode;
        }
    }

    [Benchmark(Baseline = true, Description = "HttpClient (default)")]
    public Task<int> Default() => ColdRequestAsync(HandlerBenchmarkBase.CreateDefaultClient);

    [Benchmark(Description = "SocketsHttpHandler")]
    public Task<int> Sockets() => ColdRequestAsync(HandlerBenchmarkBase.CreateSocketsClient);

    [Benchmark(Description = "YetAnotherHttpHandler")]
    public Task<int> Yaha() => ColdRequestAsync(HandlerBenchmarkBase.CreateYahaClient);
}
