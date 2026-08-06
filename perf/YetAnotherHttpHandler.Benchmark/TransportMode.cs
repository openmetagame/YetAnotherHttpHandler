namespace YahaBenchmark;

/// <summary>
/// The wire configurations the benchmarks compare the two handlers over.
/// </summary>
public enum TransportMode
{
    /// <summary>HTTP/1.1 over TLS.</summary>
    Http1Tls,

    /// <summary>HTTP/2 over TLS, negotiated by ALPN.</summary>
    Http2Tls,

    /// <summary>HTTP/2 cleartext (h2c), prior knowledge. Isolates protocol cost from TLS cost.</summary>
    Http2Cleartext,
}

internal static class TransportModeExtensions
{
    public static bool UsesTls(this TransportMode mode)
        => mode is TransportMode.Http1Tls or TransportMode.Http2Tls;

    public static bool UsesHttp2(this TransportMode mode)
        => mode is TransportMode.Http2Tls or TransportMode.Http2Cleartext;
}
