using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildStoredAgentCardPayload(GuildAgentCard Card, string Guidance, ImmutableArray<string> RequestedTools,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GuildRemoteTransportDescriptor? RemoteTransport = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiscoveryEndpoint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RequestedToolsSpecified = null);

internal sealed record GuildCardWrite(string SourceIdentity, Guid? ExplicitAgentId, Guid? SessionId, Guid? CampaignId,
    GuildStoredAgentCardPayload Payload, string SourceDigest, long? ExpectedRevision = null, ArtifactSensitivityLabel? Label = null, string? WorkspaceRoot = null);

internal sealed record GuildStoredCard(GuildStoredAgentCardPayload Payload, GuildDataRef Content,
    string SourceIdentity, Guid? SessionId, Guid? CampaignId);

internal sealed record GuildStoredCardPage(ImmutableArray<GuildStoredCard> Items, string? NextCardId, long SnapshotSequence);

internal sealed record GuildCardReadScope(Guid? WorkspaceId, Guid? SessionId, Guid? CampaignId, bool IncludeInstallation = true, string? WorkspaceRoot = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildStoredAgentCardPayload))]
[JsonSerializable(typeof(GuildLocalExecutionInput))]
[JsonSerializable(typeof(GuildLocalExecutionCardInput))]
[JsonSerializable(typeof(GuildRemoteExecutionInput))]
[JsonSerializable(typeof(GuildLocalWaitSnapshot))]
internal sealed partial class GuildStorageJsonContext : JsonSerializerContext;

internal sealed record GuildContentWrite(Guid ContentId, Guid SourceRecordId, GuildContentKind Kind, long Revision,
    Guid? SessionId, Guid? CampaignId, string MimeType, byte[] Body, DerivedArtifactWrite Sensitivity);
