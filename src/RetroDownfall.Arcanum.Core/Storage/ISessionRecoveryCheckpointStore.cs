using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionRecoveryKind>))]
public enum SessionRecoveryKind
{
    [JsonStringEnumMemberName("awaitingInput")]
    AwaitingInput = 1,

    [JsonStringEnumMemberName("reconciliationRequired")]
    ReconciliationRequired,

    [JsonStringEnumMemberName("interrupted")]
    Interrupted,
}

/// <summary>The public, current Session evidence. Question text is disclosed through its existing transcript owner.</summary>
public sealed record SessionRecoveryCheckpoint(Guid Id, Guid SessionId, Guid AssistantEntryId, SessionRecoveryKind Kind,
    string Provider, string Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? InvocationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? LastVerifiedInvocationId = null)
{
    public TurnRecoveryAction NextAction => Kind switch
    {
        SessionRecoveryKind.AwaitingInput when InvocationId is null => TurnRecoveryAction.ProvideInput,
        SessionRecoveryKind.ReconciliationRequired or SessionRecoveryKind.AwaitingInput => TurnRecoveryAction.ReconcileTool,
        _ => TurnRecoveryAction.ResumeSession,
    };
}

public sealed record SessionRecoveryScope(string Provider, string Model, CovenantDigest ProviderRouteDigest, Guid? CampaignId,
    string? Workspace, CovenantDigest? WorkspaceIdentity);

public sealed record SessionRecoveryCheckpointRecord(Guid Id, Guid SessionId, Guid AssistantEntryId, Guid ClaimId,
    SessionRecoveryKind Kind, SessionRecoveryScope Scope, Guid? QuestionEntryId, Guid? InvocationId,
    Guid? LastVerifiedInvocationId, Guid? ConsumingClaimId)
{
    public SessionRecoveryCheckpoint ToPublic(string? question = null) =>
        new(Id, SessionId, AssistantEntryId, Kind, Scope.Provider, Scope.Model, question, InvocationId, LastVerifiedInvocationId);
}

public sealed record SessionRecoveryReadSnapshot(SessionRecoveryCheckpointRecord? Checkpoint, InterruptedToolEffectPage InterruptedEffects);

public interface ISessionRecoveryCheckpointStore
{
    Task<Result<SessionRecoveryCheckpointRecord>> SaveAsync(SessionTurnClaimLease owner,
        SessionRecoveryCheckpointRecord checkpoint, CancellationToken cancellationToken);

    Task<Result<SessionRecoveryCheckpointRecord?>> FindAsync(Guid sessionId, Guid? checkpointId, CancellationToken cancellationToken);

    Task<Result<SessionRecoveryReadSnapshot>> ReadStatusAsync(Guid sessionId, Guid? after, CancellationToken cancellationToken);
}
