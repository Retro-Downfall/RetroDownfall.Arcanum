using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantOperationalDefaultKind>))]
public enum CovenantOperationalDefaultKind : byte
{
    Temperature = 1,
    TopP = 2
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantOperationalDefaultConsumer>))]
public enum CovenantOperationalDefaultConsumer : byte
{
    InferenceSampling = 1
}

/// <summary>One available sampling default and its exact Confirmed Covenant source.</summary>
public sealed record CovenantOperationalDefault
{
    internal CovenantOperationalDefault(
        CovenantOperationalDefaultKind kind,
        CovenantOperationalDefaultConsumer consumer,
        float value,
        uint policyVersion,
        Guid entryId,
        Guid versionId,
        ulong revision,
        CovenantScope scope,
        Guid? campaignId)
    {
        Kind = kind;

        Consumer = consumer;

        Value = value;

        PolicyVersion = policyVersion;

        EntryId = entryId;

        VersionId = versionId;

        Revision = revision;

        Scope = scope;

        CampaignId = campaignId;
    }

    public CovenantOperationalDefaultKind Kind { get; }

    public CovenantOperationalDefaultConsumer Consumer { get; }

    public float Value { get; }

    public uint PolicyVersion { get; }

    public Guid EntryId { get; }

    public Guid VersionId { get; }

    public ulong Revision { get; }

    public CovenantScope Scope { get; }

    public Guid? CampaignId { get; }
}
