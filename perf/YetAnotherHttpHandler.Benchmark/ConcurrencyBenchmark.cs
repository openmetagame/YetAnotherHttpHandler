using BenchmarkDotNet.Attributes;

namespace YahaBenchmark;

/// <summary>
/// Behaviour under concurrent in-flight requests. For HTTP/2 this exercises stream multiplexing over
/// one connection; for HTTP/1.1 it exercises the connection pool.
///
/// One operation is a batch of <see cref="Concurrency"/> requests, so the absolute times are
/// per-batch, not per-request — <c>OperationsPerInvoke</c> cannot vary with a parameter. The Ratio
/// column is the number to read, since both handlers run identical batches.
/// </summary>
public class ConcurrencyBenchmark : HandlerBenchmarkBase
{
    private string _url = default!;
    private Task<HttpResponseMessage>[] _pending = default!;

    [Params(8, 64)]
    public int Concurrency { get; set; }

    protected override Task OnSetupAsync()
    {
        _url = $"{BaseUri}/small";
        _pending = new Task<HttpResponseMessage>[Concurrency];
        return Task.CompletedTask;
    }

    private async Task RunBatchAsync(HttpClient client)
    {
        for (var i = 0; i < _pending.Length; i++)
        {
            _pending[i] = client.GetAsync(_url);
        }

        for (var i = 0; i < _pending.Length; i++)
        {
            using var response = await _pending[i];
            response.EnsureSuccessStatusCode();
        }
    }

    [Benchmark(Baseline = true, Description = "HttpClient (default)")]
    public Task Default() => RunBatchAsync(DefaultClient);

    [Benchmark(Description = "SocketsHttpHandler")]
    public Task Sockets() => RunBatchAsync(SocketsClient);

    [Benchmark(Description = "YetAnotherHttpHandler")]
    public Task Yaha() => RunBatchAsync(YahaClient);
}
