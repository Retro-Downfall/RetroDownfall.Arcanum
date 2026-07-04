namespace RetroDownfall.Arcanum.Core.Intelligence.Spells;

public sealed record SpellCastRequest(
    string? Workspace = null,
    Guid? SessionId = null,
    Guid? CampaignId = null);
