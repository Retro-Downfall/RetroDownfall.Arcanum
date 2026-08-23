using System.Text.Json;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

/// <summary>
/// Reads and writes the structured spell sidecar next to <c>SPELL.md</c>.
/// Canonical filename is <see cref="CanonicalFileName"/> (<c>SPELL.json</c>);
/// legacy <see cref="LegacyFileName"/> (<c>SKILL.json</c>) is read as fallback only.
/// </summary>
internal static class SkillJsonIO
{

    public const string CanonicalFileName = "SPELL.json";

    public const string LegacyFileName = "SKILL.json";

    /// <summary>
    /// Resolves which sidecar file to read: prefers <see cref="CanonicalFileName"/> when present;
    /// otherwise falls back to <see cref="LegacyFileName"/>. Returns <c>null</c> when neither exists.
    /// When both exist, the canonical file wins.
    /// </summary>
    public static string? ResolveSidecarPath(string spellDirectory)
    {

        if (string.IsNullOrWhiteSpace(spellDirectory))
        {
            return null;
        }

        string canonical = Path.Combine(spellDirectory, CanonicalFileName);

        if (File.Exists(canonical))
        {
            return canonical;
        }

        string legacy = Path.Combine(spellDirectory, LegacyFileName);

        return File.Exists(legacy) ? legacy : null;

    }

    public static bool HasStructuredFields(CreateSpellRequest request) =>
        !string.IsNullOrWhiteSpace(request.Version)
        || request.InputSchema is not null
        || request.OutputSchema is not null
        || request.DeclaredTools is { Length: > 0 }
        || request.Dependencies is { Length: > 0 }
        || request.DefaultParameters is { Count: > 0 };

    public static bool HasStructuredFields(UpdateSpellRequest request) =>
        !string.IsNullOrWhiteSpace(request.Version)
        || request.InputSchema is not null
        || request.OutputSchema is not null
        || request.DeclaredTools is not null
        || request.Dependencies is not null
        || request.DefaultParameters is not null;

    public static SkillMetadata BuildMetadataFromCreate(string name, CreateSpellRequest request) =>
        new(
            name,
            request.Version ?? "1.0.0",
            request.Description,
            request.Tags?.ToList() ?? [],
            request.InputSchema,
            request.OutputSchema,
            request.DeclaredTools?.ToList() ?? [],
            request.Dependencies?.ToList() ?? [],
            request.Model,
            request.Provider,
            request.DefaultParameters,
            DateTimeOffset.UtcNow);

    public static SkillMetadata MergeMetadata(ParsedSpell existing, UpdateSpellRequest request)
    {
        SkillMetadata? current = existing.SkillMetadata;

        return new SkillMetadata(
            existing.Name,
            request.Version ?? current?.Version ?? "1.0.0",
            request.Description ?? current?.Description ?? existing.Description,
            request.Tags?.ToList() ?? current?.Tags ?? existing.Tags.ToList(),
            request.InputSchema ?? current?.InputSchema,
            request.OutputSchema ?? current?.OutputSchema,
            request.DeclaredTools?.ToList() ?? current?.DeclaredTools ?? [],
            request.Dependencies?.ToList() ?? current?.Dependencies ?? [],
            request.Model ?? current?.Model ?? existing.Model,
            request.Provider ?? current?.Provider ?? existing.Provider,
            request.DefaultParameters ?? current?.DefaultParameters,
            DateTimeOffset.UtcNow,
            current?.ActiveVersion);
    }

    /// <summary>
    /// Builds the merged <see cref="SkillMetadata"/> for activating a spell version: preserves every
    /// existing structured field (falling back to the parsed spell's frontmatter when no sidecar
    /// exists yet) and sets <see cref="SkillMetadata.ActiveVersion"/> to the newly activated label.
    /// Only <c>ActivateSpellVersionAsync</c> calls this — create/update version do not touch the field.
    /// The subsequent <see cref="WriteAsync"/> always writes canonical <c>SPELL.json</c>.
    /// </summary>
    public static SkillMetadata SetActiveVersion(ParsedSpell existing, string activeVersionLabel)
    {
        SkillMetadata? current = existing.SkillMetadata;

        return new SkillMetadata(
            existing.Name,
            current?.Version ?? "1.0.0",
            current?.Description ?? (string.IsNullOrEmpty(existing.Description) ? null : existing.Description),
            current?.Tags ?? existing.Tags.ToList(),
            current?.InputSchema,
            current?.OutputSchema,
            current?.DeclaredTools ?? [],
            current?.Dependencies ?? [],
            current?.Model ?? existing.Model,
            current?.Provider ?? existing.Provider,
            current?.DefaultParameters,
            DateTimeOffset.UtcNow,
            activeVersionLabel);
    }

    /// <summary>Writes canonical <see cref="CanonicalFileName"/> only. Never creates a new legacy sidecar.</summary>
    public static async Task WriteAsync(string spellDirectory, SkillMetadata metadata, CancellationToken ct)
    {
        string path = Path.Combine(spellDirectory, CanonicalFileName);

        string json = JsonSerializer.Serialize(metadata, ArcanumCoreJsonContext.Default.SkillMetadata);

        await SpellAtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

}
