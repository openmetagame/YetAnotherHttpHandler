using System.Net;
using Cysharp.Net.Http;

namespace _YetAnotherHttpHandler.Lifecycle.Test;

/// <summary>
/// Covers <see cref="YetAnotherHttpHandler.OnResolveDns"/> (external name resolution) and
/// <see cref="YetAnotherHttpHandler.DnsCacheFallbackDuration"/> (serving the last known good
/// address when a lookup fails).
///
/// Every test resolves a name that genuinely cannot be resolved by the platform - <c>.invalid</c> is
/// reserved by RFC 2606 - so a request only succeeds if our resolver is actually driving the
/// connection.
/// </summary>
public class DnsResolverTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds a client that cannot pool connections, so every request re-resolves. Without this,
    /// hyper reuses the first connection and later lookups never happen.
    /// </summary>
    private static HttpClient CreateClient(
        DnsResolutionHandler resolver,
        TimeSpan? cacheFallback = null)
    {
        var handler = new YetAnotherHttpHandler
        {
            OnResolveDns = resolver,
            MaxIdlePerHost = 0,
            PoolIdleTimeout = TimeSpan.FromMilliseconds(1),
        };
        if (cacheFallback is { } fallback)
        {
            handler.DnsCacheFallbackDuration = fallback;
        }

        return new HttpClient(handler) { Timeout = Timeout };
    }

    private static Uri UriFor(RawTestServer server, string host = "yaha-resolver-test.invalid")
        => new($"http://{host}:{server.BaseUri.Port}/");

    [Fact]
    public async Task ExternalResolver_IsUsedInsteadOfThePlatformResolver()
    {
        using var server = RawTestServer.OkClosing("resolved");

        var resolvedHosts = new List<string>();
        using var client = CreateClient(host =>
        {
            lock (resolvedHosts) resolvedHosts.Add(host);
            return [IPAddress.Loopback];
        });

        var body = await client.GetStringAsync(UriFor(server));

        Assert.Equal("resolved", body);
        Assert.Contains("yaha-resolver-test.invalid", resolvedHosts);
    }

    /// <summary>
    /// The handler receives a bare host name: no scheme, no port, and lower-cased.
    /// </summary>
    /// <remarks>
    /// hyper normalises the authority before resolving, which is correct - host names are
    /// case-insensitive - but worth pinning down, because a handler that keys its own cache or an
    /// allow-list on the argument will only ever see the lower-cased form.
    /// </remarks>
    [Fact]
    public async Task ExternalResolver_ReceivesTheBareLowercasedHost()
    {
        using var server = RawTestServer.OkClosing();

        string? seen = null;
        using var client = CreateClient(host =>
        {
            seen = host;
            return [IPAddress.Loopback];
        });

        await client.GetStringAsync(UriFor(server, "Some-Host.invalid"));

        Assert.Equal("some-host.invalid", seen);
    }

    [Fact]
    public async Task ExternalResolver_ReturningNull_FailsTheRequest()
    {
        using var server = RawTestServer.OkClosing();
        using var client = CreateClient(_ => null, cacheFallback: TimeSpan.Zero);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(UriFor(server)));
        Assert.Contains("dns", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalResolver_ReturningEmpty_FailsTheRequest()
    {
        using var server = RawTestServer.OkClosing();
        using var client = CreateClient(_ => [], cacheFallback: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(UriFor(server)));
    }

    /// <summary>
    /// A throwing handler must be reported as a failed lookup, never allowed to unwind into Rust -
    /// an exception crossing an <c>extern "C"</c> boundary aborts the process.
    /// </summary>
    [Fact]
    public async Task ExternalResolver_Throwing_FailsTheRequestWithoutCrashing()
    {
        using var server = RawTestServer.OkClosing();
        using var client = CreateClient(
            _ => throw new InvalidOperationException("resolver blew up"),
            cacheFallback: TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(UriFor(server)));

        // The process is still healthy: a second client with a working resolver succeeds.
        using var working = CreateClient(_ => [IPAddress.Loopback]);
        Assert.Equal("hello", await working.GetStringAsync(UriFor(server)));
    }

    /// <summary>
    /// More than one address must survive marshalling, in order, so the connector can fail over.
    /// The server only listens on 127.0.0.1, so 127.0.0.2 refuses instantly and the second address
    /// is what actually carries the request.
    /// </summary>
    [Fact]
    public async Task ExternalResolver_MultipleAddresses_FallsOverToAReachableOne()
    {
        using var server = RawTestServer.OkClosing("failover");

        using var client = CreateClient(_ =>
        [
            IPAddress.Parse("127.0.0.2"),
            IPAddress.Loopback,
        ]);

        Assert.Equal("failover", await client.GetStringAsync(UriFor(server)));
    }

    // --- cache fallback ---------------------------------------------------------------------

    /// <summary>
    /// The headline behaviour: once a host has resolved successfully, a later failure serves the
    /// remembered address instead of failing the request. This is what keeps an Android app working
    /// after it resumes from the background onto a stale network binding, where every
    /// <c>getaddrinfo</c> call fails with <c>EAI_NODATA</c>.
    /// </summary>
    [Fact]
    public async Task CacheFallback_ServesTheLastKnownGoodAddressWhenResolutionFails()
    {
        using var server = RawTestServer.OkClosing("from-cache");

        var failing = false;
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            Interlocked.Increment(ref calls);
            return failing ? null : [IPAddress.Loopback];
        });

        // Prime the cache.
        Assert.Equal("from-cache", await client.GetStringAsync(UriFor(server)));

        // Now every lookup fails, exactly as it would after the app returns from the background.
        failing = true;

        Assert.Equal("from-cache", await client.GetStringAsync(UriFor(server)));
        Assert.Equal("from-cache", await client.GetStringAsync(UriFor(server)));
        Assert.True(calls >= 2, "the resolver should have been consulted again before the cache was used");
    }

    [Fact]
    public async Task CacheFallback_Disabled_FailsWhenResolutionFails()
    {
        using var server = RawTestServer.OkClosing();

        var failing = false;
        using var client = CreateClient(
            _ => failing ? null : [IPAddress.Loopback],
            cacheFallback: TimeSpan.Zero);

        Assert.Equal("hello", await client.GetStringAsync(UriFor(server)));

        failing = true;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(UriFor(server)));
    }

    [Fact]
    public async Task CacheFallback_ExpiredEntry_IsNotUsed()
    {
        using var server = RawTestServer.OkClosing();

        var failing = false;
        using var client = CreateClient(
            _ => failing ? null : [IPAddress.Loopback],
            cacheFallback: TimeSpan.FromMilliseconds(250));

        Assert.Equal("hello", await client.GetStringAsync(UriFor(server)));

        failing = true;
        await Task.Delay(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync(UriFor(server)));
    }

    [Fact]
    public async Task CacheFallback_IsPerHost()
    {
        using var server = RawTestServer.OkClosing();

        var failing = false;
        using var client = CreateClient(_ => failing ? null : [IPAddress.Loopback]);

        // Only host A is ever resolved successfully.
        Assert.Equal("hello", await client.GetStringAsync(UriFor(server, "host-a.invalid")));

        failing = true;

        // A is served from cache; B was never cached and must still fail.
        Assert.Equal("hello", await client.GetStringAsync(UriFor(server, "host-a.invalid")));
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetStringAsync(UriFor(server, "host-b.invalid")));
    }

    // --- the default option value -----------------------------------------------------------

    /// <summary>
    /// Resolution runs in managed code out of the box: <see cref="YetAnotherHttpHandler.OnResolveDns"/>
    /// defaults to <see cref="SystemDnsResolver.Resolve"/> rather than being null.
    /// </summary>
    [Fact]
    public void OnResolveDns_DefaultsToTheManagedResolver()
    {
        using var handler = new YetAnotherHttpHandler();

        Assert.NotNull(handler.OnResolveDns);
        Assert.Equal<DnsResolutionHandler>(SystemDnsResolver.Resolve, handler.OnResolveDns!);
    }

    [Fact]
    public async Task DefaultConfiguration_ResolvesAndConnects()
    {
        using var server = RawTestServer.OkClosing("default-ok");

        var handler = new YetAnotherHttpHandler
        {
            MaxIdlePerHost = 0,
            PoolIdleTimeout = TimeSpan.FromMilliseconds(1),
            // OnResolveDns left at its default.
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        Assert.Equal("default-ok", await client.GetStringAsync($"http://localhost:{server.BaseUri.Port}/"));
    }

    /// <summary>Setting the resolver to null hands resolution back to the native runtime.</summary>
    [Fact]
    public async Task OnResolveDns_SetToNull_UsesTheNativeResolver()
    {
        using var server = RawTestServer.OkClosing("native-ok");

        var handler = new YetAnotherHttpHandler
        {
            OnResolveDns = null,
            MaxIdlePerHost = 0,
            PoolIdleTimeout = TimeSpan.FromMilliseconds(1),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        Assert.Equal("native-ok", await client.GetStringAsync($"http://localhost:{server.BaseUri.Port}/"));

        // And an unresolvable name still fails cleanly on that path.
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetStringAsync($"http://yaha-native-path.invalid:{server.BaseUri.Port}/"));
    }

    [Fact]
    public void SystemDnsResolver_ReturnsNullForAnUnresolvableHost()
    {
        Assert.Null(SystemDnsResolver.Resolve("yaha-system-resolver.invalid"));
    }

    [Fact]
    public void SystemDnsResolver_ResolvesLoopback()
    {
        var addresses = SystemDnsResolver.Resolve("localhost");

        Assert.NotNull(addresses);
        Assert.NotEmpty(addresses!);
        Assert.All(addresses!, a => Assert.True(IPAddress.IsLoopback(a)));
    }

    /// <summary>
    /// The connector short-circuits IP literals, so a resolver is never consulted for them.
    /// </summary>
    [Fact]
    public async Task IpLiteralUri_DoesNotInvokeTheResolver()
    {
        using var server = RawTestServer.OkClosing("literal");

        var calls = 0;
        using var client = CreateClient(_ =>
        {
            Interlocked.Increment(ref calls);
            return [IPAddress.Loopback];
        });

        Assert.Equal("literal", await client.GetStringAsync($"http://127.0.0.1:{server.BaseUri.Port}/"));
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// The cache is populated by whichever resolver produced the addresses, including the native
    /// one, so the fallback works even with <see cref="YetAnotherHttpHandler.OnResolveDns"/> unset.
    /// </summary>
    [Fact]
    public async Task CacheFallback_AppliesToTheNativeResolverAsWell()
    {
        using var server = RawTestServer.OkClosing("localhost-ok");

        var handler = new YetAnotherHttpHandler
        {
            OnResolveDns = null,
            MaxIdlePerHost = 0,
            PoolIdleTimeout = TimeSpan.FromMilliseconds(1),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        var body = await client.GetStringAsync($"http://localhost:{server.BaseUri.Port}/");
        Assert.Equal("localhost-ok", body);
    }

    // --- address marshalling ----------------------------------------------------------------

    [Fact]
    public void TrySetAddress_RoundTripsIPv4()
    {
        var entry = default(YahaSocketAddress);
        Assert.True(entry.TrySetAddress(IPAddress.Parse("203.0.113.7")));

        Assert.Equal(YahaSocketAddress.AddressFamilyIPv4, entry.family);
        Assert.Equal(0u, entry.scope_id);
        Assert.Equal(new byte[] { 203, 0, 113, 7 }, ReadAddress(ref entry, 4));
    }

    [Fact]
    public void TrySetAddress_RoundTripsIPv6IncludingScopeId()
    {
        var entry = default(YahaSocketAddress);
        var address = IPAddress.Parse("fe80::1%7");
        Assert.True(entry.TrySetAddress(address));

        Assert.Equal(YahaSocketAddress.AddressFamilyIPv6, entry.family);
        Assert.Equal(7u, entry.scope_id);
        Assert.Equal(address.GetAddressBytes(), ReadAddress(ref entry, 16));
    }

    [Fact]
    public void TrySetAddress_ClearsStaleBytesBetweenUses()
    {
        var entry = default(YahaSocketAddress);
        Assert.True(entry.TrySetAddress(IPAddress.Parse("::ffff:ffff:ffff:ffff")));

        // Reusing the slot for a shorter IPv4 address must not leave IPv6 bytes behind.
        Assert.True(entry.TrySetAddress(IPAddress.Parse("10.0.0.1")));
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, ReadAddress(ref entry, 4));
        Assert.Equal(new byte[12], ReadAddress(ref entry, 16).Skip(4).ToArray());
    }

    [Fact]
    public void TrySetAddress_RejectsNullAndUnsupportedFamilies()
    {
        var entry = default(YahaSocketAddress);
        Assert.False(entry.TrySetAddress(null));
        Assert.Equal(0, entry.family);
    }

    private static unsafe byte[] ReadAddress(ref YahaSocketAddress entry, int length)
    {
        fixed (byte* p = entry.address)
        {
            return new ReadOnlySpan<byte>(p, length).ToArray();
        }
    }
}
