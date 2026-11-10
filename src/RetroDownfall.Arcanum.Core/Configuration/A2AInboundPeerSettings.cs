using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Operator-authored peer facts; neither a credential value nor live Guild authority.</summary>
public sealed record A2AInboundPeerSettings
{
    private Guid? _campaignId;

    [JsonRequired]
    public string PeerId { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    [JsonRequired]
    public string CredentialEnvironmentVariable { get; set; } = string.Empty;

    [JsonRequired]
    public GuildGrantSettings? Grant { get; set; }

    [JsonRequired]
    public Guid? CampaignId
    {
        get => _campaignId;
        set
        {
            _campaignId = value;

            HasExplicitCampaignId = true;
        }
    }

    internal bool HasExplicitCampaignId { get; private set; }

    public Guid? SessionId { get; set; }

    public Guid? WorkspaceId { get; set; }
}

/// <summary>Mutable generated-binder counterpart of the complete Guild grant wire algebra.</summary>
public sealed record GuildGrantSettings
{
    private DateTimeOffset? _expiresAtUtc;

    [JsonRequired]
    public GuildWorkspaceGrantSettings[]? Workspaces { get; set; }

    [JsonRequired]
    public GuildToolGrantSettings[]? Tools { get; set; }

    [JsonRequired]
    public GuildMemoryGrantSettings[]? Memory { get; set; }

    [JsonRequired]
    public GuildBudgetGrantSettings? Budget { get; set; }

    [JsonRequired]
    public GuildDelegationKind[]? Delegation { get; set; }

    [JsonRequired]
    public DateTimeOffset? ExpiresAtUtc
    {
        get => _expiresAtUtc;
        set
        {
            _expiresAtUtc = value;

            HasExplicitExpiry = true;
        }
    }

    internal bool HasExplicitExpiry { get; private set; }

    /// <summary>Validates data only. The host must still verify ownership, availability and live authority.</summary>
    public Result<GuildGrant> ToGrant()
    {
        if (Workspaces is null || Tools is null || Memory is null || Budget is null
            || Delegation is null || !HasExplicitExpiry)
        {
            return Result<GuildGrant>.Failure(new Error(ErrorCodes.Guild.InvalidRequest,
                "Every standing Guild grant axis must be explicitly supplied."));
        }

        GuildGrant grant = new(
            Workspaces.Select(static item => item is null ? null! : new GuildWorkspaceGrant(
                item.WorkspaceId, item.RelativeRoot, item.Access ?? (GuildWorkspaceAccess)(-1))).ToImmutableArray(),
            Tools.Select(static item => item is null ? null! : new GuildToolGrant(
                item.SourceKind ?? (GuildToolSourceKind)(-1), item.SourceRegistrationId, item.Name)).ToImmutableArray(),
            Memory.Select(static item => item is null ? null! : new GuildMemoryGrant(
                item.Store ?? (GuildMemoryStore)(-1), item.Operation ?? (GuildMemoryOperation)(-1),
                item.Scope is null ? null! : new GuildMemoryScope(
                    item.Scope.Kind ?? (GuildMemoryScopeKind)(-1), item.Scope.Id))).ToImmutableArray(),
            new GuildBudgetGrant(
                Budget.Usd is null ? null! : new GuildUsdBudget(Budget.Usd.Mode ?? (GuildBudgetMode)(-1), Budget.Usd.Value),
                Budget.Tokens is null ? null! : new GuildTokenBudget(Budget.Tokens.Mode ?? (GuildBudgetMode)(-1), Budget.Tokens.Value)),
            Delegation.ToImmutableArray(),
            ExpiresAtUtc);

        Result validation = GuildGrantPolicy.Validate(grant);

        return validation.IsSuccess ? Result<GuildGrant>.Success(grant) : Result<GuildGrant>.Failure(validation.Error);
    }
}

public sealed record GuildWorkspaceGrantSettings
{
    [JsonRequired]
    public Guid WorkspaceId { get; set; }

    [JsonRequired]
    public string RelativeRoot { get; set; } = null!;

    [JsonRequired]
    public GuildWorkspaceAccess? Access { get; set; }
}

public sealed record GuildToolGrantSettings
{
    [JsonRequired]
    public GuildToolSourceKind? SourceKind { get; set; }

    [JsonRequired]
    public string SourceRegistrationId { get; set; } = string.Empty;

    [JsonRequired]
    public string Name { get; set; } = string.Empty;
}

public sealed record GuildMemoryGrantSettings
{
    [JsonRequired]
    public GuildMemoryStore? Store { get; set; }

    [JsonRequired]
    public GuildMemoryOperation? Operation { get; set; }

    [JsonRequired]
    public GuildMemoryScopeSettings? Scope { get; set; }
}

public sealed record GuildMemoryScopeSettings
{
    [JsonRequired]
    public GuildMemoryScopeKind? Kind { get; set; }

    public Guid? Id { get; set; }
}

public sealed record GuildBudgetGrantSettings
{
    [JsonRequired]
    public GuildUsdBudgetSettings? Usd { get; set; }

    [JsonRequired]
    public GuildTokenBudgetSettings? Tokens { get; set; }
}

public sealed record GuildUsdBudgetSettings
{
    [JsonRequired]
    public GuildBudgetMode? Mode { get; set; }

    [JsonConverter(typeof(ExactNullableDecimalJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Value { get; set; }
}

public sealed record GuildTokenBudgetSettings
{
    [JsonRequired]
    public GuildBudgetMode? Mode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Value { get; set; }
}
