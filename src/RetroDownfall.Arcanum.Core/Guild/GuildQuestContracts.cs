using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

public sealed record GuildMutationReceiptDto(Guid Id, Guid IdempotencyKey, bool Replayed, DateTimeOffset? AcceptedAt, long AcceptedRevision);

/// <summary>Immutable content locator and integrity observation; not a disclosure capability.</summary>
public sealed record GuildDataRef(Guid ContentId, GuildContentKind Kind, long Revision, string Digest, string MimeType, long ByteLength, bool ContentErased);

public sealed record GuildContentDto(GuildDataRef Reference, string Body);

public sealed record GuildQuestInputDto(Guid QuestId, long Revision, GuildQuestInputEvidence AwaitingInput, string Question);

public sealed record GuildIssueRootRequest(Guid IdempotencyKey, Guid? AgentId, string? Name, string Goal, GuildGrant Grant, Guid? SessionId = null, Guid? CampaignId = null, Guid? WorkspaceId = null, Guid? PredecessorQuestId = null, GuildRemoteInvocationOptions? RemoteOptions = null);

public sealed record GuildIssueChildData(Guid? AgentId, string? Name, string Goal, GuildRequestedGrant? RequestedGrant);

public sealed record GuildIssueChildRequest(Guid IdempotencyKey, long ExpectedRevision, Guid? AgentId, string? Name, string Goal, GuildRequestedGrant? RequestedGrant, GuildRemoteInvocationOptions? RemoteOptions = null, Guid? CardId = null);

public sealed record GuildSendOperatorMessageRequest(Guid IdempotencyKey, long ExpectedRevision, GuildMessageKind Kind, string MimeType, string Body);

public sealed record GuildCancelQuestRequest(Guid IdempotencyKey, long ExpectedRevision, GuildCancellationReason ReasonCode);

public sealed record GuildListQuestsQuery(Guid? RootId = null, Guid? ParentId = null, QuestState? State = null, string? Cursor = null, int PageSize = 100);

public sealed record GuildPageQuery(string? Cursor = null, int PageSize = 100);

public sealed record GuildQuestDto(
    Guid Id,
    string? Name,
    Guid OperationId,
    Guid AgentId,
    Guid CardId,
    long CardRevision,
    Guid RunId,
    Guid? ParentQuestId,
    Guid RootQuestId,
    Guid? SessionId,
    Guid? CampaignId,
    Guid? WorkspaceId,
    Guid SignetId,
    long SignetRevision,
    long Revision,
    long IssuedSequence,
    QuestState State,
    GuildWaitReason? WaitReason,
    bool CancellationRequested,
    bool CancellationConverged,
    bool ReconciliationRequired,
    GuildDeliveryState DeliveryState,
    GuildAccountingState AccountingState,
    GuildDataRef GoalRef,
    GuildDataRef? ResultRef,
    Guid? ResultReceiptId,
    GuildGrant ResolvedGrant,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long EventWatermark,
    GuildMutationReceiptDto? Receipt = null,
    GuildQuestInputEvidence? AwaitingInput = null,
    string? ReasonCode = null,
    ToolNameDiagnostic[]? ToolDiagnostics = null);

public sealed record GuildCancellationDto(Guid QuestId, Guid RootQuestId, long Revision, bool CancellationRequested, bool CancellationConverged, long PendingExecutionCount, long UnresolvedExternalEffectCount, GuildAccountingState AccountingState, long EventWatermark, GuildMutationReceiptDto? Receipt = null);

/// <summary>Physical usage and separate remaining authority; unknown dimensions never imply measured zero.</summary>
public sealed record GuildSpendDto(Guid QuestId, decimal KnownUsd, long KnownTokens, bool UnknownUsd, bool UnknownTokens, long PricedCount, long UnpricedCount, long UncertainCount, GuildBudgetGrant OutstandingAllocation, GuildBudgetGrant UnresolvedHolds, GuildBudgetGrant Remaining, long EventWatermark);

public sealed record GuildMessageReceipt(Guid QuestId, Guid MessageId, long Revision, GuildDeliveryState DeliveryState, long EventWatermark, GuildMutationReceiptDto? Receipt = null);

public sealed record GuildRevokeSignetRequest(Guid IdempotencyKey, long ExpectedRevision, GuildCancellationReason ReasonCode);

public sealed record SignetRevocationDto(Guid SignetId, long Revision, long Generation, bool Revoked, bool CancellationRequested, bool CancellationConverged, long EventWatermark, GuildMutationReceiptDto? Receipt = null);
