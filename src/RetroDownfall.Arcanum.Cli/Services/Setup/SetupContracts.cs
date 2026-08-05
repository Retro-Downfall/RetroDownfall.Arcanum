using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Cli.Services.Setup;

/// <summary>
/// Explicit, ordered steps of the guided setup state machine. The wizard is resumable: a step that
/// fails is re-entered rather than restarting the run, and every step before <see cref="Commit"/>
/// mutates only the in-memory draft.
/// </summary>
public enum SetupStep
{

    /// <summary>Runtime edition and privacy posture (loopback-only versus network binding).</summary>
    Edition = 0,

    /// <summary>Provider name, OpenAI-compatible endpoint, and model.</summary>
    Provider = 1,

    /// <summary>Provider credential value or environment reference.</summary>
    ProviderCredential = 2,

    /// <summary>Optional web-research credential.</summary>
    WebResearchCredential = 3,

    /// <summary>Non-billable live provider validation.</summary>
    Connectivity = 4,

    /// <summary>Workspace root and optional Campaign name.</summary>
    Workspace = 5,

    /// <summary>Onboarding preset selection.</summary>
    Preset = 6,

    /// <summary>Final diff and explicit acceptance.</summary>
    Review = 7,

    /// <summary>Ordered commit with rollback.</summary>
    Commit = 8,

    Done = 9,

}

/// <summary>What the wizard will do with one credential when the plan is committed.</summary>
public enum SetupCredentialAction
{

    /// <summary>Leave whatever is already stored or referenced exactly as it is.</summary>
    Unchanged = 0,

    /// <summary>Write the supplied value into the OS-backed secure store.</summary>
    Store = 1,

    /// <summary>Record an environment-variable reference; no secret is read or written.</summary>
    Reference = 2,

    /// <summary>Delete the stored credential (a keyless local provider needs none).</summary>
    Clear = 3,

}

/// <summary>
/// Every choice the wizard has collected so far. Held in memory only — it is never serialized,
/// never logged, and never persisted, because it can carry credential values between the credential
/// step and the commit step.
/// </summary>
public sealed record SetupDraft
{

    public ArcanumEdition Edition { get; init; } = ArcanumEdition.Local;

    /// <summary>Privacy posture: <see langword="false"/> keeps the host bound to loopback.</summary>
    public bool ListenAny { get; init; }

    public string ProviderName { get; init; } = string.Empty;

    public string ProviderEndpoint { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public SetupCredentialAction ProviderCredential { get; init; } = SetupCredentialAction.Unchanged;

    public string? ProviderCredentialEnvironmentVariable { get; init; }

    public string? ProviderCredentialValue { get; init; }

    public bool WebResearchRequested { get; init; }

    public SetupCredentialAction WebResearchCredential { get; init; } =
        SetupCredentialAction.Unchanged;

    public string? WebResearchCredentialEnvironmentVariable { get; init; }

    public string? WebResearchCredentialValue { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? CampaignName { get; init; }

    public string PresetId { get; init; } = "general-assistant";

    /// <summary>
    /// Lets an operator commit a plan whose provider could not be validated (an air-gapped host, or
    /// a local model server that has not been started yet). Never the default.
    /// </summary>
    public bool AllowUnreachableProvider { get; init; }

    /// <summary>
    /// Suppresses the record's compiler-generated member listing. Without this override an
    /// interpolated draft — in a log line, an exception message, or a debugger view — would print
    /// the credential values verbatim.
    /// </summary>
    public override string ToString() =>
        $"SetupDraft {{ Provider = {ProviderName}, Model = {Model}, Preset = {PresetId}, "
        + "Credentials = <redacted> }";

}

/// <summary>
/// Ordered, resumable traversal of <see cref="SetupStep"/>. Kept separate from prompting and I/O so
/// the traversal itself is deterministic and directly testable.
/// </summary>
public sealed class SetupStateMachine
{

    private static readonly SetupStep[] Sequence =
    [
        SetupStep.Edition,
        SetupStep.Provider,
        SetupStep.ProviderCredential,
        SetupStep.WebResearchCredential,
        SetupStep.Connectivity,
        SetupStep.Workspace,
        SetupStep.Preset,
        SetupStep.Review,
        SetupStep.Commit,
        SetupStep.Done,
    ];

    private int _index;

    public SetupStateMachine(SetupStep start = SetupStep.Edition) => Reenter(start);

    public static IReadOnlyList<SetupStep> Order => Sequence;

    public SetupStep Current => Sequence[_index];

    public bool IsComplete => Current == SetupStep.Done;

    public bool MoveNext()
    {

        if (_index >= Sequence.Length - 1)
        {

            return false;

        }

        _index++;

        return true;

    }

    public bool MoveBack()
    {

        if (_index == 0)
        {

            return false;

        }

        _index--;

        return true;

    }

    /// <summary>Resume at an explicit step, for example after a failed dependency check.</summary>
    public void Reenter(SetupStep step)
    {

        int index = Array.IndexOf(Sequence, step);

        _index = index < 0
            ? throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown setup step.")
            : index;

    }

}

/// <summary>One changed configuration leaf in the wizard's final diff. Sensitive values are masked.</summary>
public sealed record SetupChangePayload(
    string Path,
    string Current,
    string Proposed);

/// <summary>
/// What the plan will do with one credential. Carries the identity, the action, and the storage
/// class — never the value and never a value-derived hint.
/// </summary>
public sealed record SetupCredentialPlanPayload(
    string Kind,
    string Target,
    string Action,
    string Storage,
    string? EnvironmentVariable);

public sealed record SetupConnectivityPayload(
    string Status,
    long LatencyMs,
    int ModelsAdvertised,
    bool SelectedModelAdvertised,
    string Detail);

/// <summary>
/// The completion summary required by issue #19. The provider endpoint itself is sensitive, so only
/// <see cref="EndpointClass"/> is reported.
/// </summary>
public sealed record SetupCompletionSummaryPayload(
    string ActivePreset,
    string ProviderAndModel,
    string EndpointClass,
    string WorkspaceAndCampaign,
    string[] NetworkCapabilities,
    string[] MemoryCapabilities,
    string ToolSecurityPosture,
    string PrivacyState,
    string NextCommand);

public sealed record SetupPlanPayload(
    string Step,
    bool IsApplicable,
    bool IsIdempotent,
    SetupChangePayload[] ConfigurationChanges,
    SetupCredentialPlanPayload[] Credentials,
    SetupConnectivityPayload Connectivity,
    string[] Blockers,
    string[] Notes,
    SetupCompletionSummaryPayload Summary);

public sealed record SetupResultPayload(
    bool Committed,
    string Step,
    SetupPlanPayload Plan,
    string[] Applied,
    string[] RolledBack,
    string? Failure,
    string? Recovery);
