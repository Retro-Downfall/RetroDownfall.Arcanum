using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Content-free current projection of an immutable tree member, including retained tombstones.</summary>
internal sealed record GuildQuestTreeNode(Guid Id, Guid RootQuestId, Guid? ParentQuestId, long IssuedSequence,
    long Revision, QuestState State, GuildWaitReason? WaitReason, bool CancellationRequested,
    bool CancellationConverged, bool ReconciliationRequired, GuildAccountingState AccountingState, bool Retired);

/// <summary>Unfiltered captured membership, including the requested subtree root exactly once.</summary>
internal sealed record GuildQuestTreeSummary(long MemberCount, long ActiveExecutionCount, long ReconciliationRequiredCount,
    long CancellationRequestedCount, long AccountingUnresolvedCount);

/// <summary>Stable membership at SnapshotSequence; state and summary are current on every page.</summary>
internal sealed record GuildQuestTreePage(GuildQuestTreeSummary Summary, ImmutableArray<GuildQuestTreeNode> Items,
    string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildQuestTreeNode))]
[JsonSerializable(typeof(GuildQuestTreeSummary))]
[JsonSerializable(typeof(GuildQuestTreePage))]
internal sealed partial class GuildTreeJsonContext : JsonSerializerContext;
