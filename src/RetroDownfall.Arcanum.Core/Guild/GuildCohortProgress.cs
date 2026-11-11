namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Content-free informational reference published by a committed Cohort mutation.</summary>
public sealed record GuildCohortProgress(string Type, Guid CohortId, Guid ObjectId, long Sequence);
