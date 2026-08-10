using System;
using System.Net;

namespace Cysharp.Net.Http
{
    /// <summary>
    /// The default name resolver: <see cref="Dns.GetHostAddresses(string)"/>.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="YetAnotherHttpHandler.OnResolveDns"/> is set to out of the box, so
    /// name resolution runs in managed code by default rather than in the native runtime.
    /// <para>
    /// Both paths ultimately reach the platform resolver, so this is not by itself a behavioural
    /// change. What it buys is a single place to observe, wrap or replace resolution - including on
    /// platforms where the correct resolver is only reachable from managed code, such as Android's
    /// per-<c>Network</c> lookup. Compose with it rather than replacing it outright when you only
    /// need to special-case some hosts:
    /// </para>
    /// <code>
    /// handler.OnResolveDns = host => ResolveSpecialCase(host) ?? SystemDnsResolver.Resolve(host);
    /// </code>
    /// </remarks>
    public static class SystemDnsResolver
    {
        /// <summary>
        /// Resolves <paramref name="host"/> using the platform resolver.
        /// </summary>
        /// <returns>
        /// The resolved addresses, or <see langword="null"/> if the host could not be resolved.
        /// </returns>
        /// <remarks>
        /// Called on a background thread, so blocking here is expected and safe.
        /// </remarks>
        public static IPAddress[]? Resolve(string host)
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                return addresses.Length == 0 ? null : addresses;
            }
            catch (Exception e)
            {
                // A failed lookup is an ordinary outcome, not an error to propagate: returning null
                // lets the caller fall back to a cached address (see
                // YetAnotherHttpHandler.DnsCacheFallbackDuration) before the request fails.
                if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Trace($"SystemDnsResolver: Failed to resolve '{host}': {e.Message}");
                return null;
            }
        }
    }
}
