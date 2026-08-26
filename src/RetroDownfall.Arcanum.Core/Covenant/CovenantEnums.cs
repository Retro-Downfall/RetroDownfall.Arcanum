using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantScope>))]
public enum CovenantScope : byte
{
    Global = 1,
    Campaign = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantLane>))]
public enum CovenantLane : byte
{
    Confirmed = 1,
    Proposed = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantOperation>))]
public enum CovenantOperation : byte
{
    Set = 1,
    Retire = 2
}

/// <summary>
/// What one curation change does to the subject it names.
/// </summary>
/// <remarks>
/// Curation is a separate vocabulary from <see cref="CovenantOperation"/> rather than an extension of
/// it, because a pin is not a version of the operator's text: it carries no content, no tombstone, and
/// no lane revision of the entry's own. Every code is written literally because it is persisted.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCurationKind>))]
public enum CovenantCurationKind : byte
{
    /// <summary>Marks the subject durable, so an agent may not author over it.</summary>
    Pin = 1,

    /// <summary>Releases a pin.</summary>
    Unpin = 2,

    /// <summary>Stops a Global key applying inside one Campaign, with nothing in its place.</summary>
    Mask = 3,

    /// <summary>Releases a mask, so the Global key applies in that Campaign again.</summary>
    Unmask = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantOrigin>))]
public enum CovenantOrigin : byte
{
    Operator = 1,
    AgentProposed = 2,
    AgentApproved = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMutationKind>))]
public enum CovenantMutationKind : byte
{
    OperatorSet = 1,
    OperatorRetire = 2,
    AgentPropose = 3,
    AgentRetire = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantPlacement>))]
public enum CovenantPlacement : byte
{
    GlobalConfirmed = 1,
    CampaignConfirmed = 2,
    CampaignProposed = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantPlanDecision>))]
public enum CovenantPlanDecision : byte
{
    EligibleConfirmed = 1,
    EligibleProposed = 2,
    Shadowed = 3,
    ReviewOnly = 4,
    Quarantined = 5,
    Invalid = 6,

    /// <summary>A Global entry the evaluating Campaign masked. Nothing replaces it.</summary>
    /// <remarks>
    /// Distinct from <see cref="Shadowed"/>, which names the entry that replaced it. A mask names
    /// nothing, and folding the two together would tell an operator their Global preference had been
    /// superseded by content that does not exist.
    /// </remarks>
    Masked = 7
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantAdmissionDecision>))]
public enum CovenantAdmissionDecision : byte
{
    Admitted = 1,
    Pressured = 2,
    RequiredNoFit = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantAuthorizationMode>))]
public enum CovenantAuthorizationMode : byte
{
    None = 0,
    ApiMasterKey = 1,
    WardInteractive = 2,
    WardConfiguredAutoApproval = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMutationOutcome>))]
public enum CovenantMutationOutcome : byte
{
    Applied = 1,
    NoChange = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantFinalOutcome>))]
public enum CovenantFinalOutcome : byte
{
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Interrupted = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<AssistantFinalizationOrigin>))]
public enum AssistantFinalizationOrigin : byte
{
    Committed = 1,
    Discarded = 2,
    CommittedImported = 3,
    CommittedForked = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantWardDecision>))]
public enum CovenantWardDecision : byte
{
    Approved = 1,
    Denied = 2,
    Cancelled = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantProviderRole>))]
public enum CovenantProviderRole : byte
{
    System = 1,
    User = 2,
    Assistant = 3,
    Tool = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantProviderDispatchMode>))]
public enum CovenantProviderDispatchMode : byte
{
    Buffered = 1,
    Streaming = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantProviderContentPart>))]
public enum CovenantProviderContentPart : byte
{
    Text = 1,
    Binary = 2,
    ToolCall = 3,
    ToolResult = 4,
    Json = 5,
    Uri = 6,
    TextReasoning = 7
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantToolRiskIdentity>))]
public enum CovenantToolRiskIdentity : byte
{
    Ordinary = 1,
    ConfiguredForbiddenArt = 2,
    IntrinsicForbiddenArt = 3,
    CovenantSensitiveEgress = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SensitiveArtifactKind>))]
public enum SensitiveArtifactKind : byte
{
    AssistantEntry = 1,
    TurnEvidence = 2,
    Summary = 3,
    ToolArtifact = 4,
    SessionTitle = 5,
    Saga = 6,
    Lexicon = 7,
    Embedding = 8,
    SearchProjection = 9,
    AuditProjection = 10,
    Notification = 11,
    ManagedWorkspaceFile = 12,
    IdempotencyClaim = 13
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantEgressDestination>))]
public enum CovenantEgressDestination : byte
{
    Provider = 1,
    ManagedWorkspaceFile = 2,
    UnmanagedWorkspaceFile = 3,
    Process = 4,
    Network = 5,
    ExternalMcp = 6,
    Message = 7,
    EncryptedBackup = 8
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantDisclosureSubjectKind>))]
public enum CovenantDisclosureSubjectKind : byte
{
    Turn = 1,
    Operation = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantDisclosureRevocability>))]
