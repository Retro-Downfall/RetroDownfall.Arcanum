namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>
/// Which tier of the Lexicon a read or a write belongs to: the installation, or exactly one Campaign.
/// </summary>
/// <remarks>
/// <see cref="Global"/> is the default value of the struct, and deliberately so. Every entity written
/// before scopes existed is installation-global authored content, every caller that has no Campaign to
/// state is asking about that tier, and the default has to be the one that preserves today's behaviour
/// rather than the one that hides entities from an operator who never opted in.
///
/// <para><see cref="Key"/> is the stored form. The empty string, not <c>NULL</c>, marks the global tier:
/// SQLite treats NULLs as distinct in a UNIQUE index, so a nullable scope column would let one global
/// name be inserted any number of times and quietly undo the uniqueness the Lexicon has always had.</para>
/// </remarks>
public readonly record struct LexiconScope
{

    private LexiconScope(Guid? campaignId) => CampaignId = campaignId;

    /// <summary>The installation tier: what every entity written before scopes existed belongs to.</summary>
    public static LexiconScope Global => default;

    /// <summary>The tier owned by one Campaign, which may shadow a global entity of the same name.</summary>
    public static LexiconScope ForCampaign(Guid campaignId) => new(campaignId);

    /// <summary>
    /// The scope a turn resolved to: that Campaign's tier, or the global one when nothing resolved.
    /// </summary>
    public static LexiconScope ForResolvedCampaign(Guid? campaignId) =>
        campaignId is { } resolved ? ForCampaign(resolved) : Global;

    public Guid? CampaignId { get; }

    public bool IsGlobal => CampaignId is null;

    /// <summary>The value stored in <c>lexicon_entries.ScopeCampaignId</c> for this tier.</summary>
    public string Key => CampaignId?.ToString() ?? string.Empty;

}
