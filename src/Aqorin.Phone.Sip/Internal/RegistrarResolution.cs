using System.Net;
using System.Net.Sockets;

namespace Aqorin.Phone.Sip.Internal;

internal sealed class SystemDnsRegistrarResolver : IRegistrarResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            // Our SIP channel is IPv4; prefer A records, keep AAAA as a fallback for the message.
            return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

/// <summary>
/// Sanity checks for the registrar address. Since 2024 <c>.box</c> is a public top-level domain, so a machine
/// whose DNS is not the FRITZ!Box (VPN, custom DNS, DoH) resolves <c>fritz.box</c> to a stranger's server on the
/// internet. Sending REGISTER (and later a digest response) there is both useless and a credential leak.
/// </summary>
internal static class RegistrarResolution
{
    public static bool IsFritzBoxName(string host) =>
        host.Equals("fritz.box", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".fritz.box", StringComparison.OrdinalIgnoreCase)
        || host.Equals("fritz.nas", StringComparison.OrdinalIgnoreCase)
        || host.Equals("fritz.repeater", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 1918, link-local, loopback, CGNAT and IPv6 ULA/link-local/loopback.</summary>
    public static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return IPAddress.IPv6Loopback.Equals(address)
                || (b[0] & 0xFE) == 0xFC      // fc00::/7 unique local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80); // fe80::/10 link local
        }

        return false;
    }

    /// <summary>Picks the address to use, or returns an explanation why none is acceptable.</summary>
    public static (IPAddress? Address, string? Error) Choose(string host, IPAddress[] addresses)
    {
        if (addresses.Length == 0)
        {
            return (null, $"Cannot resolve host {host}. Use the router or PBX LAN IP address if its host name is not resolvable.");
        }

        var ipv4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork || a.IsIPv4MappedToIPv6).Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToArray();
        var candidates = ipv4.Length > 0 ? ipv4 : addresses;

        if (IsFritzBoxName(host) && !candidates.Any(IsPrivateOrLocal))
        {
            var shown = string.Join(", ", candidates.Select(a => a.ToString()));
            return (null,
                $"{host} resolved to the public internet address {shown}, not to your FRITZ!Box. Your computer's DNS is not the " +
                "FRITZ!Box (VPN, custom DNS or DNS-over-HTTPS): '.box' is a public domain since 2024. Enter the FRITZ!Box LAN IP address " +
                "(usually 192.168.178.1) as registrar, or make the FRITZ!Box your DNS server. Nothing was sent.");
        }

        if (ipv4.Length == 0)
        {
            return (null, $"{host} only resolved to IPv6 addresses ({string.Join(", ", addresses)}); the softphone uses IPv4 SIP transports. Enter an IPv4 registrar address instead.");
        }

        // Prefer a private address when several are offered.
        return (candidates.FirstOrDefault(IsPrivateOrLocal) ?? candidates[0], null);
    }
}
