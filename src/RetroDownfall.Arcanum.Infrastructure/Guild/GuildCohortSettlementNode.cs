using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

// Content-free, disposable traversal state. Monetary strings use ExactUsdText throughout.
internal sealed class GuildCohortSettlementNode
{
    public Guid AccountId { get; set; }

    public Guid RootAccountId { get; set; }

    public Guid? ParentAccountId { get; set; }

    public GuildBudgetOwnerKind Kind { get; set; }

    public Guid OwnerId { get; set; }

    public string? UsdLimit { get; set; }

    public long? TokenLimit { get; set; }

    public bool Closed { get; set; }

    public long Revision { get; set; }

    public int Phase { get; set; }

    public Guid? LastEffect { get; set; }

    public Guid? LastChild { get; set; }

    public string KnownUsd { get; set; } = "0";

    public long KnownTokens { get; set; }

    public string? HoldUsd { get; set; } = "0";

    public long? HoldTokens { get; set; } = 0;

    public string? ActiveUsd { get; set; } = "0";

    public long? ActiveTokens { get; set; } = 0;

    public string? LiabilityUsd { get; set; } = "0";

    public long? LiabilityTokens { get; set; } = 0;

    public bool UnknownUsd { get; set; }

    public bool UnknownTokens { get; set; }

    public bool CapabilityFault { get; set; }

    public long Priced { get; set; }

    public long Unpriced { get; set; }

    public long Uncertain { get; set; }

    public long PendingMembers { get; set; }

    public long PendingDescendants { get; set; }

    public long CancelRequested { get; set; }

    public long PendingAccounts { get; set; }

    public long PendingEffects { get; set; }

    public bool IsPrimaryMember { get; set; }
}

[JsonSerializable(typeof(GuildCohortSettlementNode))]
internal sealed partial class GuildCohortSettlementJsonContext : JsonSerializerContext;
