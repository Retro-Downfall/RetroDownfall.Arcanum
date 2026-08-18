using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Cli;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #33 — every doctor finding names the action that actually helps. The probe state that says
/// "something answered, just not as an Arcanum host" is the one where the generic "start the host"
/// advice is worst: a second host cannot bind a port another process already holds.
/// </summary>
public sealed class HostHealthDiagnosticsTests
{

    [Fact]
    public void An_unexpected_responder_is_not_reported_as_a_host_that_never_answered()
    {

        DoctorFinding finding = HostHealthComponentsCheck.Describe(
            new HealthProbeResult(
                HealthProbeState.UnexpectedResponder,
                null,
                TimeSpan.Zero,
                "The response ended prematurely."));

        Assert.Equal(DoctorOutcome.Unhealthy, finding.Outcome);

        Assert.DoesNotContain("did not answer", finding.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(
            DoctorRemedyCommands.Serve,
            (finding.Remedies ?? []).Select(static remedy => remedy.Command));

    }

    [Theory]
    [InlineData(HealthProbeState.ConnectionRefused)]
    [InlineData(HealthProbeState.NetworkUnreachable)]
    [InlineData(HealthProbeState.DnsFailure)]
    public void A_host_that_never_answered_still_recommends_starting_it(HealthProbeState state)
    {

        DoctorFinding finding = HostHealthComponentsCheck.Describe(
            new HealthProbeResult(state, null, TimeSpan.Zero, "no listener"));

        Assert.Equal(DoctorOutcome.Unavailable, finding.Outcome);

        Assert.Contains(
            DoctorRemedyCommands.Serve,
            (finding.Remedies ?? []).Select(static remedy => remedy.Command));

    }

}
