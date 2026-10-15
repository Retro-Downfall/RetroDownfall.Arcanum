using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Tests.Sanctum;

/// <summary>
/// The one rule that turns a Sanctum verdict into whether the guarded operation continues. AuditOnly overrides a refusal of the Sanctum's
/// policy and nothing else: a refusal that says the policy could not be evaluated, or that the breach could not be recorded, is final in
/// every mode.
/// </summary>
public sealed class SanctumEnforcementTests
{
    [Theory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public void An_allowed_verdict_always_continues(SanctumMode mode) =>
        Assert.True(SanctumEnforcement.AllowsContinuation(new SanctumResult { Allowed = true }, mode));

    [Fact]
    public void A_policy_refusal_stops_a_strict_sanctum_and_is_overridden_by_an_audit_only_one()
    {
        SanctumResult policy = new() { Allowed = false, DenialKind = SanctumDenialKind.Policy };

        Assert.False(SanctumEnforcement.AllowsContinuation(policy, SanctumMode.Strict));

        Assert.True(SanctumEnforcement.AllowsContinuation(policy, SanctumMode.AuditOnly));
    }

    [Fact]
    public void A_hand_built_refusal_is_a_policy_refusal()
    {
        SanctumResult refusal = new() { Allowed = false };

        Assert.Equal(SanctumDenialKind.Policy, refusal.DenialKind);

        Assert.True(SanctumEnforcement.AllowsContinuation(refusal, SanctumMode.AuditOnly));
    }

    [Theory]
    [InlineData(SanctumDenialKind.PolicyUnavailable, SanctumMode.Strict)]
    [InlineData(SanctumDenialKind.PolicyUnavailable, SanctumMode.AuditOnly)]
    [InlineData(SanctumDenialKind.AuditRecordFailed, SanctumMode.Strict)]
    [InlineData(SanctumDenialKind.AuditRecordFailed, SanctumMode.AuditOnly)]
    public void A_refusal_that_is_not_a_policy_refusal_stops_the_operation_in_both_modes(SanctumDenialKind kind, SanctumMode mode) =>
        Assert.False(SanctumEnforcement.AllowsContinuation(new SanctumResult { Allowed = false, DenialKind = kind }, mode));

    [Fact]
    public void An_undefined_mode_counts_as_strict()
    {
        Assert.False(SanctumEnforcement.AllowsContinuation(new SanctumResult { Allowed = false }, (SanctumMode)7));

        Assert.True(SanctumEnforcement.AllowsContinuation(new SanctumResult { Allowed = true }, (SanctumMode)7));
    }

    [Fact]
    public void The_denial_kind_stays_off_the_wire()
    {
        string json = JsonSerializer.Serialize(
            new SanctumResult { Allowed = false, DenyReason = "outside", DenialKind = SanctumDenialKind.PolicyUnavailable },
            ArcanumJsonContext.Default.SanctumResult);

        Assert.DoesNotContain("denialKind", json, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("outside", json, StringComparison.Ordinal);
    }
}
