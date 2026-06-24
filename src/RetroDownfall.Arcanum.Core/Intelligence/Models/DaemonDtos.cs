namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record UnseenServantJobStatusDto(
    string Name,
    string TargetSpell,
    int BaseIntervalMinutes,
    int EffectiveIntervalMinutes,
    bool IsEnabled,
    DateTimeOffset? LastRunAt = null,
    DateTimeOffset? NextDueAt = null,
    string? LastResult = null);

public sealed record AdjustInitiativeRequestDto(int IntervalMinutes);
