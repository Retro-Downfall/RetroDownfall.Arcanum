using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>
/// Tagged attribution data, never a verified caller. Each variant has exactly its own identity fields.
/// Producers and envelopes are host-stamped after admission, not accepted as runtime authority.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuildProducerIdentity(
    GuildProducerKind Kind,
    Guid? AgentId,
    Guid? RunId,
    Guid? QuestId,
    string? OperatorPrincipalId,
    Guid? DomainOwnerId,
    GuildReceiptSourceKind? SourceKind,
    Guid? ReceiptId)
{
    public GuildProducerKind Kind { get; } = Validate(Kind, AgentId, RunId, QuestId, OperatorPrincipalId, DomainOwnerId, SourceKind, ReceiptId);

    public Guid? AgentId { get; } = AgentId;

    public Guid? RunId { get; } = RunId;

    public Guid? QuestId { get; } = QuestId;

    public string? OperatorPrincipalId { get; } = OperatorPrincipalId;

    public Guid? DomainOwnerId { get; } = DomainOwnerId;

    public GuildReceiptSourceKind? SourceKind { get; } = SourceKind;

    public Guid? ReceiptId { get; } = ReceiptId;

    public GuildProducerStreamKey StreamKey(Guid questId)
    {
        if (questId == Guid.Empty || (Kind == GuildProducerKind.Run && QuestId != questId))
            throw new ArgumentException("The producer stream requires its exact Quest.", nameof(questId));

        return new(Kind, AgentId, RunId, OperatorPrincipalId, DomainOwnerId, SourceKind, questId);
    }

    public GuildPublishDedupeKey DedupeKey(Guid questId, Guid producerRequestId)
    {
        if (producerRequestId == Guid.Empty || (Kind == GuildProducerKind.HostReceipt && producerRequestId != ReceiptId))
            throw new ArgumentException("A host receipt publication retains the immutable receipt identity.", nameof(producerRequestId));

        return new(StreamKey(questId), producerRequestId);
    }

    private static GuildProducerKind Validate(GuildProducerKind kind, Guid? agentId, Guid? runId, Guid? questId, string? operatorPrincipalId, Guid? domainOwnerId, GuildReceiptSourceKind? sourceKind, Guid? receiptId)
    {
        bool runFields = agentId is { } agent && agent != Guid.Empty && runId is { } run && run != Guid.Empty && questId is { } quest && quest != Guid.Empty;

        bool noRunFields = agentId is null && runId is null && questId is null;

        bool noReceiptFields = domainOwnerId is null && sourceKind is null && receiptId is null;

        bool valid = kind switch
        {
            GuildProducerKind.Run => runFields && operatorPrincipalId is null && noReceiptFields,
            GuildProducerKind.Operator => noRunFields && !string.IsNullOrWhiteSpace(operatorPrincipalId) && noReceiptFields,
            GuildProducerKind.HostReceipt => noRunFields && operatorPrincipalId is null && domainOwnerId is { } owner && owner != Guid.Empty && sourceKind is { } source && Enum.IsDefined(source) && receiptId is { } receipt && receipt != Guid.Empty,
            _ => false
        };

        if (!valid)
            throw new ArgumentException("Producer identity fields must match exactly one tagged variant.", nameof(kind));

        return kind;
    }
}

/// <summary>HostReceipt stream ordering is shared by owner/source across individual receipts.</summary>
public sealed record GuildProducerStreamKey(GuildProducerKind Kind, Guid? AgentId, Guid? RunId, string? OperatorPrincipalId, Guid? DomainOwnerId, GuildReceiptSourceKind? SourceKind, Guid QuestId);

/// <summary>The immutable domain receipt remains part of HostReceipt deduplication.</summary>
public sealed record GuildPublishDedupeKey(GuildProducerStreamKey Stream, Guid ProducerRequestId);

public sealed record GuildCircleAddress(GuildCircleAddressKind Kind, Guid? QuestId, Guid? CohortId, long? MembershipVersion);

/// <summary>Intent only. Caller, producer, relationship and resolved fan-out are supplied by verified host context.</summary>
public sealed record GuildPublishRequest(Guid IdempotencyKey, GuildCircleAddress Address, GuildEnvelopeKind Kind, string MimeType, string Body, long? ExpectedRevision);

public sealed record GuildEnvelope(Guid Id, GuildProducerIdentity Producer, Guid QuestId, Guid RootQuestId, long? AuthorityGeneration, Guid? SessionId, Guid? CampaignId, GuildAuthorityKind Authority, GuildEnvelopeKind Kind, GuildDataRef ContentRef, long SenderSequence, DateTimeOffset CreatedAt, GuildCircleAddress Address);
