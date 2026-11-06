using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<QuestState>))]
public enum QuestState
{
    [JsonStringEnumMemberName("submitted")]
    Submitted = 0,

    [JsonStringEnumMemberName("working")]
    Working = 1,

    [JsonStringEnumMemberName("input-required")]
    InputRequired = 2,

    [JsonStringEnumMemberName("completed")]
    Completed = 3,

    [JsonStringEnumMemberName("failed")]
    Failed = 4,

    [JsonStringEnumMemberName("canceled")]
    Canceled = 5,

    [JsonStringEnumMemberName("rejected")]
    Rejected = 6,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildAgentSourceKind>))]
public enum GuildAgentSourceKind
{
    [JsonStringEnumMemberName("declaredGlobal")]
    DeclaredGlobal = 0,

    [JsonStringEnumMemberName("declaredWorkspace")]
    DeclaredWorkspace = 1,

    [JsonStringEnumMemberName("derived")]
    Derived = 2,

    [JsonStringEnumMemberName("remote")]
    Remote = 3,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildDeliveryState>))]
public enum GuildDeliveryState
{
    [JsonStringEnumMemberName("none")]
    None = 0,

    [JsonStringEnumMemberName("pending")]
    Pending = 1,

    [JsonStringEnumMemberName("applied")]
    Applied = 2,

    [JsonStringEnumMemberName("withheld")]
    Withheld = 3,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildAccountingState>))]
public enum GuildAccountingState
{
    [JsonStringEnumMemberName("settled")]
    Settled = 0,

    [JsonStringEnumMemberName("pending")]
    Pending = 1,

    [JsonStringEnumMemberName("uncertain")]
    Uncertain = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWaitReason>))]
public enum GuildWaitReason
{
    [JsonStringEnumMemberName("input")]
    Input = 0,

    [JsonStringEnumMemberName("maintenance")]
    Maintenance = 1,

    [JsonStringEnumMemberName("budget")]
    Budget = 2,

    [JsonStringEnumMemberName("transport")]
    Transport = 3,

    [JsonStringEnumMemberName("reconciliation")]
    Reconciliation = 4,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildContentKind>))]
public enum GuildContentKind
{
    [JsonStringEnumMemberName("agentCard")]
    AgentCard = 0,

    [JsonStringEnumMemberName("questGoal")]
    QuestGoal = 1,

    [JsonStringEnumMemberName("questResult")]
    QuestResult = 2,

    [JsonStringEnumMemberName("circleBody")]
    CircleBody = 3,

    [JsonStringEnumMemberName("cohortSpecification")]
    CohortSpecification = 4,

    [JsonStringEnumMemberName("cohortContribution")]
    CohortContribution = 5,

    [JsonStringEnumMemberName("offer")]
    Offer = 6,

    [JsonStringEnumMemberName("bid")]
    Bid = 7,

    [JsonStringEnumMemberName("cohortLedgerEntry")]
    CohortLedgerEntry = 8,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildMessageKind>))]
public enum GuildMessageKind
{
    [JsonStringEnumMemberName("message")]
    Message = 0,

    [JsonStringEnumMemberName("continuation")]
    Continuation = 1,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildCancellationReason>))]
public enum GuildCancellationReason
{
    [JsonStringEnumMemberName("operatorRequested")]
    OperatorRequested = 0,

    [JsonStringEnumMemberName("ownerDeleted")]
    OwnerDeleted = 1,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildProducerKind>))]
public enum GuildProducerKind
{
    [JsonStringEnumMemberName("run")]
    Run = 0,

    [JsonStringEnumMemberName("operator")]
    Operator = 1,

    [JsonStringEnumMemberName("hostReceipt")]
    HostReceipt = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildReceiptSourceKind>))]
public enum GuildReceiptSourceKind
{
    [JsonStringEnumMemberName("questIssue")]
    QuestIssue = 0,

    [JsonStringEnumMemberName("result")]
    Result = 1,

    [JsonStringEnumMemberName("settlement")]
    Settlement = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildCircleAddressKind>))]
public enum GuildCircleAddressKind
{
    [JsonStringEnumMemberName("parent")]
    Parent = 0,

    [JsonStringEnumMemberName("child")]
    Child = 1,

    [JsonStringEnumMemberName("operator")]
    Operator = 2,

    [JsonStringEnumMemberName("cohort")]
    Cohort = 3,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildEnvelopeKind>))]
public enum GuildEnvelopeKind
{
    [JsonStringEnumMemberName("quest.issued")]
    QuestIssued = 0,

    [JsonStringEnumMemberName("progress")]
    Progress = 1,

    [JsonStringEnumMemberName("result")]
    Result = 2,

    [JsonStringEnumMemberName("intervention")]
    Intervention = 3,

    [JsonStringEnumMemberName("escalation")]
    Escalation = 4,

    [JsonStringEnumMemberName("settle")]
    Settle = 5,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildEventKind>))]
public enum GuildEventKind
{
    [JsonStringEnumMemberName("questChanged")]
    QuestChanged = 0,

    [JsonStringEnumMemberName("messageCommitted")]
    MessageCommitted = 1,

    [JsonStringEnumMemberName("contentErased")]
    ContentErased = 2,

    [JsonStringEnumMemberName("signetRevoked")]
    SignetRevoked = 3,

    [JsonStringEnumMemberName("cohortChanged")]
    CohortChanged = 4,

    [JsonStringEnumMemberName("membershipChanged")]
    MembershipChanged = 5,

    [JsonStringEnumMemberName("contributionAccepted")]
    ContributionAccepted = 6,

    [JsonStringEnumMemberName("offerChanged")]
    OfferChanged = 7,

    [JsonStringEnumMemberName("bidChanged")]
    BidChanged = 8,

    [JsonStringEnumMemberName("awardCommitted")]
    AwardCommitted = 9,

    [JsonStringEnumMemberName("noteAppended")]
    NoteAppended = 10,

    [JsonStringEnumMemberName("leaseChanged")]
    LeaseChanged = 11,

    [JsonStringEnumMemberName("spendChanged")]
    SpendChanged = 12,
}
