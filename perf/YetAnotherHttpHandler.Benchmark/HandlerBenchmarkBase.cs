using System.Net;
using BenchmarkDotNet.Attributes;
using Cysharp.Net.Http;

namespace YahaBenchmark;

/// <summary>
/// Shared setup for the handler comparison benchmarks.
///
/// Every arm is an <see cref="HttpClient"/> — only the handler underneath it differs:
/// <list type="bullet">
/// <item><description><see cref="HttpClientHandler"/> — what plain <c>new HttpClient()</c> uses. This is the baseline.</description></item>
/// <item><description><see cref="SocketsHttpHandler"/> — the lower-level .NET stack that <see cref="HttpClientHandler"/> wraps.</description></item>
/// <item><description><see cref="Cysharp.Net.Http.YetAnotherHttpHandler"/> — this library.</description></item>
/// </list>
/// Keeping the first two separate shows whether the <see cref="HttpClientHandler"/> shim costs
/// anything, so a reader cannot mistake wrapper overhead for a difference in the native stack.
///
/// All three clients are created once per parameter set in <see cref="GlobalSetupAsync"/> and warmed
/// up with a real request, so the measured operations reflect steady-state per-request cost over an
/// already-established connection. See <see cref="ConnectionSetupBenchmark"/> for the cold path.
/// </summary>
[MemoryDiagnoser]
public abstract class HandlerBenchmarkBase
{
    private BenchmarkServer? _server;
    private HttpMessageHandler? _defaultHandler;
    private HttpMessageHandler? _socketsHandler;
    private HttpMessageHandler? _yahaHandler;

    /// <summary>
    /// HTTP/2 over TLS is listed first: it is the configuration this library targets, and the one to
    /// read if you only read one row. HTTP/1.1-over-TLS and h2c are kept for contrast — h2c isolates
    /// protocol cost from TLS cost.
    /// </summary>
    [Params(TransportMode.Http2Tls, TransportMode.Http1Tls, TransportMode.Http2Cleartext)]
    public TransportMode Transport { get; set; }

    /// <summary>An <see cref="HttpClient"/> over the default <see cref="HttpClientHandler"/>.</summary>
    protected HttpClient DefaultClient { get; private set; } = default!;

    /// <summary>An <see cref="HttpClient"/> over <see cref="SocketsHttpHandler"/>.</summary>
    protected HttpClient SocketsClient { get; private set; } = default!;

    /// <summary>An <see cref="HttpClient"/> over <see cref="Cysharp.Net.Http.YetAnotherHttpHandler"/>.</summary>
    protected HttpClient YahaClient { get; private set; } = default!;

    protected string BaseUri => _server!.BaseUri;

    /// <summary>
    /// Issued against every client during setup so the connection, TLS session and JIT are warm.
    /// </summary>
    protected virtual string WarmupPath => "/small";

    /// <summary>
    /// Runs after the server and all clients are ready. Derived benchmarks precompute request URLs
    /// and payloads here. This is a plain virtual rather than another <c>[GlobalSetup]</c> because
    /// BenchmarkDotNet would otherwise discover and invoke both the base and derived attributed
    /// methods.
    /// </summary>
    protected virtual Task OnSetupAsync() => Task.CompletedTask;

    [GlobalSetup]
    public async Task GlobalSetupAsync()
    {
        _server = await BenchmarkServer.LaunchAsync(Transport);

        (DefaultClient, _defaultHandler) = CreateDefaultClient(Transport);
        (SocketsClient, _socketsHandler) = CreateSocketsClient(Transport);
        (YahaClient, _yahaHandler) = CreateYahaClient(Transport);

        // Warm up every path. Failing here is far easier to diagnose than a misattributed timing.
        var clients = new[]
        {
            ("HttpClient (HttpClientHandler)", DefaultClient),
            ("SocketsHttpHandler", SocketsClient),
            ("YetAnotherHttpHandler", YahaClient),
        };

        foreach (var (name, client) in clients)
        {
            using var response = await client.GetAsync($"{BaseUri}{WarmupPath}");
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{name} warmup returned {(int)response.StatusCode} for {WarmupPath} over {Transport}.");
            }

            // A silent downgrade to HTTP/1.1 would make the comparison meaningless, so fail loudly.
            var expected = Transport.UsesHttp2() ? HttpVersion.Version20 : HttpVersion.Version11;
            if (response.Version != expected)
            {
                throw new InvalidOperationException(
                    $"{name} negotiated HTTP/{response.Version} but {Transport} requires HTTP/{expected}.");
            }
        }

        await OnSetupAsync();
    }

    [GlobalCleanup]
    public async Task GlobalCleanupAsync()
    {
        DefaultClient?.Dispose();
        SocketsClient?.Dispose();
        YahaClient?.Dispose();
        _defaultHandler?.Dispose();
        _socketsHandler?.Dispose();
        _yahaHandler?.Dispose();

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    /// <summary>
    /// The out-of-the-box .NET client: <c>new HttpClient()</c> uses <see cref="HttpClientHandler"/>.
    /// It is constructed explicitly here only to accept the server's self-signed certificate.
    /// </summary>
    internal static (HttpClient Client, HttpMessageHandler Handler) CreateDefaultClient(TransportMode transport)
    {
        var handler = new HttpClientHandler();

        if (transport.UsesTls())
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return (ConfigureVersion(new HttpClient(handler), transport), handler);
    }

    internal static (HttpClient Client, HttpMessageHandler Handler) CreateSocketsClient(TransportMode transport)
    {
        var handler = new SocketsHttpHandler();

        if (transport.UsesTls())
        {
            // The benchmark server uses a self-signed certificate. Every handler skips verification so
            // none pays for chain building the others avoid.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return (ConfigureVersion(new HttpClient(handler), transport), handler);
    }

    internal static (HttpClient Client, HttpMessageHandler Handler) CreateYahaClient(TransportMode transport)
    {
        var handler = new YetAnotherHttpHandler
        {
            SkipCertificateVerification = transport.UsesTls(),
            // Required for h2c (prior knowledge); for Http2Tls it pins the ALPN outcome.
            Http2Only = transport.UsesHttp2(),
        };

        return (new HttpClient(handler), handler);
    }

    /// <summary>
    /// h2c has no ALPN to negotiate with, so the version must be requested exactly. Setting it for
    /// every mode keeps protocol selection directly comparable across the .NET handlers.
    /// </summary>
    private static HttpClient ConfigureVersion(HttpClient client, TransportMode transport)
    {
        client.DefaultRequestVersion = transport.UsesHttp2() ? HttpVersion.Version20 : HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }
}
