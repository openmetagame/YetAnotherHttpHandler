using BenchmarkDotNet.Attributes;

namespace YahaBenchmark;

/// <summary>
/// Fixed per-request overhead: a 6-byte response over a warm connection.
///
/// This is the headline comparison — at this payload size almost all of the measured time is handler
/// bookkeeping (request construction, header encoding, and for YetAnotherHttpHandler the
/// managed/native boundary), not I/O.
/// </summary>
public class SmallResponseBenchmark : HandlerBenchmarkBase
{
    private string _url = default!;

    protected override Task OnSetupAsync()
    {
        _url = $"{BaseUri}/small";
        return Task.CompletedTask;
    }

    private async Task<int> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync(_url);
        return (await response.Content.ReadAsByteArrayAsync()).Length;
    }

    [Benchmark(Baseline = true, Description = "HttpClient (default)")]
    public Task<int> Default() => GetAsync(DefaultClient);

    [Benchmark(Description = "SocketsHttpHandler")]
    public Task<int> Sockets() => GetAsync(SocketsClient);

    [Benchmark(Description = "YetAnotherHttpHandler")]
    public Task<int> Yaha() => GetAsync(YahaClient);
}
