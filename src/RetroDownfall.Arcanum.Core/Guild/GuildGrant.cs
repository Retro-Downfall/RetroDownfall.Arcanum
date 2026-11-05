using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Serializable authority description, never proof of authenticated or live authority.</summary>
public sealed record GuildGrant(
    [property: JsonRequired] ImmutableArray<GuildWorkspaceGrant> Workspaces,
    [property: JsonRequired] ImmutableArray<GuildToolGrant> Tools,
    [property: JsonRequired] ImmutableArray<GuildMemoryGrant> Memory,
    [property: JsonRequired] GuildBudgetGrant Budget,
    [property: JsonRequired] ImmutableArray<GuildDelegationKind> Delegation,
    [property: JsonRequired] DateTimeOffset? ExpiresAtUtc);

public sealed record GuildWorkspaceGrant(
    [property: JsonRequired] Guid WorkspaceId,
    [property: JsonRequired] string RelativeRoot,
    [property: JsonRequired] GuildWorkspaceAccess Access);

public sealed record GuildToolGrant(
    [property: JsonRequired] GuildToolSourceKind SourceKind,
    [property: JsonRequired] string SourceRegistrationId,
    [property: JsonRequired] string Name);

public sealed record GuildMemoryGrant(
    [property: JsonRequired] GuildMemoryStore Store,
    [property: JsonRequired] GuildMemoryOperation Operation,
    [property: JsonRequired] GuildMemoryScope Scope);

public sealed record GuildMemoryScope(
    [property: JsonRequired] GuildMemoryScopeKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Id = null);

public sealed record GuildBudgetGrant(
    [property: JsonRequired] GuildUsdBudget Usd,
    [property: JsonRequired] GuildTokenBudget Tokens);

public sealed record GuildUsdBudget(
    [property: JsonRequired] GuildBudgetMode Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null);

public sealed record GuildTokenBudget(
    [property: JsonRequired] GuildBudgetMode Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Value = null);

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildWorkspaceAccess>))]
public enum GuildWorkspaceAccess
{
    [JsonStringEnumMemberName("read")] Read,

    [JsonStringEnumMemberName("readWrite")] ReadWrite,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildToolSourceKind>))]
public enum GuildToolSourceKind
{
    [JsonStringEnumMemberName("builtIn")] BuiltIn,

    [JsonStringEnumMemberName("internalMcp")] InternalMcp,

    [JsonStringEnumMemberName("adapter")] Adapter,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildMemoryStore>))]
public enum GuildMemoryStore
{
    [JsonStringEnumMemberName("transcript")] Transcript,

    [JsonStringEnumMemberName("saga")] Saga,

    [JsonStringEnumMemberName("lexicon")] Lexicon,

    [JsonStringEnumMemberName("attachment")] Attachment,

    [JsonStringEnumMemberName("tapestry")] Tapestry,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildMemoryOperation>))]
public enum GuildMemoryOperation
{
    [JsonStringEnumMemberName("read")] Read,

    [JsonStringEnumMemberName("append")] Append,

    [JsonStringEnumMemberName("delete")] Delete,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildMemoryScopeKind>))]
public enum GuildMemoryScopeKind
{
    [JsonStringEnumMemberName("global")] Global,

    [JsonStringEnumMemberName("campaign")] Campaign,

    [JsonStringEnumMemberName("session")] Session,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildBudgetMode>))]
public enum GuildBudgetMode
{
    [JsonStringEnumMemberName("finite")] Finite,

    [JsonStringEnumMemberName("unlimited")] Unlimited,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<GuildDelegationKind>))]
public enum GuildDelegationKind
{
    [JsonStringEnumMemberName("local")] Local,

    [JsonStringEnumMemberName("remote")] Remote,

    [JsonStringEnumMemberName("cohort")] Cohort,
}
