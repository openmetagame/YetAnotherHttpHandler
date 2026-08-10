using System.Net;
using System.Net.Sockets;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// A bare TCP server that speaks just enough HTTP/1.1 to drive the teardown paths we care about
/// (stall mid-body, truncate mid-body, never respond). Raw sockets rather than Kestrel, so the
/// test can close the connection at an exact point in the response.
/// </summary>
internal sealed class RawTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<NetworkStream, CancellationToken, Task> _handler;

    public Uri BaseUri { get; }

    private RawTestServer(Func<NetworkStream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await _handler(client.GetStream(), cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Connection torn down by the client; that is the point of most of these tests.
                    }
                }
            }, cancellationToken);
        }
    }

    private static async Task ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var seen = new List<byte>();
        while (!EndOfHead(seen))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            seen.AddRange(buffer[..read]);
        }

        static bool EndOfHead(List<byte> b)
        {
            for (var i = 0; i + 3 < b.Count; i++)
            {
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return true;
            }
            return false;
        }
    }

    private static Task WriteAsync(NetworkStream stream, string s, CancellationToken cancellationToken)
        => stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(s), cancellationToken).AsTask();

    /// <summary>Responds fully and correctly.</summary>
    public static RawTestServer Ok(string body = "hello") => new(async (stream, ct) =>
    {
        await ReadRequestHeadAsync(stream, ct);
        await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}", ct);
        await stream.FlushAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    });

    /// <summary>
    /// Responds and immediately closes, so hyper cannot pool the connection. Use this when a test
    /// needs every request to go through name resolution again.
    /// </summary>
    public static RawTestServer OkClosing(string body = "hello") => new(async (stream, ct) =>
    {
        await ReadRequestHeadAsync(stream, ct);
        await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}", ct);
        await stream.FlushAsync(ct);
        stream.Socket.Shutdown(SocketShutdown.Both);
    });

    /// <summary>Sends headers plus a little body, then stalls with the stream open forever.</summary>
    public static RawTestServer HeadersThenStall() => new(async (stream, ct) =>
    {
        await ReadRequestHeadAsync(stream, ct);
        await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 1048576\r\n\r\n0123456789ABCDEF", ct);
        await stream.FlushAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(120), ct);
    });

    /// <summary>Announces a body then closes the socket part-way through it.</summary>
    public static RawTestServer TruncatedBody() => new(async (stream, ct) =>
    {
        await ReadRequestHeadAsync(stream, ct);
        await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\nshort", ct);
        await stream.FlushAsync(ct);
        await Task.Delay(50, ct);
        stream.Socket.Shutdown(SocketShutdown.Both);
    });

    /// <summary>Accepts the connection and never writes a response.</summary>
    public static RawTestServer NeverResponds() => new(async (stream, ct) =>
    {
        await ReadRequestHeadAsync(stream, ct);
        await Task.Delay(TimeSpan.FromSeconds(120), ct);
    });

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