public enum CovenantDisclosureRevocability : byte
{
    LocallyRevocable = 1,
    Nonrevocable = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantDisclosureCountKind>))]
public enum CovenantDisclosureCountKind : byte
{
    Exact = 1,
    LowerBound = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionTurnSurface>))]
public enum SessionTurnSurface : byte
{
    Intelligence = 1,
    PromptExecute = 2,
    SpellExecute = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantToolPolicyCode>))]
public enum CovenantToolPolicyCode : byte
{
    AllTools = 1,
    NoTools = 2,
    ReadOnlyTools = 3,
    NoForbiddenArts = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaintenanceStep>))]
public enum CovenantMaintenanceStep : byte
{
    Summary = 1,
    Title = 2,
    Saga = 3,
    Lexicon = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaintenanceCheckpoint>))]
public enum CovenantMaintenanceCheckpoint : byte
{
    Prepared = 1,
    Committed = 2,
    Failed = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionTurnClaimState>))]
public enum SessionTurnClaimState : byte
{
    PendingMaintenance = 1,
    Begun = 2,
    Committed = 3,
    Discarded = 4,
    Erased = 5,
    RestoredInterrupted = 6
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupDisclosurePhase>))]
public enum BackupDisclosurePhase : byte
{
    SnapshotRead = 1,
    EncryptedArchiveWrite = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CampaignPathIdentityOperation>))]
public enum CampaignPathIdentityOperation : byte
{
    Register = 1,
    Update = 2,
    RepairMoved = 3,
    Deregister = 4,
    TakeoverOrphan = 5
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ProviderToolChoice>))]
public enum ProviderToolChoice : byte
{
    Auto = 1,
    None = 2,
    Required = 3,
    Named = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ProviderResponseFormat>))]
public enum ProviderResponseFormat : byte
{
    Text = 1,
    JsonObject = 2,
    JsonSchema = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantReasoningEffort>))]
public enum CovenantReasoningEffort : byte
{
    None = 1,
    Minimal = 2,
    Low = 3,
    Medium = 4,
    High = 5,
    ExtraHigh = 6
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantReasoningOutput>))]
public enum CovenantReasoningOutput : byte
{
    None = 1,
    Summary = 2,
    Full = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantReasoningWireDialect>))]
public enum CovenantReasoningWireDialect : byte
{
    Standard = 1,
    OpenRouter = 2,
    TopLevelReasoningBudget = 3,
    AnthropicThinking = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantTriStateBoolean>))]
public enum CovenantTriStateBoolean : byte
{
    Absent = 0,
    False = 1,
    True = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantImageDetail>))]
public enum CovenantImageDetail : byte
{
    Auto = 1,
    Low = 2,
    High = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantPromptAttribution>))]
public enum CovenantPromptAttribution : byte
{
    DataHeader = 1,
    CovenantProposed = 2,
    DataBody = 3,
    WorkspaceContext = 4,
    CovenantConfirmed = 5,
    ContextBody = 6,
    SpecialOrUncovered = 7,
    Preamble = 8,
    Instructions = 9
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaterializationContainer>))]
public enum CovenantMaterializationContainer : byte
{
    SystemPrompt = 1,
    MessagePart = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaterializationOccurrence>))]
public enum CovenantMaterializationOccurrence : byte
{
    Utf16TextRange = 1,
    WholeBinaryPart = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantMaterializationSourceRange>))]
public enum CovenantMaterializationSourceRange : byte
{
    WholeSource = 1,
    Utf16Range = 2,
    ByteRange = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCursorEndpoint>))]
public enum CovenantCursorEndpoint : byte
{
    List = 1,
    FtsQuery = 2,
    FallbackQuery = 3,
    Versions = 4
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCursorScopeSelection>))]
public enum CovenantCursorScopeSelection : byte
{
    Global = 1,
    Campaign = 2,
    AllScopes = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantLifecycle>))]
public enum CovenantLifecycle : byte
{
    Set = 1,
    Retired = 2,
    Any = 3
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantCursorSort>))]
public enum CovenantCursorSort : byte
{
    CanonicalHeads = 1,
    FtsRank = 2,
    FallbackHeads = 3,
    VersionDescending = 4
}
