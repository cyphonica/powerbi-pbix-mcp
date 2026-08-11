using System.Net.Http;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Shared HttpClient factory for the connectors. Every connector talks to a third-party origin (a customer's
/// WooCommerce store, Shopify, Google, Microsoft, ...) and many sit behind Cloudflare or a WAF whose Browser
/// Integrity Check rejects requests with no User-Agent (HTTP 403 "Just a moment" - even with Bot Fight Mode
/// off). An identifying User-Agent clears that default check, and because it names DaxOps a store owner with a
/// stricter WAF can allowlist it by name (you cannot allowlist a faked browser UA).
/// </summary>
internal static class HttpDefaults
{
    /// <summary>The identifying User-Agent every connector request carries.</summary>
    public const string UserAgent = "DaxOps/1.0 (+https://daxops.com)";

    /// <summary>A long-lived HttpClient (never one-per-request) with the DaxOps User-Agent set.</summary>
    public static HttpClient New()
    {
        // .NET's default SocketsHttpHandler produces a TLS ClientHello fingerprint (JA3) that Cloudflare and
        // many WAFs flag as a bot - HTTP 403 even with a valid User-Agent. WinHttpHandler uses the Windows
        // native WinHTTP/SChannel TLS stack, whose fingerprint is the standard Windows one and is not
        // challenged (verified on a Cloudflare-fronted store: SocketsHttpHandler -> 403, WinHttpHandler -> 200).
        // The engine runs on Windows; fall back to the default handler on any other OS.
        HttpMessageHandler handler = OperatingSystem.IsWindows() ? new WinHttpHandler() : new SocketsHttpHandler();
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    /// <summary>As <see cref="New()"/> but over a supplied handler (used by the transport-swap connectors).</summary>
    public static HttpClient New(HttpMessageHandler handler)
    {
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }
}
