using System.Text.Json.Nodes;

namespace SuperBiMcp.Agent;

/// <summary>
/// The engine's view of "is the configured Anthropic key alive?" - one process-wide status record updated
/// by every real AI call (prompter, ask-your-data) and by the periodic self-test, plus the failure
/// classifier that decides WHY a call failed. Snapshot() gives a host a monitorable health block -
/// booleans and a coarse reason only, never key material, never spend figures.
///
/// Failure classes the operator can act on:
///   billing  - Anthropic credit balance exhausted / billing problem  -> top up the Anthropic account
///   auth     - key revoked/invalid or permission denied              -> rotate SUPERBI_ANTHROPIC_KEY
///   rate     - 429/529, transient                                    -> usually self-heals
///   network  - engine could not reach api.anthropic.com              -> host egress problem
///   api      - Anthropic 5xx or malformed response                   -> Anthropic-side incident
/// </summary>
public static class AiHealth
{
    public enum FailureClass { None, Billing, Auth, Rate, Network, Api }

    private static readonly object _lock = new();
    private static bool? _healthy;                 // null = no AI call has run yet this process
    private static FailureClass _failure = FailureClass.None;
    private static DateTimeOffset? _checkedAt;
    private static string _source = "";            // "call" (a real customer call) | "selftest"

    public static void RecordSuccess(string source)
    {
        lock (_lock) { _healthy = true; _failure = FailureClass.None; _checkedAt = DateTimeOffset.UtcNow; _source = source; }
    }

    public static void RecordFailure(string source, FailureClass cls)
    {
        // A transient class (rate/network/api) must not mask a standing billing/auth failure the operator
        // has not fixed yet - billing/auth only clears on a SUCCESS.
        lock (_lock)
        {
            bool standing = _healthy == false && (_failure is FailureClass.Billing or FailureClass.Auth);
            bool incoming = cls is FailureClass.Billing or FailureClass.Auth;
            if (standing && !incoming) return;
            _healthy = false; _failure = cls; _checkedAt = DateTimeOffset.UtcNow; _source = source;
        }
    }

    /// <summary>
    /// Classify an Anthropic Messages API failure. Pure - unit-testable. statusCode 0 = the request never
    /// got an HTTP response (DNS/socket/timeout).
    /// </summary>
    public static FailureClass Classify(int statusCode, string? errorType, string? message)
    {
        if (statusCode == 0) return FailureClass.Network;
        string msg = message ?? "";
        // Out-of-credit is a 400 invalid_request_error whose message names the credit balance; an
        // account-level billing problem is a 403 billing_error. Both mean: top up / fix billing.
        if (string.Equals(errorType, "billing_error", StringComparison.OrdinalIgnoreCase)) return FailureClass.Billing;
        if (statusCode == 400 && msg.Contains("credit balance", StringComparison.OrdinalIgnoreCase)) return FailureClass.Billing;
        if (statusCode == 401) return FailureClass.Auth;
        if (statusCode == 403) return FailureClass.Auth;
        if (statusCode is 429 or 529) return FailureClass.Rate;
        if (statusCode >= 500) return FailureClass.Api;
        return FailureClass.Api;
    }

    /// <summary>The /health "ai" block. Coarse by design: booleans + a reason word. No secrets, no totals.</summary>
    public static JsonObject Snapshot(bool configured)
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["configured"] = configured,
                ["healthy"] = !configured ? false : (_healthy is bool b ? b : (bool?)null),
                ["reason"] = !configured ? "unconfigured" : (_healthy == false ? _failure.ToString().ToLowerInvariant() : null),
                ["checkedAt"] = _checkedAt?.ToString("o"),
                ["source"] = _checkedAt != null ? _source : null,
            };
        }
    }

    /// <summary>Test hook: reset to the never-called state.</summary>
    internal static void ResetForTests()
    {
        lock (_lock) { _healthy = null; _failure = FailureClass.None; _checkedAt = null; _source = ""; }
    }
}

/// <summary>
/// A classified Anthropic Messages API failure. PublicMessage is the customer-safe text a handler may echo;
/// Message keeps the real detail for the operator log. The raw Anthropic error (which can name the account's
/// credit balance) must never reach a customer response.
/// </summary>
public sealed class AnthropicApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorType { get; }
    public AiHealth.FailureClass Failure { get; }

    public string PublicMessage => Failure switch
    {
        AiHealth.FailureClass.Rate => "The AI builder is busy right now - please try again in a minute.",
        _ => "The AI builder is temporarily unavailable. Please try again shortly - your credits were not charged.",
    };

    public AnthropicApiException(int statusCode, string? errorType, string message)
        : base($"Anthropic {(statusCode == 0 ? "network" : statusCode.ToString())}: {message}")
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        Failure = AiHealth.Classify(statusCode, errorType, message);
    }
}
