namespace RetroDownfall.Arcanum.Core.Lexicon;

/// <summary>
/// Internal (non-configurable) bounds for The Lexicon, shared by the raw-SQL service, the prompt
/// injector, and the entity extractor. Configurable caps (<c>EnableLexiconSystem</c>,
/// <c>LexiconMaxMatchedEntries</c>, <c>LexiconMaxInjectedBytes</c>) live in
/// <see cref="RetroDownfall.Arcanum.Core.Configuration.IntelligenceSettings"/> and are clamped via
/// <see cref="RetroDownfall.Arcanum.Core.Configuration.ArcanumSettingClamps"/>.
/// </summary>
public static class LexiconLimits
{

    /// <summary>Maximum stored length (chars) of an entity <c>Name</c>.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Maximum stored length (chars) of an entity <c>Type</c>.</summary>
    public const int MaxTypeLength = 64;

    /// <summary>Maximum facts accepted in a single <c>scribe_lexicon</c> upsert call.</summary>
    public const int MaxFactsPerUpsert = 32;

    /// <summary>Maximum stored length (chars) of a single fact string.</summary>
    public const int MaxFactLength = 1024;

    /// <summary>Maximum facts retained per entity after merging successive appends.</summary>
    public const int MaxFactsRetainedPerEntry = 256;

    /// <summary>Maximum entity strings extracted by the SemanticRouter or the fallback extractor.</summary>
    public const int MaxExtractedEntities = 8;

    /// <summary>Default type assigned to a brand-new entity when the caller omits a type.</summary>
    public const string DefaultType = "General";

    /// <summary>Entity <c>Type</c> used by Unseen Servant to persist per-job daemon state.</summary>
    public const string DaemonStateType = "DaemonState";

}
