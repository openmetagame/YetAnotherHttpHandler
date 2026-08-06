using BenchmarkDotNet.Attributes;

namespace YahaBenchmark;

/// <summary>
/// Response-body throughput. The body is streamed with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
/// and drained into a single reused buffer, so the numbers reflect how fast each handler can move
/// bytes to the caller rather than how fast it can allocate a byte[].
/// </summary>
public class DownloadBenchmark : HandlerBenchmarkBase
{
    private readonly byte[] _buffer = new byte[81920];
    private string _url = default!;

    /// <summary>
    /// 64 KiB is a couple of HTTP/2 windows; 8 MiB is large enough that flow control and buffer
    /// management dominate over per-request overhead.
    /// </summary>
    [Params(64 * 1024, 1024 * 1024, 8 * 1024 * 1024)]
    public int ResponseSize { get; set; }

    protected override Task OnSetupAsync()
    {
        _url = $"{BaseUri}/download/{ResponseSize}";
        return Task.CompletedTask;
    }

    private async Task<long> DownloadAsync(HttpClient client)
    {
        using var response = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(_buffer)) != 0)
        {
            total += read;
        }

        // Guard against a silently truncated body making one handler look faster than it is.
        if (total != ResponseSize)
        {
            throw new InvalidOperationException($"Expected {ResponseSize} bytes but read {total}.");
        }

        return total;
    }

    [Benchmark(Baseline = true, Description = "HttpClient (default)")]
    public Task<long> Default() => DownloadAsync(DefaultClient);

    [Benchmark(Description = "SocketsHttpHandler")]
    public Task<long> Sockets() => DownloadAsync(SocketsClient);

    [Benchmark(Description = "YetAnotherHttpHandler")]
    public Task<long> Yaha() => DownloadAsync(YahaClient);
}
