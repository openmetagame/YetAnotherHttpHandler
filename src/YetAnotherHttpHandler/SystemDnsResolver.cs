using System;
using System.Net;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
#endif

namespace Cysharp.Net.Http
{
    /// <summary>
    /// The default name resolver.
    /// </summary>
    /// <remarks>
    /// On Android, resolves through the currently active <c>Network</c>; everywhere else (and as a
    /// fallback) uses <see cref="Dns.GetHostAddresses(string)"/>.
    /// <para>
    /// This is what <see cref="YetAnotherHttpHandler.OnResolveDns"/> is set to out of the box, so
    /// name resolution runs in managed code by default rather than in the native runtime. Compose
    /// with it rather than replacing it outright when you only need to special-case some hosts:
    /// </para>
    /// <code>
    /// handler.OnResolveDns = host => ResolveSpecialCase(host) ?? SystemDnsResolver.Resolve(host);
    /// </code>
    /// </remarks>
    public static class SystemDnsResolver
    {
        /// <summary>
        /// Resolves <paramref name="host"/>.
        /// </summary>
        /// <returns>
        /// The resolved addresses, or <see langword="null"/> if the host could not be resolved.
        /// </returns>
        /// <remarks>
        /// Called on a background thread, so blocking here is expected and safe.
        /// </remarks>
        public static IPAddress[]? Resolve(string host)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Prefer the active network. See ResolveViaActiveNetwork for why this matters.
            var viaActiveNetwork = ResolveViaActiveNetwork(host);
            if (viaActiveNetwork is { Length: > 0 })
            {
                return viaActiveNetwork;
            }
#endif
            return ResolveViaSystemDns(host);
        }

        private static IPAddress[]? ResolveViaSystemDns(string host)
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

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Resolves through the currently active Android <c>Network</c>, via
        /// <c>ConnectivityManager.getActiveNetwork().getAllByName(host)</c>.
        /// </summary>
        /// <remarks>
        /// <c>getaddrinfo</c> - which is what <see cref="Dns.GetHostAddresses(string)"/> and hyper's
        /// own resolver both reach - resolves against whichever network the <em>process</em> is bound
        /// to. If the app is backgrounded and that network goes away, every later lookup fails with
        /// <c>EAI_NODATA</c> ("No address associated with hostname") until the process re-binds, so
        /// the app returns to the foreground and every request fails.
        /// <para>
        /// Android's own <c>bindProcessToNetwork</c> documentation calls this out and recommends
        /// per-network resolution instead, which is what this does.
        /// </para>
        /// <para>
        /// Returns <see langword="null"/> on any failure, including there being no connectivity at
        /// all, so the caller falls through to the ordinary system resolver.
        /// </para>
        /// </remarks>
        private static IPAddress[]? ResolveViaActiveNetwork(string host)
        {
            // JNI calls are illegal from a thread the JVM does not know about, and this runs on a
            // native runtime thread that Unity never attached. Detaching afterwards matters as much
            // as attaching: the native runtime retires idle threads and creates new ones, so
            // leaving them attached would leak a JVM thread reference per retired thread.
            if (AndroidJNI.AttachCurrentThread() != 0)
            {
                return null;
            }

            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null) return null;

                using var connectivityManager = activity.Call<AndroidJavaObject>("getSystemService", "connectivity");
                if (connectivityManager == null) return null;

                // Deliberately re-fetched on every lookup, never cached. A stale Network handle is
                // the exact failure this method exists to avoid.
                // NOTE: getActiveNetwork() requires API 23; older devices throw and fall through.
                using var network = connectivityManager.Call<AndroidJavaObject>("getActiveNetwork");
                if (network == null) return null; // No connectivity at all.

                // Network.getAllByName is a blocking lookup and must not run on the UI thread. It
                // does not: the native runtime calls us on a background thread.
                var inetAddresses = network.Call<AndroidJavaObject[]>("getAllByName", host);
                if (inetAddresses == null || inetAddresses.Length == 0) return null;

                var addresses = new List<IPAddress>(inetAddresses.Length);
                foreach (var inetAddress in inetAddresses)
                {
                    if (inetAddress == null) continue;
                    try
                    {
                        var bytes = inetAddress.Call<byte[]>("getAddress");
                        if (bytes == null) continue;
                        addresses.Add(ToIPAddress(inetAddress, bytes));
                    }
                    finally
                    {
                        inetAddress.Dispose();
                    }
                }

                if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Trace($"SystemDnsResolver: Resolved '{host}' to {addresses.Count} address(es) via the active network.");
                return addresses.Count == 0 ? null : addresses.ToArray();
            }
            catch (Exception e)
            {
                // An unknown host lands here too (getAllByName throws UnknownHostException), as does
                // any device where the JNI shape differs. Either way the system resolver gets a turn.
                if (YahaEventSource.Log.IsEnabled()) YahaEventSource.Log.Trace($"SystemDnsResolver: Active-network resolution of '{host}' failed: {e.Message}");
                return null;
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        private static IPAddress ToIPAddress(AndroidJavaObject inetAddress, byte[] bytes)
        {
            if (bytes.Length == 16)
            {
                try
                {
                    // Only Inet6Address has getScopeId; Inet4Address throws, which is fine.
                    return new IPAddress(bytes, (uint)inetAddress.Call<int>("getScopeId"));
                }
                catch (Exception)
                {
                    // Fall through to the unscoped form.
                }
            }

            return new IPAddress(bytes);
        }
#endif
    }
}
