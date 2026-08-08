using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Cli;

/// <summary>
/// The outcome of one diagnostic (DESIGN §4.4.2). Ordered least-to-most severe so aggregation is a
/// plain maximum. <see cref="Skipped"/> sorts below <see cref="Healthy"/> because a check whose
/// precondition is absent is not a fault and must never colour the report.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DoctorOutcome>))]
public enum DoctorOutcome
{

    /// <summary>The precondition for this check is absent, or the operator excluded it.</summary>
    Skipped = 0,

    /// <summary>The subsystem is in its intended state.</summary>
    Healthy = 1,

    /// <summary>The subsystem could not be consulted, so its state is unknown.</summary>
    Unavailable = 2,

    /// <summary>The subsystem works but is in a state that needs attention.</summary>
    Degraded = 3,

    /// <summary>The subsystem is broken.</summary>
    Unhealthy = 4,

}

/// <summary>
/// The closed set of subsystems a diagnostic can be attributed to. The lower-cased member name is
/// the namespace every check id in that subsystem must use, so <c>--only grimoire</c> and
/// <c>--only grimoire.integrity</c> resolve against the same vocabulary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DoctorSubsystem>))]
public enum DoctorSubsystem
{

    System,

    Paths,

    Permissions,

    Configuration,

    Credentials,

    Grimoire,

    Storage,

    Operations,

    Runtime,

    Providers,

    WebResearch,

    Mcp,

    Conclave,

    Weave,

    Host,

}

/// <summary>
/// One machine-actionable next step. <paramref name="RepairId"/> is non-null only when
/// <c>arcanum doctor --repair</c> can perform the change locally; otherwise the operator runs
/// <paramref name="Command"/> themselves. Never carries a secret, a path outside the Arcanum
/// installation, or a raw response body.
/// </summary>
public sealed record DoctorRemedy(
    string Id,
    string? RepairId,
    string Command,
    string Explanation);

/// <summary>
/// The result of inspecting one subsystem. Produced by <see cref="IDoctorCheck.InspectAsync"/> and
/// projected onto a <see cref="DoctorCheck"/> by <see cref="DoctorDiagnosticRunner"/>.
/// </summary>
public sealed record DoctorFinding(
    DoctorOutcome Outcome,
    string Detail,
    IReadOnlyList<DoctorRemedy>? Remedies = null);

/// <summary>One change a repair intends to perform, or performed.</summary>
public sealed record DoctorRepairStep(
    string Target,
    string Before,
    string After);

[JsonConverter(typeof(JsonStringEnumConverter<DoctorRepairState>))]
public enum DoctorRepairState
{

    /// <summary>The repair was described but nothing was changed (the default, without <c>--apply</c>).</summary>
    Planned = 0,

    /// <summary>The repair changed something.</summary>
    Applied = 1,

    /// <summary>Nothing needed changing; re-running a successful repair lands here.</summary>
    AlreadyConverged = 2,

    /// <summary>The repair declined to act because its safety precondition did not hold.</summary>
    Skipped = 3,

    /// <summary>The repair tried and could not complete.</summary>
    Failed = 4,

}

public sealed record DoctorRepairResult(
    string RepairId,
    DoctorRepairState State,
    string Summary,
    IReadOnlyList<DoctorRepairStep> Steps,
    string? Failure);

/// <summary>
/// One read-only subsystem diagnostic. Implementations must never mutate state — not even to create
/// a directory or take a lock — and are expected to translate their own failures into a finding.
/// The runner still contains anything that escapes, because <c>arcanum doctor</c> is run precisely
/// when the installation is broken.
/// </summary>
public interface IDoctorCheck
{

    /// <summary>Stable machine id, <c>subsystem.snake_case</c>. Part of the <c>--json</c> contract.</summary>
    string Id { get; }

    /// <summary>Human-facing panel and legacy <c>DoctorCheck.Name</c> label.</summary>
    string Name { get; }

    DoctorSubsystem Subsystem { get; }

    /// <summary>True when inspecting performs outbound network I/O, which is opt-in.</summary>
    bool RequiresNetwork { get; }

    Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken);

}

/// <summary>
/// One narrow, idempotent repair. <see cref="PlanAsync"/> must be side-effect free and is what a
/// bare <c>--repair</c> shows; <see cref="ApplyAsync"/> must converge, so a second successful run
/// reports <see cref="DoctorRepairState.AlreadyConverged"/> rather than changing anything again.
/// </summary>
public interface IDoctorRepair
{

    /// <summary>Stable machine id, <c>subsystem.snake_case</c>.</summary>
    string Id { get; }

    DoctorSubsystem Subsystem { get; }

    /// <summary>Check ids whose findings justify this repair. Every one must be registered.</summary>
    IReadOnlyList<string> DetectorIds { get; }

    /// <summary>One sentence describing what applying this repair changes.</summary>
    string Description { get; }

    Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken);

    Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken);

}

/// <summary>What one <c>arcanum doctor</c> invocation was asked to do.</summary>
public sealed record DoctorRunRequest(
    IReadOnlyList<string> Only,
    IReadOnlyList<string> Skip,
    bool IncludeNetwork,
    IReadOnlyList<string> Repairs,
    bool Apply,
    bool Strict)
{

    /// <summary>The default invocation: every local check, no network, no repair, nothing changed.</summary>
    public static DoctorRunRequest ReadOnly { get; } = new([], [], false, [], false, false);

}

/// <summary>Error codes the diagnostic spine can fail with. Both map to <c>CliExitCode.ConfigurationError</c>.</summary>
public static class DoctorErrors
{

    public const string UnknownSelectorCode = "Doctor.UnknownSelector";

    public const string ApplyWithoutRepairCode = "Doctor.ApplyWithoutRepair";

}

/// <summary>Pure projections over <see cref="DoctorOutcome"/>.</summary>
public static class DoctorOutcomes
{

    /// <summary>
    /// The pre-#33 wire vocabulary. <see cref="DoctorCheck.Status"/> keeps carrying it so existing
    /// <c>--json</c> consumers and their <c>jq</c> filters survive the richer report.
    /// </summary>
    public static string ToLegacyStatus(DoctorOutcome outcome) => outcome switch
    {
        DoctorOutcome.Unhealthy => "fail",
        DoctorOutcome.Degraded or DoctorOutcome.Unavailable => "warn",
        _ => "ok",
    };

    /// <summary>
    /// The most severe outcome present. An empty run, or one that only skipped, is healthy: a check
    /// that never applied is not evidence of a fault.
    /// </summary>
    public static DoctorOutcome Aggregate(IEnumerable<DoctorOutcome> outcomes)
    {

        ArgumentNullException.ThrowIfNull(outcomes);

        DoctorOutcome worst = DoctorOutcome.Healthy;

        foreach (DoctorOutcome outcome in outcomes)
        {

            if (outcome > worst)
            {

                worst = outcome;

            }

        }

        return worst;

    }

    /// <summary>
    /// Whether an aggregate outcome should exit nonzero. Only <see cref="DoctorOutcome.Unhealthy"/>
    /// fails by default — an unreachable host has always been a warning, and a diagnostic that
    /// failed the build every time the API was not running would be unusable in CI.
    /// </summary>
    public static bool IsFailure(DoctorOutcome outcome, bool strict) =>
        outcome == DoctorOutcome.Unhealthy
        || (strict && outcome is DoctorOutcome.Degraded or DoctorOutcome.Unavailable);

}
