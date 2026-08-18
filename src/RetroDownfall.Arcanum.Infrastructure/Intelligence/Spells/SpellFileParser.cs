using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal sealed class SpellParseResult
{

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string[] Tags { get; init; } = Array.Empty<string>();

    public string? SystemPrompt { get; init; }

    public string? Template { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string[] Tools { get; init; } = Array.Empty<string>();

    public string[] RequiredMcpServers { get; init; } = Array.Empty<string>();

    public string Body { get; init; } = string.Empty;

}

internal static class SpellFileParser
{

    public static SpellParseResult Parse(string fullText, string directoryFallbackName)
    {
        string name = directoryFallbackName;

        string description = string.Empty;

        string[] tags = Array.Empty<string>();

        string? systemPrompt = null;

        string? template = null;

        string? model = null;

        string? provider = null;

        string[] tools = Array.Empty<string>();

        string[] requiredMcpServers = Array.Empty<string>();

        string body = string.Empty;

        ReadOnlySpan<char> text = fullText.AsSpan().TrimStart();

        if (!text.StartsWith("---".AsSpan(), StringComparison.Ordinal))
        {
            body = fullText;

            return new SpellParseResult
            {
                Name = name,
                Description = description,
                Body = body,
            };
        }

        int lineBreak = text.IndexOfAny('\r', '\n');

        if (lineBreak < 0)
        {
            body = fullText;

            return new SpellParseResult
            {
                Name = name,
                Description = description,
                Body = body,
            };
        }

        text = text.Slice(lineBreak).TrimStart("\r\n");

        var yamlLines = new List<string>();

        while (text.Length > 0)
        {
            lineBreak = text.IndexOfAny('\r', '\n');

            ReadOnlySpan<char> line = lineBreak < 0 ? text : text.Slice(0, lineBreak);

            ReadOnlySpan<char> trimmed = line.Trim();

            if (trimmed.SequenceEqual("---".AsSpan()))
            {
                if (lineBreak >= 0)
                {
                    body = text.Slice(lineBreak).TrimStart("\r\n").ToString();
                }

                break;
            }

            yamlLines.Add(trimmed.ToString());

            if (lineBreak < 0)
            {
                break;
            }

            text = text.Slice(lineBreak).TrimStart("\r\n");
        }

        foreach (string yamlLine in yamlLines)
        {
            if (yamlLine.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = yamlLine["name:".Length..].Trim();

                if (name.Length == 0)
                {
                    name = directoryFallbackName;
                }
            }
            else if (yamlLine.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                description = yamlLine["description:".Length..].Trim();
            }
            else if (yamlLine.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                tags = ParseArrayValue(yamlLine.AsSpan("tags:".Length));
            }
            else if (yamlLine.StartsWith("systemPrompt:", StringComparison.OrdinalIgnoreCase))
            {
                systemPrompt = yamlLine["systemPrompt:".Length..].Trim();
            }
            else if (yamlLine.StartsWith("template:", StringComparison.OrdinalIgnoreCase))
            {
                template = yamlLine["template:".Length..].Trim();
            }
            else if (yamlLine.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            {
                model = yamlLine["model:".Length..].Trim();
            }
            else if (yamlLine.StartsWith("provider:", StringComparison.OrdinalIgnoreCase))
            {
                provider = yamlLine["provider:".Length..].Trim();
            }
            else if (yamlLine.StartsWith("tools:", StringComparison.OrdinalIgnoreCase))
            {
                tools = ParseArrayValue(yamlLine.AsSpan("tools:".Length));
            }
            else if (yamlLine.StartsWith("requiredMcpServers:", StringComparison.OrdinalIgnoreCase))
            {
                requiredMcpServers = ParseArrayValue(yamlLine.AsSpan("requiredMcpServers:".Length));
            }
        }

        return new SpellParseResult
        {
            Name = name,
            Description = description,
            Tags = tags,
            SystemPrompt = systemPrompt,
            Template = template,
            Model = model,
            Provider = provider,
            Tools = tools,
            RequiredMcpServers = requiredMcpServers,
            Body = body,
        };
    }

    public static string FormatCreate(string name, CreateSpellRequest request)
    {
        string[] tags = request.Tags ?? Array.Empty<string>();

        string[] tools = request.Tools ?? Array.Empty<string>();

        string[] requiredMcpServers = request.RequiredMcpServers ?? Array.Empty<string>();

        return Format(
            name,
            request.Description,
            tags,
            request.SystemPrompt,
            request.Template,
            request.Model,
            request.Provider,
            tools,
            requiredMcpServers,
            body: request.Body ?? string.Empty);
    }

    public static string FormatUpdate(ParsedSpell existing, UpdateSpellRequest request)
    {
        string? description = request.Description ?? existing.Description;

        string[] tags = request.Tags ?? existing.Tags;

        string? systemPrompt = request.SystemPrompt ?? existing.SystemPrompt;

        string? template = request.Template ?? existing.Template;

        string? model = request.Model ?? existing.Model;

        string? provider = request.Provider ?? existing.Provider;

        string[] tools = request.Tools ?? existing.Tools;

        string[] requiredMcpServers = request.RequiredMcpServers ?? existing.RequiredMcpServers;

        return Format(
            existing.Name,
            description,
            tags,
            systemPrompt,
            template,
            model,
            provider,
            tools,
            requiredMcpServers,
            body: existing.Body);
    }

    /// <summary>
    /// Formats a spell file using <paramref name="existing"/>'s frontmatter fields with the given
    /// <paramref name="body"/> substituted. Used for version create/update (preserving frontmatter
    /// while replacing body content).
    /// </summary>
    public static string FormatWithBody(ParsedSpell existing, string body) =>
        Format(
            existing.Name,
            existing.Description,
            existing.Tags,
            existing.SystemPrompt,
            existing.Template,
            existing.Model,
            existing.Provider,
            existing.Tools,
            existing.RequiredMcpServers,
            body);

    /// <summary>
    /// Formats a spell file using <paramref name="existing"/>'s frontmatter and body, with the
    /// <c>name</c> field rewritten to <paramref name="newName"/>. Used for spell clone.
    /// </summary>
    public static string FormatRenamed(ParsedSpell existing, string newName) =>
        Format(
            newName,
            existing.Description,
            existing.Tags,
            existing.SystemPrompt,
            existing.Template,
            existing.Model,
            existing.Provider,
            existing.Tools,
            existing.RequiredMcpServers,
            existing.Body);

    private static string Format(
        string name,
        string? description,
        string[] tags,
        string? systemPrompt,
        string? template,
        string? model,
        string? provider,
        string[] tools,
        string[] requiredMcpServers,
        string body)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");

        sb.Append("name: ");

        sb.AppendLine(SingleFrontmatterLine(name));

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append("description: ");

            sb.AppendLine(SingleFrontmatterLine(description));
        }

        AppendArrayLine(sb, "tags", tags);

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append("systemPrompt: ");

            sb.AppendLine(SingleFrontmatterLine(systemPrompt));
        }

        if (!string.IsNullOrWhiteSpace(template))
        {
            sb.Append("template: ");

            sb.AppendLine(SingleFrontmatterLine(template));
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            sb.Append("model: ");

            sb.AppendLine(SingleFrontmatterLine(model));
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            sb.Append("provider: ");

            sb.AppendLine(SingleFrontmatterLine(provider));
        }

        AppendArrayLine(sb, "tools", tools);

        AppendArrayLine(sb, "requiredMcpServers", requiredMcpServers);

        sb.AppendLine("---");

        if (!string.IsNullOrEmpty(body))
        {
            sb.Append(body);

            if (!body.EndsWith('\n'))
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collapses a frontmatter scalar onto the single line it is written on.
    /// </summary>
    /// <remarks>
    /// The frontmatter block is line-delimited, so a value carrying CR or LF would forge every field
    /// after it — including <c>tools</c> and <c>requiredMcpServers</c>, which decide what a cast may
    /// reach. <see cref="SpellFrontmatterValidator"/> rejects such a value and every current writer
    /// runs it, so this is belt and braces: holding the invariant at the emitter means a future
    /// writer that forgets the gate still cannot produce a forged file. The text is kept and only
    /// the delimiter is neutralized, so a legitimate value is never silently discarded.
    /// </remarks>
    internal static string SingleFrontmatterLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny(['\r', '\n']) < 0
            ? value.Trim()
            : value.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
    }

    private static void AppendArrayLine(StringBuilder sb, string key, string[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        sb.Append(key);

        sb.Append(": ");

        sb.AppendLine(string.Join(", ", values.Select(SingleFrontmatterLine)));
    }

    private static string[] ParseArrayValue(ReadOnlySpan<char> raw)
    {
        string trimmed = raw.Trim().ToString();

        if (trimmed.Length == 0)
        {
            return Array.Empty<string>();
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Length == 0)
        {
            return Array.Empty<string>();
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

}
