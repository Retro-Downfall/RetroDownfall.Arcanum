using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// Asks a Familiar whether it is installed and signed in, without asking it for a completion.
/// </summary>
/// <remarks>
/// Runs host-side, next to the process runner, because Compendium is an editor and must not grow a
/// second process-spawn implementation. The probe costs no tokens, so it belongs on the non-billable
/// surface list beside <c>GET /v1/models</c>.
/// </remarks>
public interface IFamiliarProbe
{

    Task<FamiliarProbeResult> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken);

}

/// <summary>
/// Probes the two Familiar kinds using each CLI's own documented status surface.
/// </summary>
/// <remarks>
/// <para>
/// Verified against <c>claude 2.1.220</c> and <c>codex-cli 0.147.0</c>. Claude Code reports auth
/// through <c>claude auth status --json</c> and its version through <c>claude --version</c>; Codex
/// answers both in one <c>codex doctor --json</c> report, which it documents as redacted.
/// </para>
/// <para>
/// Redaction is structural rather than a filtering pass: the bound DTOs have no field for the
/// account e-mail, organisation identifiers, or local paths those commands print, so that material
/// cannot reach a payload or a log even by accident.
/// </para>
/// </remarks>
public sealed class FamiliarProbe(
    IFamiliarProcessRunner runner,
    IOptionsMonitor<ArcanumSettings>? settings = null) : IFamiliarProbe
{

    public async Task<FamiliarProbeResult> ProbeAsync(
        ProviderSettings provider,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(provider);

        string command = FamiliarProviders.ResolveCommand(provider);

        (FamiliarModelEnumeration enumeration, string[] models) = ResolveModels(provider);

        // Resolution and the spawn must agree on the same file. On Windows the resolver applies
        // PATHEXT, so a CLI installed as `claude.cmd` resolves here but a bare `claude` would not
        // start — reporting "ready" for something that can never run.
        if (!FamiliarExecutableResolver.TryResolve(command, out string? resolvedPath))
        {

            return Build(
                provider,
                FamiliarProbeStatus.NotInstalled,
                version: null,
                $"{FamiliarProviders.DisplayName(provider.Type)} was not found as `{command}`.",
                $"Install the {FamiliarProviders.DisplayName(provider.Type)} yourself and make sure it is on PATH, or set this provider's `command` to its full path. Arcanum never installs a Familiar.",
                remediationCommand: null,
                enumeration,
                models);

        }

        string executable = resolvedPath ?? command;

        // A status spawn is still a spawn. Both CLIs read project state out of their working root —
        // Claude Code's `.claude/settings.json` hooks, Codex's `AGENTS.md` and `.rules` — so the
        // probe gets the same private root the inference path gives every turn. Inheriting the host's
        // current directory would let whatever repository the operator started Arcanum in steer it.
        FamiliarWorkingDirectory root;

        try
        {

            root = FamiliarWorkingDirectory.Create();

        }
        catch (IOException)
        {

            // The inference path is right to throw here, but the probe is the surface an operator
            // calls to find out what is wrong — so it reports the unusable host instead of taking
            // down the caller. Nothing is spawned either way: running the CLI from a directory other
            // accounts can write to is precisely what was refused.
            return Build(
                provider,
                FamiliarProbeStatus.NotConfigured,
                version: null,
                $"{FamiliarProviders.DisplayName(provider.Type)} could not be checked: Arcanum could not create a private directory to run it in.",
                "Make sure this machine's temporary directory exists and is writable by the account Arcanum runs as. A Familiar is never run from a directory other accounts can write to.",
                remediationCommand: null,
                enumeration,
                models);

        }

        using (root)
        {

            return provider.Type switch
            {

                AiProviderKind.ClaudeCodeCli =>
                    await ProbeClaudeAsync(provider, executable, root.Path, enumeration, models, cancellationToken)
                        .ConfigureAwait(false),

                AiProviderKind.CodexCli =>
                    await ProbeCodexAsync(provider, executable, root.Path, enumeration, models, cancellationToken)
                        .ConfigureAwait(false),

                _ => Build(
                    provider,
                    FamiliarProbeStatus.NotInstalled,
                    version: null,
                    $"'{provider.Type}' is not a Familiar kind.",
                    "Set this provider's type to ClaudeCodeCli or CodexCli.",
                    remediationCommand: null,
                    enumeration,
                    models),

            };

        }

    }

    private async Task<FamiliarProbeResult> ProbeClaudeAsync(
        ProviderSettings provider,
        string command,
        string workingDirectory,
        FamiliarModelEnumeration enumeration,
        string[] models,
        CancellationToken cancellationToken)
    {

        string? version = await ReadVersionAsync(command, ["--version"], workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        FamiliarProcessOutput output = await runner.RunToCompletionAsync(
            new FamiliarProcessRequest
            {
                FileName = command,
                Arguments = ["auth", "status", "--json"],
                WorkingDirectory = workingDirectory,
                DeniedEnvironmentVariables = DeniedEnvironmentVariables(),
                Timeout = FamiliarProcessLimits.ProbeTimeout,
            },
            cancellationToken).ConfigureAwait(false);

        // A CLI that never answered is not a signed-out CLI. A wedged binary that hit the probe
        // deadline, or one the OS refused to start, leaves stdout empty — and reading that as a
        // sign-out would send the operator to `auth login` to fix something that is not broken.
        if (output.Failure is FamiliarProcessFailure.TimedOut
            or FamiliarProcessFailure.StartFailed
            or FamiliarProcessFailure.NotInstalled)
        {

            return Build(
                provider,
                FamiliarProbeStatus.NotConfigured,
                version,
                "Claude Code is installed but did not answer its status command.",
                "Run the status command yourself to see what it says; Arcanum only reads its status.",
                "claude auth status",
                enumeration,
                models);

        }

        ClaudeAuthStatus? status = TryParse(
            output.StandardOutput,
            FamiliarProbeJsonContext.Default.ClaudeAuthStatus);

        // A non-zero exit here means the CLI ran and refused to answer, which for an installed
        // binary is the same operator action as an explicit "not signed in".
        if (status?.LoggedIn != true)
        {

            return Build(
                provider,
                FamiliarProbeStatus.NotConfigured,
                version,
                "Claude Code is installed but not signed in.",
                "Sign in with your own Claude subscription. Arcanum never authenticates for you and never reads the CLI's credential store.",
                FamiliarProviders.SignInCommand(provider.Type),
                enumeration,
                models);

        }

        string plan = string.IsNullOrWhiteSpace(status.SubscriptionType)
            ? string.Empty
            : $" ({status.SubscriptionType})";

        return Build(
            provider,
            FamiliarProbeStatus.Configured,
            version,
            $"Claude Code is installed and signed in{plan}.",
            remediation: string.Empty,
            remediationCommand: null,
            enumeration,
            models);

    }

    private async Task<FamiliarProbeResult> ProbeCodexAsync(
        ProviderSettings provider,
        string command,
        string workingDirectory,
        FamiliarModelEnumeration enumeration,
        string[] models,
        CancellationToken cancellationToken)
    {

        FamiliarProcessOutput output = await runner.RunToCompletionAsync(
            new FamiliarProcessRequest
            {
                FileName = command,
                Arguments = ["doctor", "--json"],
                WorkingDirectory = workingDirectory,
                DeniedEnvironmentVariables = DeniedEnvironmentVariables(),
                Timeout = FamiliarProcessLimits.ProbeTimeout,
            },
            cancellationToken).ConfigureAwait(false);

        CodexDoctorReport? report = TryParse(
            output.StandardOutput,
            FamiliarProbeJsonContext.Default.CodexDoctorReport);

        if (report is null)
        {

            return Build(
                provider,
                FamiliarProbeStatus.NotConfigured,
                version: null,
                "Codex is installed but its health report could not be read.",
                "Run the report yourself to see what it says; Arcanum only reads its status.",
                "codex doctor",
                enumeration,
                models);

        }

        bool authConfigured =
            report.Checks is not null
            && report.Checks.TryGetValue("auth.credentials", out CodexDoctorCheck? auth)
            && string.Equals(auth?.Status, "ok", StringComparison.OrdinalIgnoreCase);

        if (!authConfigured)
        {

            return Build(
                provider,
                FamiliarProbeStatus.NotConfigured,
                report.CodexVersion,
                "Codex is installed but not signed in.",
                "Sign in with your own ChatGPT subscription. Arcanum never authenticates for you and never reads the CLI's credential store.",
                FamiliarProviders.SignInCommand(provider.Type),
                enumeration,
                models);

        }

        return Build(
            provider,
            FamiliarProbeStatus.Configured,
            report.CodexVersion,
            "Codex is installed and signed in.",
            remediation: string.Empty,
            remediationCommand: null,
            enumeration,
            models);

    }

    /// <summary>
    /// Reads a version string, keeping only the leading token. <c>claude --version</c> answers
    /// "2.1.220 (Claude Code)"; the product name is decoration, and the number is the fact.
    /// </summary>
    private async Task<string?> ReadVersionAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {

        FamiliarProcessOutput output = await runner.RunToCompletionAsync(
            new FamiliarProcessRequest
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                DeniedEnvironmentVariables = DeniedEnvironmentVariables(),
                Timeout = FamiliarProcessLimits.ProbeTimeout,
            },
            cancellationToken).ConfigureAwait(false);

        if (output.Failure != FamiliarProcessFailure.None)
        {
            return null;
        }

        string first = output.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(string.Empty)
            .Trim();

        string token = first.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(string.Empty);

        return token.Length > 0 ? token : null;

    }

    /// <summary>
    /// The same credential boundary the inference path enforces. A status spawn is still a spawn:
    /// Arcanum's configured secrets have no business reaching it just because it asks a cheaper
    /// question.
    /// </summary>
    private IReadOnlyList<string> DeniedEnvironmentVariables() =>
        settings is null
            ? []
            : FamiliarSecretEnvironmentNames.Collect(settings.CurrentValue);

    /// <summary>
    /// What Arcanum can say about this Familiar's catalogue. Neither CLI publishes one, so the
    /// honest answers are "the operator declared these" and "unknown" — never a fabricated list.
    /// </summary>
    private static (FamiliarModelEnumeration Enumeration, string[] Models) ResolveModels(
        ProviderSettings provider)
    {

        string[] declared = [.. ProviderResolver.EnumerateAdvertisedModels(provider)];

        return declared.Length > 0
            ? (FamiliarModelEnumeration.OperatorDeclared, declared)
            : (FamiliarModelEnumeration.Unknown, []);

    }

    private static FamiliarProbeResult Build(
        ProviderSettings provider,
        FamiliarProbeStatus status,
        string? version,
        string summary,
        string remediation,
        string? remediationCommand,
        FamiliarModelEnumeration enumeration,
        string[] models) =>
        new(
            provider.Name,
            provider.Type.ToString(),
            status,
            version,
            summary,
            remediation,
            remediationCommand,
            enumeration,
            models,
            [.. provider.HiddenModels ?? []]);

    private static T? TryParse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {

            return JsonSerializer.Deserialize(json, typeInfo);

        }
        catch (JsonException)
        {

            // A CLI whose report Arcanum cannot read is reported as unready, not as a crash.
            return null;

        }

    }

}

