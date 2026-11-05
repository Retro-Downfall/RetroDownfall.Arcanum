using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Host authentication provenance, not a model-selectable authority label.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildCallerKind>))]
public enum GuildCallerKind
{
    [JsonStringEnumMemberName("operator")] Operator,

    [JsonStringEnumMemberName("run")] Run,

    [JsonStringEnumMemberName("peer")] Peer,

    [JsonStringEnumMemberName("hostReceipt")] HostReceipt,
}

/// <summary>Exact protected action to which a future live verification is bound.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildAuthorityAction>))]
public enum GuildAuthorityAction
{
    [JsonStringEnumMemberName("issue")] Issue,

    [JsonStringEnumMemberName("start")] Start,

    [JsonStringEnumMemberName("continue")] Continue,

    [JsonStringEnumMemberName("cancel")] Cancel,

    [JsonStringEnumMemberName("circlePublish")] CirclePublish,

    [JsonStringEnumMemberName("circleRead")] CircleRead,

    [JsonStringEnumMemberName("toolAdvertise")] ToolAdvertise,

    [JsonStringEnumMemberName("toolDispatch")] ToolDispatch,

    [JsonStringEnumMemberName("workspaceRead")] WorkspaceRead,

    [JsonStringEnumMemberName("workspaceMutate")] WorkspaceMutate,

    [JsonStringEnumMemberName("memoryRead")] MemoryRead,

    [JsonStringEnumMemberName("memoryAppend")] MemoryAppend,

    [JsonStringEnumMemberName("memoryDelete")] MemoryDelete,

    [JsonStringEnumMemberName("providerDispatch")] ProviderDispatch,

    [JsonStringEnumMemberName("remoteDispatch")] RemoteDispatch,

    [JsonStringEnumMemberName("resultApply")] ResultApply,

    [JsonStringEnumMemberName("revoke")] Revoke,

    [JsonStringEnumMemberName("recoveryRebind")] RecoveryRebind,

    [JsonStringEnumMemberName("leaseAcquire")] LeaseAcquire,

    [JsonStringEnumMemberName("leaseRenew")] LeaseRenew,

    [JsonStringEnumMemberName("leaseRelease")] LeaseRelease,

    [JsonStringEnumMemberName("cohortManage")] CohortManage,

    [JsonStringEnumMemberName("spendRead")] SpendRead,
}

/// <summary>
/// Reserved trusted-composition handle. No implementation in this dormant slice authenticates a
/// principal, issues a Signet or turns a serialized record into this context.
/// </summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildCallerContext>))]
public sealed class GuildCallerContext
{
    internal GuildCallerContext(GuildCallerKind kind, Guid principalId, Guid invocationId, Guid installationGeneration,
        GuildExecutionHandle? execution = null, string? peerGrantRevision = null, Guid? receiptId = null, string? receiptSource = null,
        string? peerId = null)
    {
        bool validVariant = kind switch
        {
            GuildCallerKind.Operator => execution is null && peerGrantRevision is null && peerId is null && receiptId is null && receiptSource is null,
            GuildCallerKind.Run => execution is not null && peerGrantRevision is null && peerId is null && receiptId is null && receiptSource is null,
            GuildCallerKind.Peer => execution is null && !string.IsNullOrWhiteSpace(peerGrantRevision) && !string.IsNullOrWhiteSpace(peerId)
                && receiptId is null && receiptSource is null,
            GuildCallerKind.HostReceipt => execution is null && peerGrantRevision is null && peerId is null
                && receiptId is { } id && id != Guid.Empty && !string.IsNullOrWhiteSpace(receiptSource),
            _ => false,
        };

        if (!validVariant || (kind == GuildCallerKind.Peer ? principalId != Guid.Empty : principalId == Guid.Empty)
            || invocationId == Guid.Empty || installationGeneration == Guid.Empty)
        {
            throw new ArgumentException("The trusted Guild caller binding is incomplete.");
        }

        Kind = kind;

        PrincipalId = kind == GuildCallerKind.Peer ? null : principalId;

        InvocationId = invocationId;

        InstallationGeneration = installationGeneration;

        Execution = execution;

        PeerGrantRevision = peerGrantRevision;

        PeerId = peerId;

        ReceiptId = receiptId;

        ReceiptSource = receiptSource;
    }

    internal GuildCallerContext(string peerId, string peerGrantRevision, Guid invocationId, Guid installationGeneration)
        : this(GuildCallerKind.Peer, Guid.Empty, invocationId, installationGeneration, peerGrantRevision: peerGrantRevision, peerId: peerId)
    {
    }

    public GuildCallerKind Kind { get; }

    public Guid? PrincipalId { get; }

    public Guid InvocationId { get; }

    public Guid InstallationGeneration { get; }

    public GuildExecutionHandle? Execution { get; }

    public string? PeerGrantRevision { get; }

    public string? PeerId { get; }

    public Guid? ReceiptId { get; }

    public string? ReceiptSource { get; }
}

/// <summary>Host-only binding to one credential generation. Contains no bearer or cryptographic key.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildExecutionHandle>))]
public sealed class GuildExecutionHandle
{
    internal GuildExecutionHandle(Guid signetId, Guid agentId, Guid runId, Guid commissionId, Guid rootCommissionId,
        long credentialGeneration, long revocationGeneration, Guid? issuerSignetId = null)
    {
        if (signetId == Guid.Empty || agentId == Guid.Empty || runId == Guid.Empty || commissionId == Guid.Empty
            || rootCommissionId == Guid.Empty || credentialGeneration <= 0 || revocationGeneration < 0
            || (commissionId == rootCommissionId ? issuerSignetId is not null : issuerSignetId is null || issuerSignetId == Guid.Empty))
        {
            throw new ArgumentException("The trusted Guild execution binding is incomplete.");
        }

        SignetId = signetId;

        AgentId = agentId;

        RunId = runId;

        CommissionId = commissionId;

        RootCommissionId = rootCommissionId;

        CredentialGeneration = credentialGeneration;

        RevocationGeneration = revocationGeneration;

        IssuerSignetId = issuerSignetId;
    }

