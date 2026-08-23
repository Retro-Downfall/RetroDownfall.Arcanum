using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal static class SpellMarkdownGenerator
{

    public static string Generate(SkillMetadata metadata)
    {
        string description = metadata.Description ?? string.Empty;

        string tagsLine = metadata.Tags.Count > 0
            ? string.Join(", ", metadata.Tags.Select(SpellFileParser.SingleFrontmatterLine))
            : string.Empty;

        var lines = new List<string>
        {
            "---",
            $"name: {SpellFileParser.SingleFrontmatterLine(metadata.Name)}",
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add($"description: {SpellFileParser.SingleFrontmatterLine(description)}");
        }

        if (tagsLine.Length > 0)
        {
            lines.Add($"tags: [{tagsLine}]");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Model))
        {
            lines.Add($"model: {SpellFileParser.SingleFrontmatterLine(metadata.Model)}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Provider))
        {
            lines.Add($"provider: {SpellFileParser.SingleFrontmatterLine(metadata.Provider)}");
        }

        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add($"# {metadata.Name}");
        lines.Add(string.Empty);

        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(description.Trim());
        }

        return string.Join('\n', lines) + '\n';
    }

    public static string GenerateFromCreateRequest(string name, CreateSpellRequest request)
    {
        SkillMetadata metadata = new(
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

        return Generate(metadata);
    }

}
