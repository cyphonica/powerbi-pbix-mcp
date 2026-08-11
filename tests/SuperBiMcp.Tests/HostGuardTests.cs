using System.Net;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Direct coverage of the SSRF guard the SQL connector relies on. Public, internet-routable addresses pass;
/// every loopback / private / link-local / unique-local / carrier-grade-NAT / cloud-metadata address is
/// rejected. The classification (<see cref="HostGuard.IsPublic"/>) is asserted per address family, and the
/// host-level entry point (<see cref="HostGuard.EnsurePublicHost"/>) is asserted on literal IP hosts so no
/// DNS is needed and the tests stay hermetic.
/// </summary>
public sealed class HostGuardTests
{
    public HostGuardTests() => HostGuard.AllowPrivate = false; // cloud-default for every case here

    // ---- public addresses pass -----------------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8")]            // Google DNS
    [InlineData("1.1.1.1")]            // Cloudflare DNS
    [InlineData("203.0.113.7")]        // TEST-NET-3 documentation but classified public (not in a private range)
    [InlineData("2606:4700:4700::1111")] // Cloudflare IPv6
    public void IsPublic_AllowsRoutableAddresses(string ip)
        => Assert.True(HostGuard.IsPublic(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("192.0.2.1")]          // TEST-NET-1 (RFC 5737 documentation)
    [InlineData("198.51.100.10")]      // TEST-NET-2 (RFC 5737 documentation)
    [InlineData("203.0.113.200")]      // TEST-NET-3 (RFC 5737 documentation)
    [InlineData("198.18.0.1")]         // 198.18.0.0/15 (RFC 2544 benchmarking)
    [InlineData("198.19.255.255")]     // 198.18.0.0/15 high end
    public void IsPublic_AllowsDocumentationAndBenchmarkRanges_ByDeliberateDesign(string ip)
        // DELIBERATE divergence from the stricter cloud guard (documented in HostGuard.IsPublic): the
        // documentation / benchmark ranges are non-routable-but-not-internal, so the SQL SSRF guard does not
        // reject them - blocking them buys no protection against reaching a tenant's private network. This test
        // pins that decision so the divergence is intentional and reviewable.
        => Assert.True(HostGuard.IsPublic(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void EnsurePublicHost_AllowsRoutableLiteralHost(string ip)
        => HostGuard.EnsurePublicHost(ip); // does not throw

    // ---- the required reject list ---------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]          // IPv4 loopback
    [InlineData("10.0.0.5")]           // 10/8 private
    [InlineData("192.168.1.10")]       // 192.168/16 private
    [InlineData("172.16.4.4")]         // 172.16/12 private (low end)
    [InlineData("172.31.255.255")]     // 172.16/12 private (high end)
    [InlineData("169.254.169.254")]    // cloud metadata + link-local
    [InlineData("169.254.0.1")]        // link-local
    [InlineData("100.64.0.1")]         // carrier-grade NAT
    [InlineData("0.0.0.0")]            // unspecified / this-network
    [InlineData("::1")]                // IPv6 loopback
    [InlineData("fc00::1")]            // unique-local fc00::/7
    [InlineData("fd12:3456:789a::1")]  // unique-local (fd00::/8)
    [InlineData("fe80::1")]            // IPv6 link-local
    public void IsPublic_RejectsPrivateAndMetadata(string ip)
        => Assert.False(HostGuard.IsPublic(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.4.4")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("[::1]")]              // bracketed IPv6 literal form (as it would arrive from a host param)
    [InlineData("fc00::1")]
    public void EnsurePublicHost_RejectsPrivateLiteralHost(string ip)
        => Assert.Throws<InvalidOperationException>(() => HostGuard.EnsurePublicHost(ip));

    [Fact]
    public void EnsurePublicHost_RejectsIPv4MappedPrivateAddress()
    {
        // a probe must not bypass the v4 private ranges by wrapping the address as IPv4-mapped IPv6.
        var mapped = IPAddress.Parse("10.0.0.5").MapToIPv6();
        Assert.False(HostGuard.IsPublic(mapped));
    }

    [Fact]
    public void EnsurePublicHost_RejectsBlankHost()
    {
        Assert.Throws<InvalidOperationException>(() => HostGuard.EnsurePublicHost(null));
        Assert.Throws<InvalidOperationException>(() => HostGuard.EnsurePublicHost("   "));
    }

    [Fact]
    public void EnsurePublicHost_ErrorNeverLeaksTheResolvedAddress()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HostGuard.EnsurePublicHost("169.254.169.254"));
        Assert.DoesNotContain("169.254.169.254", ex.Message); // a probe cannot read the IP back from the error
    }

    [Fact]
    public void AllowPrivate_OptsOutForTheDesktopBuild()
    {
        HostGuard.AllowPrivate = true;
        try
        {
            HostGuard.EnsurePublicHost("127.0.0.1"); // desktop: local DB, no SSRF surface - must not throw
            HostGuard.EnsurePublicHost("10.0.0.1");
        }
        finally
        {
            HostGuard.AllowPrivate = false;
        }
    }
}
