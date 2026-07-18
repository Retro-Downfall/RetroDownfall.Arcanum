namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// OpenAI-compatible <c>POST /v1/moderations</c>. Bound from <c>Arcanum:Moderations</c>. Phase 1 is a
/// pass-through stub (always <c>flagged: false</c>, every category/score <c>false</c>/<c>0.0</c>) —
/// Arcanum runs no local or remote moderation model yet. Disabled by default so clients that probe
/// this route get an explicit <c>404</c> rather than a silently-useless "always safe" verdict.
/// </summary>
public sealed record ModerationsSettings
{

    public bool Enabled { get; set; } = false;

}