/// <summary>
/// The subset of <c>claude auth status --json</c> Arcanum binds.
/// </summary>
/// <remarks>
/// The command also prints <c>email</c>, <c>orgId</c>, and <c>orgName</c>. They are absent here on
/// purpose: with no field to bind to, account material cannot reach a probe payload or a log, and
/// the guarantee holds structurally rather than depending on a redaction pass being remembered.
/// </remarks>
internal sealed record ClaudeAuthStatus
{

    public bool LoggedIn { get; init; }

    public string? AuthMethod { get; init; }

    public string? SubscriptionType { get; init; }

}

/// <summary>The subset of <c>codex doctor --json</c> Arcanum binds.</summary>
internal sealed record CodexDoctorReport
{

    public string? CodexVersion { get; init; }

    public string? OverallStatus { get; init; }

    public Dictionary<string, CodexDoctorCheck>? Checks { get; init; }

}

/// <summary>
/// One <c>codex doctor</c> check. <c>details</c> is deliberately unbound — it carries local paths.
/// </summary>
internal sealed record CodexDoctorCheck
{

    public string? Status { get; init; }

    public string? Summary { get; init; }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClaudeAuthStatus))]
[JsonSerializable(typeof(CodexDoctorReport))]
internal partial class FamiliarProbeJsonContext : JsonSerializerContext;
