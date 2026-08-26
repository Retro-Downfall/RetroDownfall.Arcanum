namespace RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Intelligence;

public sealed record LexiconFactProvenance(
    string Fact,
    AttachmentMemoryProvenance Source);

/// <summary>
/// A single Lexicon entity surfaced to MCP tools, the inference pipeline, and (later) Minimal APIs.
/// Mirrors the <c>lexicon_entries</c> raw-SQL table (see <c>Infrastructure/Data/Schema/Tables/lexicon_entries.sql</c>) one-to-one,
/// with <c>FactsJson</c> deserialized into the <c>Facts</c> string array.
/// </summary>
public sealed record LexiconEntryDto(
    Guid Id,
    string Name,
    string Type,
    string[] Facts,
    DateTimeOffset UpdatedAt,
    LexiconFactProvenance[]? FactProvenance = null,
    Guid? ScopeCampaignId = null);
