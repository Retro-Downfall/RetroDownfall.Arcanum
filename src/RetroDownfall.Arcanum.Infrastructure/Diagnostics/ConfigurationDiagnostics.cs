using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Runs the same semantic validation the host runs at startup — provider shapes, model references,
/// HTTPS certificate and port coherence, embedding and web-research settings, path allowlists.
///
/// <para>This closes a real gap rather than duplicating an existing check. Semantic validation lives
/// in an <c>IStartupFilter</c> that only executes under <c>arcanum serve</c>, so an operator who
/// never starts the host has no way to learn that their configuration is internally inconsistent —
/// the file parses, so the pre-#33 <c>Configuration</c> check reported it healthy right up until
/// the first inference call failed.</para>
/// </summary>
public sealed class ConfigurationSemanticsCheck(
    IOptions<ArcanumSettings> options,
    ConfigurationValidator validator) : IDoctorCheck
{

    public const string CheckId = "configuration.semantics";

    public string Id => CheckId;

    public string Name => "Configuration semantics";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Configuration;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        Result result = validator.Validate(options.Value);

        if (result.IsSuccess)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                "Every configured provider, model reference, and host setting is internally consistent."));

        }

        IReadOnlyList<ConfigurationValidationError> details = result.Error.Details ?? [];

        string summary = details.Count == 0
            ? result.Error.Message
            : string.Join(
                "; ",
                details.Select(static detail => $"{detail.Pointer}: {detail.Detail}"));

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Unhealthy,
            $"{Math.Max(details.Count, 1)} configuration problem(s): {summary}",
            [
                new DoctorRemedy(
                    "configuration.correct_settings",
                    null,
                    DoctorRemedyCommands.ConfigEdit,
                    "Correct the reported paths, then re-run 'arcanum doctor --only configuration'."),
            ]));

    }

}

/// <summary>
/// Environment variables that claim a configuration path but did not take effect. An override that
/// silently failed to bind is the hardest configuration fault to see: the file is right, the
/// variable is set, and the running value is neither. Values are never read into the finding —
/// only the variable name, the path it targets, and the binder's own error.
/// </summary>
public sealed class ConfigurationEnvironmentOverrideCheck(IOptions<ArcanumSettings> options) : IDoctorCheck
{

    public const string CheckId = "configuration.environment_overrides";

    public string Id => CheckId;

    public string Name => "Environment overrides";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Configuration;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        ConfigurationEnvironmentSnapshot snapshot =
            ConfigurationEnvironmentResolver.Resolve(options.Value);

        IReadOnlyList<ConfigurationEnvironmentOverride> ineffective =
            [.. snapshot.Overrides.Where(static candidate => candidate.IsPresent && !candidate.IsEffective)];

        int effective = snapshot.Overrides.Count(static candidate => candidate.IsEffective);

        if (ineffective.Count == 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                effective == 0
                    ? "No configuration environment override is set."
                    : $"{effective} configuration environment override(s) applied cleanly."));

        }

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Degraded,
            $"{ineffective.Count} environment override(s) are set but did not apply: "
            + string.Join(
                "; ",
                ineffective.Select(static candidate =>
                    $"{candidate.VariableName} targets {candidate.Path} ({candidate.Error ?? "value rejected"})")),
            [
                new DoctorRemedy(
                    "configuration.fix_environment_override",
                    null,
                    DoctorRemedyCommands.ConfigShow,
                    "Unset the reported variable or correct its value; 'arcanum config show' reports which source won for each path."),
            ]));

    }

}