    public Guid SignetId { get; }

    public Guid AgentId { get; }

    public Guid RunId { get; }

    public Guid CommissionId { get; }

    public Guid RootCommissionId { get; }

    public long CredentialGeneration { get; }

    public long RevocationGeneration { get; }

    public Guid? IssuerSignetId { get; }
}

/// <summary>Reserved proof of an exact action/resource check; not itself a live verifier.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildVerifiedAuthority>))]
public sealed class GuildVerifiedAuthority
{
    internal GuildVerifiedAuthority(GuildExecutionHandle execution, GuildAuthorityAction action, Guid resourceId, long authorityGeneration)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (!Enum.IsDefined(action) || resourceId == Guid.Empty || authorityGeneration <= 0)
        {
            throw new ArgumentException("The verified Guild action binding is incomplete.");
        }

        Execution = execution;

        Action = action;

        ResourceId = resourceId;

        AuthorityGeneration = authorityGeneration;
    }

    internal GuildVerifiedAuthority(GuildCallerContext caller, GuildAuthorityAction action, Guid resourceId, long authorityGeneration)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.Kind != GuildCallerKind.Operator || !Enum.IsDefined(action) || resourceId == Guid.Empty || authorityGeneration <= 0)
        {
            throw new ArgumentException("Operator authority requires its authenticated native principal and exact action binding.");
        }

        OperatorContext = caller;

        Action = action;

        ResourceId = resourceId;

        AuthorityGeneration = authorityGeneration;
    }

    public GuildExecutionHandle? Execution { get; }

    public GuildCallerContext? OperatorContext { get; }

    public GuildAuthorityAction Action { get; }

    public Guid ResourceId { get; }

    public long AuthorityGeneration { get; }
}

/// <summary>Coordinator-only recovery binding; never permission to replay an uncertain effect.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildRecoveryContext>))]
public sealed class GuildRecoveryContext
{
    internal GuildRecoveryContext(Guid commissionId, Guid operationId, long expectedRevision)
    {
        if (commissionId == Guid.Empty || operationId == Guid.Empty || expectedRevision < 0)
        {
            throw new ArgumentException("The trusted Guild recovery binding is incomplete.");
        }

        CommissionId = commissionId;

        OperationId = operationId;

        ExpectedRevision = expectedRevision;
    }

    public Guid CommissionId { get; }

    public Guid OperationId { get; }

    public long ExpectedRevision { get; }
}

/// <summary>Coordinator-only atomic store admission, with authenticated caller and canonical intent.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildAdmissionContext>))]
public sealed class GuildAdmissionContext
{
    internal GuildAdmissionContext(GuildCallerContext caller, Guid admissionId, GuildMutationIdentity mutation)
    {
        ArgumentNullException.ThrowIfNull(caller);

        ArgumentNullException.ThrowIfNull(mutation);

        if (admissionId == Guid.Empty)
        {
            throw new ArgumentException("An admission requires its exact host identity.", nameof(admissionId));
        }

        Caller = caller;

        AdmissionId = admissionId;

        Mutation = mutation;
    }

    public GuildCallerContext Caller { get; }

    public Guid AdmissionId { get; }

    public GuildMutationIdentity Mutation { get; }
}

/// <summary>Internal effect-delivery claim, distinct from any public subscription cursor.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildInboxClaim>))]
public sealed class GuildInboxClaim
{
    internal GuildInboxClaim(Guid envelopeId, Guid recipientCommissionId, Guid operationId, long leaseRevision, long inboxRevision)
    {
        if (envelopeId == Guid.Empty || recipientCommissionId == Guid.Empty || operationId == Guid.Empty
            || leaseRevision < 0 || inboxRevision < 0)
        {
            throw new ArgumentException("The trusted Guild inbox claim is incomplete.");
        }

        EnvelopeId = envelopeId;

        RecipientCommissionId = recipientCommissionId;

        OperationId = operationId;

        LeaseRevision = leaseRevision;

        InboxRevision = inboxRevision;
    }

    public Guid EnvelopeId { get; }

    public Guid RecipientCommissionId { get; }

    public Guid OperationId { get; }

    public long LeaseRevision { get; }

    public long InboxRevision { get; }
}

/// <summary>Reserved process-local mutation binding, never a serialized lease credential.</summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildMutationPermit>))]
public sealed class GuildMutationPermit
{
    internal GuildMutationPermit(Guid leaseId, long epoch, GuildVerifiedAuthority authority, string effectPlanDigest)
    {
        ArgumentNullException.ThrowIfNull(authority);

        if (leaseId == Guid.Empty || epoch <= 0 || string.IsNullOrWhiteSpace(effectPlanDigest))
        {
            throw new ArgumentException("The trusted Guild mutation permit is incomplete.");
        }

        LeaseId = leaseId;

        Epoch = epoch;

        Authority = authority;

        EffectPlanDigest = effectPlanDigest;
    }

    public Guid LeaseId { get; }

    public long Epoch { get; }

    public GuildVerifiedAuthority Authority { get; }

    public string EffectPlanDigest { get; }
}

/// <summary>Defense in depth: even explicitly configured JSON metadata cannot disclose or mint a host handle.</summary>
public sealed class GuildHostHandleJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Guild host authority has no JSON representation.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Guild host authority has no JSON representation.");
}
