using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Cli.Diagnostics;

/// <summary>
/// Whether every configured Familiar is installed and signed in.
/// </summary>
/// <remarks>
/// <para>
/// Not network-gated, and deliberately so. A Familiar is a local binary: the check resolves it on
/// PATH and asks it for its own status, which sends nothing outward on Arcanum's behalf and spends
/// nothing against the operator's subscription. That is a local-first question, exactly the kind
/// <c>arcanum doctor</c> exists to answer without <c>--include-network</c>.
/// </para>
/// <para>
/// The remediation is always a command the operator runs themselves. Arcanum never installs,
/// updates, configures, or signs into a Familiar, so a diagnostic cannot offer to repair one.
/// </para>
/// </remarks>
public sealed class FamiliarReadinessCheck(
    IOptions<ArcanumSettings> options,
    IFamiliarProbe probe) : IDoctorCheck
{

    public const string CheckId = "providers.familiars";

    public string Id => CheckId;

    public string Name => "Familiar readiness";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Providers;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<ProviderSettings> familiars =
        [
            .. (options.Value.Providers ?? [])
                .Where(static provider =>
                    FamiliarProviders.IsFamiliar(provider)
                    && !string.IsNullOrWhiteSpace(provider.Name)),
        ];

        if (familiars.Count == 0)
        {

            return new DoctorFinding(
                DoctorOutcome.Skipped,
                "No subscription-backed CLI provider is configured.");

        }

        List<string> summaries = [];

        List<DoctorRemedy> remedies = [];

        DoctorOutcome worst = DoctorOutcome.Healthy;

        foreach (ProviderSettings provider in familiars)
        {

            cancellationToken.ThrowIfCancellationRequested();

            FamiliarProbeResult result = await probe
                .ProbeAsync(provider, cancellationToken)
                .ConfigureAwait(false);

            (DoctorOutcome outcome, string summary, DoctorRemedy? remedy) = Classify(result);

            summaries.Add(summary);

            if (remedy is not null)
            {

                remedies.Add(remedy);

            }

            if (outcome > worst)
            {

                worst = outcome;

            }

        }

        return new DoctorFinding(
            worst,
            string.Join("; ", summaries),
            remedies.Count > 0 ? remedies : null);

    }

    internal static (DoctorOutcome Outcome, string Summary, DoctorRemedy? Remedy) Classify(
        FamiliarProbeResult result) => result.Status switch
        {

            FamiliarProbeStatus.Configured => (
                DoctorOutcome.Healthy,
                result.Version is { Length: > 0 } version
                    ? $"{result.ProviderName}: ready ({version})"
                    : $"{result.ProviderName}: ready",
                null),

            FamiliarProbeStatus.NotConfigured => (
                DoctorOutcome.Unhealthy,
                $"{result.ProviderName}: installed but not signed in",
                new DoctorRemedy(
                    "providers.familiar_sign_in",
                    null,
                    result.RemediationCommand ?? string.Empty,
                    result.Remediation)),

            _ => (
                DoctorOutcome.Unhealthy,
                $"{result.ProviderName}: not installed",
                new DoctorRemedy(
                    "providers.familiar_install",
                    null,
                    DoctorRemedyCommands.ConfigEdit,
                    result.Remediation)),

        };

}
