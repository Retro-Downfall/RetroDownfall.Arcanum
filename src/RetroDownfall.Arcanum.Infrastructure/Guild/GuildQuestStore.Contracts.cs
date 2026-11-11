using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host resolution runs only for a new, currently authorized logical issuance.</summary>
internal sealed record GuildQuestProposal(Guid QuestId, Guid RunId, Guid SessionId,
    GuildMutationIdentity Mutation, GuildIssueRootRequest? Root, Guid? ParentQuestId, GuildIssueChildRequest? Child,
    Guid? CampaignId, Guid? WorkspaceId, Guid? CohortId = null, GuildAwardIssue? Award = null);

internal sealed record GuildQuestAdmissionPlan(GuildAdmissionContext Admission, GuildGrant Grant,
    GuildSignetOwnerBinding Owners, Guid OriginPrincipalId, GuildStoredAgentCardPayload? GeneratedCard,
    ArtifactSensitivityLabel? SourceLabel = null, IDisposable? Lifetime = null, GuildRemoteTransportDescriptor? RemoteTransport = null);

/// <summary>Authentication/visibility and immutable policy facts come from host composition, never serialized claims.</summary>
internal interface IGuildQuestHostPolicy
{
    Guid InstallationGeneration { get; }

    Task<Result> AuthorizeLocateAsync(GuildCallerContext caller, SqliteConnection connection, SqliteTransaction transaction,
        Guid? questId, Guid? sessionId, Guid? campaignId, CancellationToken cancellationToken);

    Task<Result<GuildQuestAdmissionPlan>> ResolveAsync(GuildCallerContext caller, GuildQuestProposal proposal,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>Mandatory same-transaction budget participant. No production no-op implementation is installed.</summary>
internal interface IGuildQuestAllocationParticipant
{
    Task<Result<Guid>> AllocateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
        Guid? parentAccountId, GuildGrant grant, CancellationToken cancellationToken);

    Task<Result> ExposeAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
        Guid accountId, Guid dispatchId, Guid accountingAssociationId, CancellationToken cancellationToken);

    Task<Result<GuildAccountingState>> SettleAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, Guid accountId, GuildResultReceipt receipt, CancellationToken cancellationToken);
}

internal sealed record GuildQuestStored(GuildQuestDto Quest, GuildIssuanceIdentity Issuance,
    Guid MutationId, string IntentDigest, string ResolutionDigest, Guid AllocationAccountId, bool Retired);

/// <summary>A live driver needs both the host credential and its exact current ledger lease.</summary>
internal sealed record GuildQuestDriver(GuildExecutionHandle Execution, string LeaseOwner, long OperationRevision,
    GuildCallerContext? Caller = null);

internal sealed record GuildQuestResult(GuildResultReceipt Receipt, byte[]? Body,
    ArtifactSensitivityLabel? SourceLabel = null, bool NoDispatchProven = false, bool WorkStoppedProven = false, GuildRemoteResultEvidence? RemoteEvidence = null,
    int? DesignatedResultCount = null, byte[]? DesignatedResultUtf8 = null, string? DesignatedResultMimeType = null);

internal sealed record GuildQuestDispatchRecord(Guid DispatchId, Guid QuestId, string InvocationDigest,
    Guid AccountingAssociationId, bool IsContinuation, GuildDispatchEvidence Evidence, Guid? LocalChildId,
    string? RemoteTaskId, string? RemoteContextId, long Revision);

internal sealed record GuildQuestDispatchAdmission(GuildQuestDispatchRecord Dispatch, bool MayDispatch);

/// <summary>Host adapter evidence for one already journaled dispatch; it cannot set execution state.</summary>
internal sealed record GuildQuestDispatchEvidence(Guid DispatchId, long ExpectedRevision, GuildDispatchEvidence Evidence,
    Guid? LocalChildId = null, string? RemoteTaskId = null, string? RemoteContextId = null, bool NoDispatchProven = false);

/// <summary>Host-resolved award identity; every field is revalidated in the issuing transaction.</summary>
internal sealed record GuildAwardIssue(
    Guid OfferId,
    Guid BidId,
    Guid CohortId,
    Guid RootQuestId,
    Guid IssuerQuestId,
    long ExpectedOfferRevision,
    long ExpectedCohortRevision,
    Guid MemberId,
    Guid PrimaryQuestId,
    Guid PrimarySignetId,
    long PrimaryRevocationGeneration,
    Guid AgentId,
    Guid CardId,
    long CardRevision,
    GuildMutationIdentity Mutation,
    AwardWorkRequest Request,
    bool BudgetHint,
    GuildGrant PrimaryCeiling);
