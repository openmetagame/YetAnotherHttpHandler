using System;
using System.Net;
using System.Net.Sockets;

namespace Cysharp.Net.Http
{
    /// <summary>
    /// Managed-side helpers for the generated <see cref="YahaSocketAddress"/> interop struct.
    /// </summary>
    internal unsafe partial struct YahaSocketAddress
    {
        /// <summary>
        /// <see cref="family"/> value marking <see cref="address"/> as a 4-byte IPv4 address.
        /// Must match <c>YAHA_ADDRESS_FAMILY_IPV4</c> in <c>native/yaha_native/src/resolver.rs</c>.
        /// </summary>
        public const int AddressFamilyIPv4 = 4;

        /// <summary>
        /// <see cref="family"/> value marking <see cref="address"/> as a 16-byte IPv6 address.
        /// Must match <c>YAHA_ADDRESS_FAMILY_IPV6</c> in <c>native/yaha_native/src/resolver.rs</c>.
        /// </summary>
        public const int AddressFamilyIPv6 = 6;

        /// <summary>
        /// Size of <see cref="address"/> in bytes.
        /// </summary>
        private const int AddressLength = 16;

        /// <summary>
        /// Writes <paramref name="value"/> into this entry.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> if the address family has no meaning to a TCP connector, in which
        /// case this entry is left untouched and should be skipped.
        /// </returns>
        public bool TrySetAddress(IPAddress? value)
        {
            if (value is null) return false;

            uint scopeId;
            int addressFamily;
            switch (value.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    addressFamily = AddressFamilyIPv4;
                    scopeId = 0;
                    break;
                case AddressFamily.InterNetworkV6:
                    addressFamily = AddressFamilyIPv6;
                    // Link-local addresses are meaningless without their interface index.
                    scopeId = (uint)value.ScopeId;
                    break;
                default:
                    return false;
            }

            var bytes = value.GetAddressBytes();
            if (bytes.Length > AddressLength) return false;

            family = addressFamily;
            scope_id = scopeId;
            fixed (byte* destination = address)
            {
                var span = new Span<byte>(destination, AddressLength);
                span.Clear();
                bytes.AsSpan().CopyTo(span);
            }

            return true;
        }
    }
}
