using System.Net;
using System.Net.Sockets;

namespace api;

/// <summary>
/// Decides whether an address is a public internet destination. Anything private, local,
/// reserved, documentation-only or platform-internal is rejected, including IPv6 forms that
/// embed such an IPv4 address.
/// </summary>
public static class PublicAddressPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length))
            return false;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork when length == 4 => IsPublicIPv4(bytes[..4]),
            AddressFamily.InterNetworkV6 when length == 16 => IsPublicIPv6(bytes),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(ReadOnlySpan<byte> b) => !(
        b[0] == 0 ||                                        // 0.0.0.0/8 "this network"
        b[0] == 10 ||                                       // 10.0.0.0/8 private
        b[0] == 100 && (b[1] & 0xC0) == 64 ||               // 100.64.0.0/10 carrier-grade NAT
        b[0] == 127 ||                                      // 127.0.0.0/8 loopback
        b[0] == 169 && b[1] == 254 ||                       // 169.254.0.0/16 link-local, including IMDS
        b[0] == 172 && (b[1] & 0xF0) == 16 ||               // 172.16.0.0/12 private
        b[0] == 192 && b[1] == 0 && b[2] == 0 ||            // 192.0.0.0/24 IETF protocol assignments
        b[0] == 192 && b[1] == 0 && b[2] == 2 ||            // 192.0.2.0/24 documentation
        b[0] == 192 && b[1] == 88 && b[2] == 99 ||          // 192.88.99.0/24 deprecated 6to4 relay
        b[0] == 192 && b[1] == 168 ||                       // 192.168.0.0/16 private
        b[0] == 198 && (b[1] & 0xFE) == 18 ||               // 198.18.0.0/15 benchmarking
        b[0] == 198 && b[1] == 51 && b[2] == 100 ||         // 198.51.100.0/24 documentation
        b[0] == 203 && b[1] == 0 && b[2] == 113 ||          // 203.0.113.0/24 documentation
        b[0] >= 224 ||                                      // multicast, reserved and broadcast
        b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16); // Azure WireServer

    private static bool IsPublicIPv6(ReadOnlySpan<byte> b)
    {
        // ::ffff:0:0/96 IPv4-mapped.
        if (b[..10].IndexOfAnyExcept((byte)0) < 0 && b[10] == 0xFF && b[11] == 0xFF)
            return IsPublicIPv4(b[12..]);

        // ::/96 covers ::, ::1 and deprecated IPv4-compatible addresses; none are public destinations.
        if (b[..12].IndexOfAnyExcept((byte)0) < 0)
            return false;

        // 64:ff9b::/96 well-known NAT64 prefix embeds the IPv4 destination in the last 32 bits.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].IndexOfAnyExcept((byte)0) < 0)
            return IsPublicIPv4(b[12..]);

        // Only global unicast (2000::/3) can be public. This excludes 64:ff9b:1::/48 local-use NAT64,
        // 100::/64 discard, fc00::/7 ULA, fe80::/10 link-local, fec0::/10 site-local and ff00::/8 multicast.
        if ((b[0] & 0xE0) != 0x20)
            return false;

        if (b[0] == 0x20 && b[1] == 0x01)
        {
            // 2001::/32 Teredo: check the server and the de-obfuscated client IPv4 addresses.
            if (b[2] == 0x00 && b[3] == 0x00)
            {
                Span<byte> client = [(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]];
                return IsPublicIPv4(b[4..8]) && IsPublicIPv4(client);
            }

            if (b[2] == 0x0D && b[3] == 0xB8)                    // 2001:db8::/32 documentation
                return false;
            if (b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) // 2001:2::/48 benchmarking
                return false;
            if (b[2] == 0x00 && (b[3] & 0xF0) is 0x10 or 0x20)    // 2001:10::/28 and 2001:20::/28 ORCHID
                return false;
        }

        // 2002::/16 6to4 embeds the IPv4 address in bits 16-47.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsPublicIPv4(b[2..6]);

        // 3fff::/20 documentation.
        if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00)
            return false;

        return true;
    }
}
