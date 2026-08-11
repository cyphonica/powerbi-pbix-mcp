using SuperBiMcp.Agent;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The AI-key watchdog's failure classifier + the /health "ai" snapshot. The operator alert email picks its
/// remediation hint from the class, so billing must never be mistaken for a transient, and a transient
/// (rate/network/api) must never CLEAR a standing billing/auth outage the operator has not fixed yet.
/// These tests share one process-wide AiHealth record, so they run serially in one class.
/// </summary>
public sealed class AiHealthTests
{
    // -------- classification --------

    [Fact]
    public void OutOfCredit_400_IsBilling()
    {
        Assert.Equal(AiHealth.FailureClass.Billing,
            AiHealth.Classify(400, "invalid_request_error",
                "Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."));
    }

    [Fact]
    public void BillingErrorType_IsBilling_RegardlessOfStatus()
    {
        Assert.Equal(AiHealth.FailureClass.Billing, AiHealth.Classify(403, "billing_error", "billing problem"));
    }

    [Fact]
    public void RevokedKey_401_IsAuth()
    {
        Assert.Equal(AiHealth.FailureClass.Auth, AiHealth.Classify(401, "authentication_error", "invalid x-api-key"));
    }

    [Fact]
    public void Forbidden_403_IsAuth()
    {
        Assert.Equal(AiHealth.FailureClass.Auth, AiHealth.Classify(403, "permission_error", "not permitted"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public void RateAndOverload_AreRate(int status)
    {
        Assert.Equal(AiHealth.FailureClass.Rate, AiHealth.Classify(status, "rate_limit_error", "slow down"));
    }

    [Fact]
    public void ServerError_IsApi()
    {
        Assert.Equal(AiHealth.FailureClass.Api, AiHealth.Classify(500, "api_error", "boom"));
    }

    [Fact]
    public void NoHttpResponse_IsNetwork()
    {
        Assert.Equal(AiHealth.FailureClass.Network, AiHealth.Classify(0, null, "connection refused"));
    }

    [Fact]
    public void OrdinaryBadRequest_IsApi_NotBilling()
    {
        // a malformed request must not page the operator about billing
        Assert.Equal(AiHealth.FailureClass.Api, AiHealth.Classify(400, "invalid_request_error", "max_tokens must be positive"));
    }

    // -------- the exception's customer-facing text --------

    [Fact]
    public void PublicMessage_NeverLeaksTheAnthropicError()
    {
        var ex = new AnthropicApiException(400, "invalid_request_error", "Your credit balance is too low to access the Anthropic API.");
        Assert.Equal(AiHealth.FailureClass.Billing, ex.Failure);
        Assert.DoesNotContain("credit balance", ex.PublicMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anthropic", ex.PublicMessage);           // provider stays invisible to customers
        Assert.Contains("credit balance", ex.Message);                  // the operator log keeps the real detail
    }

    // -------- the /health snapshot + standing-failure rule --------

    [Fact]
    public void Snapshot_Unconfigured_ReportsUnhealthy()
    {
        AiHealth.ResetForTests();
        var s = AiHealth.Snapshot(configured: false);
        Assert.False((bool)s["configured"]!.GetValue<bool>());
        Assert.False(s["healthy"]!.GetValue<bool>());
        Assert.Equal("unconfigured", s["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Snapshot_BeforeAnyCall_HealthyIsNull()
    {
        AiHealth.ResetForTests();
        var s = AiHealth.Snapshot(configured: true);
        Assert.True((bool)s["configured"]!.GetValue<bool>());
        Assert.Null(s["healthy"]);
        Assert.Null(s["reason"]);
    }

    [Fact]
    public void SuccessThenBillingFailure_ReportsBillingDown_ThenSuccessClearsIt()
    {
        AiHealth.ResetForTests();
        AiHealth.RecordSuccess("call");
        Assert.True(AiHealth.Snapshot(true)["healthy"]!.GetValue<bool>());

        AiHealth.RecordFailure("selftest", AiHealth.FailureClass.Billing);
        var down = AiHealth.Snapshot(true);
        Assert.False(down["healthy"]!.GetValue<bool>());
        Assert.Equal("billing", down["reason"]!.GetValue<string>());
        Assert.Equal("selftest", down["source"]!.GetValue<string>());

        AiHealth.RecordSuccess("call");
        Assert.True(AiHealth.Snapshot(true)["healthy"]!.GetValue<bool>());
        AiHealth.ResetForTests();
    }

    [Fact]
    public void TransientFailure_NeverMasksAStandingBillingOutage()
    {
        AiHealth.ResetForTests();
        AiHealth.RecordFailure("call", AiHealth.FailureClass.Billing);
        AiHealth.RecordFailure("call", AiHealth.FailureClass.Rate);   // a later 429 must not rewrite the reason
        Assert.Equal("billing", AiHealth.Snapshot(true)["reason"]!.GetValue<string>());
        AiHealth.ResetForTests();
    }
}
