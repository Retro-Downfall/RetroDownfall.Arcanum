using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Host-canonical caller intent. A digest proves integrity, never caller authority.</summary>
public sealed record GuildMutationIdentity(Guid IdempotencyKey, string CanonicalRequestDigest);

/// <summary>The durable logical issuance key excludes generated children and retry counters.</summary>
public sealed record GuildIssuanceIdentity(
    Guid InstallationGeneration,
    Guid? IssuerQuestId,
    Guid? RootInvocationId,
    Guid IssuingStepId,
    Guid LogicalCallId);

/// <summary>Resolved identities committed together with the Quest, its LRO and grant allocation.</summary>
public sealed record GuildAdmissionRequest(
    GuildMutationIdentity Mutation,
    GuildIssuanceIdentity Issuance,
    GuildQuestDto Quest,
    GuildDataRef Goal,
    string AcceptedResolutionDigest,
    long? ExpectedIssuerRevision);

public sealed record GuildAdmissionReceipt(
    Guid QuestId,
    Guid OperationId,
    Guid SignetId,
    Guid AllocationAccountId,
    GuildMutationReceiptDto Receipt);

public sealed record GuildResultReceipt(
    Guid QuestId,
    Guid ResultId,
    string CanonicalContentDigest,
    GuildDataRef? Content,
    QuestState Outcome,
    string? ReasonCode);

public sealed record GuildCommitResultRequest(
    GuildMutationIdentity Mutation,
    long ExpectedQuestRevision,
    long ExpectedOperationRevision,
    GuildResultReceipt Result);

public sealed record GuildResultApplicationRequest(
    GuildMutationIdentity Mutation,
    Guid RecipientQuestId,
    Guid QuestId,
    Guid ResultId,
    string CanonicalContentDigest,
    long ExpectedRecipientRevision);

public sealed record GuildResultApplicationReceipt(
    Guid RecipientQuestId,
    Guid QuestId,
    Guid ResultId,
    string CanonicalContentDigest,
    long RecipientRevision,
    GuildMutationReceiptDto Receipt);

/// <summary>Dispatch evidence is independent of the public execution projection.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildDispatchEvidence>))]
public enum GuildDispatchEvidence
{
    [JsonStringEnumMemberName("prepared")]
    Prepared = 1,

    [JsonStringEnumMemberName("possiblyDispatched")]
    PossiblyDispatched = 2,

    [JsonStringEnumMemberName("confirmed")]
    Confirmed = 3,

    [JsonStringEnumMemberName("resolvedNoDispatch")]
    ResolvedNoDispatch = 4
}

/// <summary>Data bound to committed work; an execution handle is separately required by the port.</summary>
public sealed record GuildDispatchRequest(
    GuildMutationIdentity Mutation,
    Guid QuestId,
    Guid OperationId,
    Guid RunId,
    Guid DispatchId,
    long ExpectedOperationRevision,
    string InvocationDigest);

public sealed record GuildDispatchObservation(
    Guid QuestId,
    Guid DispatchId,
    GuildDispatchEvidence Evidence,
    QuestState State,
    bool ReconciliationRequired,
    string? ReasonCode,
    Guid? LocalChildId,
    string? RemoteTaskId,
    string? RemoteContextId);

public sealed record GuildContinueRequest(
    Guid IdempotencyKey,
    long ExpectedRevision,
    Guid ContinuationId,
    string MimeType,
    string Body,
    GuildRemoteInvocationOptions? RemoteOptions = null);

public sealed record GuildCircleInboxQuery(Guid RecipientQuestId, string? Cursor, int PageSize = 100);

public sealed record GuildCircleInboxItem(
    Guid InboxId,
    GuildEnvelope? Envelope,
    long Revision,
    GuildDeliveryState DeliveryState = GuildDeliveryState.Pending,
    string? ReasonCode = null,
    Guid? EnvelopeId = null);

public sealed record GuildCircleInboxPage(
    ImmutableArray<GuildCircleInboxItem> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record GuildCircleAcknowledgeRequest(
    GuildMutationIdentity Mutation,
    Guid ApplicationReceiptId,
    string ApplicationDigest,
    long ExpectedInboxRevision);

public sealed record GuildCircleAcknowledgement(
    Guid InboxId,
    Guid ApplicationReceiptId,
    long Revision,
    GuildMutationReceiptDto Receipt);

public sealed record GuildEventQuery(Guid RootQuestId, Guid? SubtreeQuestId, Guid? CohortId, string? Cursor, int PageSize = 100);

public sealed record GuildEventPage(
    ImmutableArray<GuildChangeEventDto> Items,
    string? NextCursor,
    long SnapshotSequence,
    string StateConsistency = "livePerPage");

public sealed record GuildCohortContributionRequest(
    GuildMutationIdentity Mutation,
    Guid CohortId,
    Guid MemberId,
    long ExpectedRevision,
    GuildResultReceipt Result);

/// <summary>The complete effect plan is data; only the host broker may issue its mutation permit.</summary>
public sealed record GuildMutationPlan(Guid WorkspaceId, ImmutableArray<GuildPathScope> Scopes, string EffectPlanDigest);

public sealed record GuildActionRequest(GuildAuthorityAction Action, Guid ResourceId);

public sealed record GuildSignetBinding(
    Guid SignetId,
    Guid AgentId,
    Guid RunId,
    Guid QuestId,
    Guid RootQuestId,
    Guid? IssuerSignetId,
    GuildGrant Grant,
    DateTimeOffset IssuedAtUtc,
    Guid OriginPrincipalId);

public sealed record GuildSignetStatus(
    Guid SignetId,
    long Revision,
    long CredentialGeneration,
    long RevocationGeneration,
    bool Revoked,
    bool RestoredInert);
