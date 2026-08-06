using BenchmarkDotNet.Attributes;

namespace YahaBenchmark;

/// <summary>
/// Request-body throughput. The server drains the body and replies with a short acknowledgement, so
/// the measurement is not also paying for a large download.
/// </summary>
public class UploadBenchmark : HandlerBenchmarkBase
{
    private byte[] _payload = default!;
    private string _url = default!;

    [Params(1024, 1024 * 1024, 8 * 1024 * 1024)]
    public int RequestSize { get; set; }

    protected override Task OnSetupAsync()
    {
        _url = $"{BaseUri}/upload";
        _payload = new byte[RequestSize];
        Random.Shared.NextBytes(_payload);
        return Task.CompletedTask;
    }

    private async Task<string> UploadAsync(HttpClient client)
    {
        // ByteArrayContent over the preallocated payload: no per-operation copy of the request body,
        // and it gives both handlers a known Content-Length rather than chunked/streamed framing.
        using var content = new ByteArrayContent(_payload);
        using var response = await client.PostAsync(_url, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Benchmark(Baseline = true, Description = "HttpClient (default)")]
    public Task<string> Default() => UploadAsync(DefaultClient);

    [Benchmark(Description = "SocketsHttpHandler")]
    public Task<string> Sockets() => UploadAsync(SocketsClient);

    [Benchmark(Description = "YetAnotherHttpHandler")]
    public Task<string> Yaha() => UploadAsync(YahaClient);
}
