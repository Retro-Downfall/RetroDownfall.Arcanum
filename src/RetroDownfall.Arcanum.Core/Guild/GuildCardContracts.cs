using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Immutable discovery metadata; capabilities and source observations confer no authority.</summary>
public sealed record GuildAgentCard(
    Guid AgentId,
    Guid CardId,
    long Revision,
    string Name,
    string Description,
    string Version,
    ImmutableArray<GuildAgentSkillDto> Skills,
    ImmutableArray<string> DefaultInputModes,
    ImmutableArray<string> DefaultOutputModes,
    GuildAgentCapabilitiesDto Capabilities,
    GuildAgentSourceDto Source);

public sealed record GuildAgentCardDto(
    Guid AgentId,
    Guid CardId,
    long Revision,
    string Name,
    string Description,
    string Version,
    ImmutableArray<GuildAgentSkillDto> Skills,
    ImmutableArray<string> DefaultInputModes,
    ImmutableArray<string> DefaultOutputModes,
    GuildAgentCapabilitiesDto Capabilities,
    GuildAgentSourceDto Source);

public sealed record GuildAgentSkillDto(string Id, string Name, string Description, ImmutableArray<string> Tags, ImmutableArray<string> InputModes, ImmutableArray<string> OutputModes);

public sealed record GuildAgentCapabilitiesDto(bool Streaming, bool PushNotifications, bool StateTransitionHistory);

public sealed record GuildAgentSourceDto(GuildAgentSourceKind Kind, Guid? WorkspaceId, string? PeerId, bool Available, string? ReasonCode);

/// <summary>Untrusted transport metadata, kept separate from Cards and executable authority. CredentialReference is a name, never a secret.</summary>
public sealed record GuildRemoteTransportDescriptor(string PeerId, string Endpoint, string ProtocolVersion, ImmutableArray<string> Interfaces, ImmutableArray<string> AuthenticationSchemes, string? CredentialReference, string? ProviderLink, string? DocumentationLink,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<GuildRemoteInterfaceDescriptor> InterfaceObservations = default);

public sealed record GuildRemoteInterfaceDescriptor(string Endpoint, string ProtocolBinding, string ProtocolVersion);

public sealed record GuildListAgentsQuery(GuildAgentSourceKind? Source = null, string? Skill = null, string? Cursor = null, int PageSize = 100);

public sealed record GuildRefreshAgentsRequest(Guid IdempotencyKey, Guid? AgentId = null, string? Cursor = null);

public sealed record GuildResolveRemoteAgentRequest(string Agent);

public sealed record GuildRefreshDiagnosticDto(Guid? AgentId, GuildAgentSourceKind SourceKind, string? ReasonCode, bool Available, long? CardRevision,
    ToolNameDiagnostic[]? ToolDiagnostics = null);

public sealed record GuildRefreshReceipt(GuildMutationReceiptDto Receipt, GuildRefreshDiagnosticPageDto Diagnostics, bool Completed);

public sealed record GuildStatusDto(bool Enabled, bool Available, ImmutableArray<string> ReasonCodes, long SchemaVersion, long LegacyPendingCount);
