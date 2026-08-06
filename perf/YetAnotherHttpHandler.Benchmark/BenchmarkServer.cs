using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YahaBenchmark;

/// <summary>
/// A minimal in-process Kestrel server used as the peer for every benchmark.
///
/// This is deliberately not <c>HttpClientTestServer</c>: the goal here is to hold the server side as
/// close to constant as possible so the measurement reflects client-handler cost. In particular the
/// download endpoint writes from a pre-allocated buffer rather than allocating per request, which
/// keeps server-side GC out of the numbers.
/// </summary>
internal sealed class BenchmarkServer : IAsyncDisposable
{
    // Response payloads are allocated once and reused, so the server does no per-request allocation
    // beyond what Kestrel itself needs.
    private static readonly byte[] SmallPayload = "__OK__"u8.ToArray();
    private static readonly Dictionary<int, byte[]> DownloadPayloads = new();
    private static readonly object DownloadPayloadsLock = new();

    private readonly WebApplication _app;

    public string BaseUri { get; }

    private BenchmarkServer(WebApplication app, string baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    private static byte[] GetDownloadPayload(int size)
    {
        lock (DownloadPayloadsLock)
        {
            if (!DownloadPayloads.TryGetValue(size, out var payload))
            {
                payload = new byte[size];
                Random.Shared.NextBytes(payload);
                DownloadPayloads[size] = payload;
            }

            return payload;
        }
    }

    public static async Task<BenchmarkServer> LaunchAsync(TransportMode transport)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Kestrel logging would otherwise dominate the console during a run.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        // Kestrel rejects port 0 for ListenLocalhost, so pick a free port up front — the same approach
        // HttpClientTestServer takes. Binding by hostname rather than 127.0.0.1 keeps SNI in play.
        var port = GetUnusedEphemeralPort();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port, listenOptions =>
            {
                listenOptions.Protocols = transport.UsesHttp2() ? HttpProtocols.Http2 : HttpProtocols.Http1;

                if (transport.UsesTls())
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        var certPath = Path.Combine(AppContext.BaseDirectory, "Certificates", "localhost.pfx");
                        httpsOptions.ServerCertificate = new X509Certificate2(certPath);
                    });
                }
            });
        });

        var app = builder.Build();

        // Smallest useful response: isolates fixed per-request overhead.
        app.MapGet("/small", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength = SmallPayload.Length;
            return ctx.Response.Body.WriteAsync(SmallPayload, 0, SmallPayload.Length);
        });

        // Download throughput. Writes a cached buffer, so no server-side allocation per request.
        app.MapGet("/download/{size:int}", (HttpContext ctx, int size) =>
        {
            var payload = GetDownloadPayload(size);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = payload.Length;
            return ctx.Response.Body.WriteAsync(payload, 0, payload.Length);
        });

        // Upload throughput. Drains the request body and acknowledges with a tiny response so the
        // measurement is not also paying for a large download.
        app.MapPost("/upload", async (HttpContext ctx) =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length))) != 0)
                {
                    total += read;
                }

                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(total.ToString());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        await app.StartAsync();

        var scheme = transport.UsesTls() ? "https" : "http";
        return new BenchmarkServer(app, $"{scheme}://localhost:{port}");
    }

    private static int GetUnusedEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
