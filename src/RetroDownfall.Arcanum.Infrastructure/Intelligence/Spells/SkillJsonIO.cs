using System.Text.Json;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal static class SkillJsonIO
{

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
            DateTimeOffset.UtcNow);
    }

    public static async Task WriteAsync(string spellDirectory, SkillMetadata metadata, CancellationToken ct)
    {
        string path = Path.Combine(spellDirectory, "SKILL.json");

        string json = JsonSerializer.Serialize(metadata, TheForgeJsonContext.Default.SkillMetadata);

        await SpellAtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

}
