using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildParsedAgent(Guid? ExplicitId, string Name, string Description, string Version,
    ImmutableArray<string> Skills, ImmutableArray<string> InputModes, ImmutableArray<string> OutputModes,
    ImmutableArray<string> RequestedTools, string Guidance, bool RequestedToolsSpecified = false)
{
    internal ToolNameDiagnostic[] ToolDiagnostics => ToolNameDiagnostics.Removed(RequestedTools);
}

/// <summary>The declared-agent format is a closed single-line subset, not a general YAML loader.</summary>
internal static class GuildDeclaredAgentParser
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "id", "name", "description", "version", "skills", "inputModes", "outputModes", "tools",
    };

    internal static Result<GuildParsedAgent> Parse(string text)
    {
        if (text is null || text.IndexOf('\0') >= 0)
        {
            return Invalid();
        }

        int position = 0;

        if (ReadLine(text, ref position) != "---")
        {
            return Invalid();
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);

        bool closed = false;

        while (position < text.Length)
        {
            string line = ReadLine(text, ref position);

            if (line == "---")
            {
                closed = true;

                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int colon = line.IndexOf(':');

            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                return Invalid();
            }

            string key = line[..colon];

            if (!Keys.Contains(key) || !fields.TryAdd(key, line[(colon + 1)..].Trim()))
            {
                return Invalid();
            }
        }

        if (!closed || !TryScalar(fields.GetValueOrDefault("name"), out string name)
            || !TryScalar(fields.GetValueOrDefault("description"), out string description))
        {
            return Invalid();
        }

        string version = "1";

        if (fields.TryGetValue("version", out string? rawVersion) && !TryScalar(rawVersion, out version))
        {
            return Invalid();
        }

        Guid? explicitId = null;

        if (fields.TryGetValue("id", out string? rawId))
        {
            if (!TryScalar(rawId, out string idText) || !Guid.TryParse(idText, out Guid id) || id == Guid.Empty)
            {
                return Invalid();
            }

            explicitId = id;
        }

        if (!TryArray(fields, "skills", ["apprentice-goal-execution"], out ImmutableArray<string> skills)
            || !TryArray(fields, "inputModes", ["text/plain"], out ImmutableArray<string> inputs)
            || !TryArray(fields, "outputModes", ["text/plain"], out ImmutableArray<string> outputs)
            || !TryArray(fields, "tools", [], out ImmutableArray<string> tools))
        {
            return Invalid();
        }

        return Result<GuildParsedAgent>.Success(new GuildParsedAgent(explicitId, name, description, version,
            skills, inputs, outputs, tools, text[position..], fields.ContainsKey("tools")));
    }

    private static string ReadLine(string text, ref int position)
    {
        int start = position;

        while (position < text.Length && text[position] is not '\r' and not '\n')
        {
            position++;
        }

        string line = text[start..position];

        if (position < text.Length && text[position++] == '\r' && position < text.Length && text[position] == '\n')
        {
            position++;
        }

        return line;
    }

    private static bool TryArray(Dictionary<string, string> fields, string key, ImmutableArray<string> fallback,
        out ImmutableArray<string> values)
    {
        values = fallback;

        if (!fields.TryGetValue(key, out string? raw))
        {
            return true;
        }

        if (raw.Length < 2 || raw[0] != '[' || raw[^1] != ']')
        {
            return false;
        }

        string inner = raw[1..^1];

        if (string.IsNullOrWhiteSpace(inner))
        {
            values = [];

            return true;
        }

        ImmutableArray<string>.Builder items = ImmutableArray.CreateBuilder<string>();

        int start = 0;

        char quote = '\0';

        for (int index = 0; index <= inner.Length; index++)
        {
            if (index < inner.Length)
            {
                char current = inner[index];

                if (quote != '\0')
                {
                    if (current == quote)
                    {
                        if (quote == '\'' && index + 1 < inner.Length && inner[index + 1] == '\'')
                        {
                            index++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }
                    else if (current == '\\' && quote == '"')
                    {
                        index++;
                    }

                    continue;
                }

                if (current is '\'' or '"' && string.IsNullOrWhiteSpace(inner[start..index]))
                {
                    quote = current;

                    continue;
                }

                if (current != ',')
                {
                    continue;
                }
            }

            if (quote != '\0' || !TryScalar(inner[start..index].Trim(), out string item))
            {
                return false;
            }

            items.Add(item);

            start = index + 1;
        }

        if (quote != '\0' || start != inner.Length + 1)
        {
            return false;
        }

        values = items.ToImmutable();

        return true;
    }

    private static bool TryScalar(string? raw, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl))
        {
            return false;
        }

        if (raw[0] is '\'' or '"')
        {
            char quote = raw[0];

            if (raw.Length < 2 || raw[^1] != quote)
            {
                return false;
            }

            StringBuilder scalar = new();

            for (int index = 1; index < raw.Length - 1; index++)
            {
                char current = raw[index];

                if (current == quote)
                {
                    if (quote != '\'' || index + 1 >= raw.Length - 1 || raw[++index] != '\'')
                    {
                        return false;
                    }
                }
                else if (quote == '"' && current == '\\')
                {
                    if (++index >= raw.Length - 1 || raw[index] is not ('"' or '\\'))
                    {
                        return false;
                    }

                    current = raw[index];
                }

                scalar.Append(current);
            }

            value = scalar.ToString();
        }
        else
        {
            if (raw[0] is '[' or ']' or '{' or '}' or '&' or '*' or '!' or '|' or '>' or '@' or '`'
                || raw.IndexOfAny(['[', ']', '{', '}']) >= 0)
            {
                return false;
            }

            value = raw;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static Result<GuildParsedAgent> Invalid() => Result<GuildParsedAgent>.Failure(
        new Error(ErrorCodes.Guild.InvalidRequest, "The declared agent frontmatter is invalid or unsupported."));
}
