using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Guild;

public sealed record GuildAgentCardPageDto(ImmutableArray<GuildAgentCardDto> Items, string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");

public sealed record GuildQuestPageDto(ImmutableArray<GuildQuestDto> Items, string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");

public sealed record GuildRefreshDiagnosticPageDto(ImmutableArray<GuildRefreshDiagnosticDto> Items, string? NextCursor, long SnapshotSequence, string StateConsistency = "livePerPage");

public sealed record GuildTreeSnapshotDto(GuildQuestDto Summary, GuildQuestPageDto Members, long Watermark, string ResumeCursor);

public sealed record GuildCohortSnapshotDto(CohortDto Summary, CohortMemberPageDto Members, long Watermark, string ResumeCursor);

public sealed record GuildChangeEventDto(long EventSequence, GuildEventKind EventKind, Guid ObjectId, long Revision,
    ImmutableArray<GuildDataRef> ContentRefs, Guid? QuestId = null);

public sealed record GuildResyncEventDto(string ReasonCode, string SnapshotRoute, string ResumeCursor);

public sealed record GuildTerminalEventDto(Guid ObjectId, long Revision, QuestState State, GuildAccountingState AccountingState, long EventWatermark);
