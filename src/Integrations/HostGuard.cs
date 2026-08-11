using System.Net;
using System.Net.Sockets;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The SSRF guard for the outbound connectors that take a customer-supplied host (today the SQL connector).
/// A connection target is only allowed to resolve to a public, internet-reachable address; any host that
/// resolves to a loopback, private (RFC 1918), link-local, carrier-grade-NAT, unique-local (IPv6 fc00::/7),
/// IPv4-mapped-IPv6, or the cloud-metadata endpoint (169.254.169.254) is rejected. This stops a tenant from
/// pointing the cloud worker at its own internal network or the VM's instance-metadata service.
///
/// The cloud worker targets an internet-reachable DB so the guard always applies there; the desktop build
/// runs the same connectors locally where there is no SSRF surface, so a caller can opt out (the desktop
/// host wires <see cref="AllowPrivate"/> on). The guard resolves the host and inspects EVERY resolved
/// address - a hostile name that resolves to a private address is rejected even if the literal text looked
/// public. It never embeds the host into a connection string; callers pass the validated host components to
/// a driver ConnectionStringBuilder so the host can never inject extra connection parameters.
/// </summary>
public static class HostGuard
{
    /// <summary>Process-wide opt-out for the desktop build (no SSRF surface when the worker runs on the
    /// user's own machine against a local DB). Defaults to false so the cloud worker is guarded by default;
    /// the desktop host sets this true at start-up. The cloud worker never sets it.</summary>
    public static bool AllowPrivate { get; set; }

    /// <summary>Validate a customer-supplied host: it must be non-blank and resolve only to public addresses
    /// (unless <see cref="AllowPrivate"/> is on for the desktop build). Throws an
    /// <see cref="InvalidOperationException"/> whose message never echoes back any resolved IP literal, so a
    /// probe cannot use the error text to map the internal network. A literal IP host is validated directly
    /// (no DNS); a name is resolved and every returned address is checked.</summary>
    public static void EnsurePublicHost(string? host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("A database host is required.");

        host = host.Trim();
        if (AllowPrivate) return; // desktop: local DB, no SSRF surface

        // strip an IPv6 literal's brackets ("[::1]" -> "::1") so IPAddress.TryParse can read it.
        string bare = host is ['[', .. var inner, ']'] ? inner : host;

        IPAddress[] addresses;
        if (IPAddress.TryParse(bare, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                throw new InvalidOperationException("The database host could not be resolved.");
            }
            if (addresses.Length == 0)
                throw new InvalidOperationException("The database host did not resolve to any address.");
        }

        foreach (var addr in addresses)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPublic(addr))
                throw new InvalidOperationException(
                    "The database host is not allowed: it resolves to a private, loopback, link-local or metadata address.");
        }
    }

    /// <summary>True when an address is a public, internet-routable unicast address (i.e. NOT loopback,
    /// private, link-local, CGNAT, unique-local, multicast, unspecified, IPv4-mapped-IPv6, or the cloud
    /// metadata endpoint). Exposed for direct unit testing of the classification.</summary>
    public static bool IsPublic(IPAddress address)
    {
        // unwrap an IPv4-mapped IPv6 address ("::ffff:10.0.0.1") so the v4 rules below apply to it - a probe
        // must not bypass the v4 private ranges by wrapping the address in IPv6.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;          // 127.0.0.0/8, ::1

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();                 // 4 bytes, network order

            if (b[0] == 0) return false;                          // 0.0.0.0/8 "this network" / unspecified
            if (b[0] == 10) return false;                         // 10.0.0.0/8 private
            if (b[0] == 127) return false;                        // 127.0.0.0/8 loopback (defensive)
            if (b[0] == 169 && b[1] == 254) return false;         // 169.254.0.0/16 link-local + 169.254.169.254 metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16.0.0/12 private
            if (b[0] == 192 && b[1] == 168) return false;         // 192.168.0.0/16 private
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // 100.64.0.0/10 carrier-grade NAT
            if (b[0] >= 224) return false;                        // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
            // DELIBERATE divergence from the stricter cloud guard: the documentation/benchmark ranges
            // (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 TEST-NET-1/2/3 and 198.18.0.0/15) are NOT rejected
            // here. They are non-routable-but-not-internal (a host pointed at them cannot reach a tenant's
            // private network or the metadata service), so blocking them buys no SSRF protection; the SQL guard
            // only has to keep a connection off the internal network. This divergence is intentional and
            // reviewable - if a future requirement is to mirror the cloud guard exactly, add the three /24s and
            // the /15 above.
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return false;            // fe80::/10
            if (address.IsIPv6SiteLocal) return false;            // fec0::/10 (deprecated)
            if (address.IsIPv6Multicast) return false;            // ff00::/8
            if (address.IsIPv6UniqueLocal) return false;          // fc00::/7 unique-local
            if (address.Equals(IPAddress.IPv6Any)) return false;  // :: unspecified
            return true;
        }

        return false; // an unknown address family is not provably public - reject
    }
}
