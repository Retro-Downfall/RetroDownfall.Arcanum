using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>The shared publication policy for bounded continuity text.</summary>
public static class CampaignSummaryPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result Validate(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Invalid();
        }

        try
        {
            if (StrictUtf8.GetByteCount(content) > CampaignRollupLimits.SummaryUtf8Bytes)
            {
                return Invalid();
            }
        }
        catch (EncoderFallbackException)
        {
            return Invalid();
        }

        if (FindEncodedBlocks(content).Count != 0)
        {
            return Invalid();
        }

        int start = 0;

        for (int index = 0; index <= content.Length; index++)
        {
            if (index != content.Length && !IsDelimiter(content[index]))
            {
                continue;
            }

            ReadOnlySpan<char> token = content.AsSpan(start, index - start);

            if (IsForbiddenToken(token))
            {
                return Invalid();
            }

            start = index + 1;
        }

        return Result.Success();
    }

    /// <summary>Omits recognizable wrapped encoded runs before maintenance payload construction.</summary>
    public static string OmitEncodedBlocks(string content)
    {
        List<(int Start, int End)> blocks = FindEncodedBlocks(content);

        if (blocks.Count == 0)
        {
            return content;
        }

        StringBuilder builder = new(content.Length);

        int cursor = 0;

        foreach ((int start, int end) in blocks)
        {
            builder.Append(content.AsSpan(cursor, start - cursor));

            builder.Append("[omitted]");

            if (content[end - 1] is '\n')
            {
                builder.Append(end >= 2 && content[end - 2] is '\r' ? "\r\n" : "\n");
            }

            cursor = end;
        }

        builder.Append(content.AsSpan(cursor));

        return builder.ToString();
    }

    public static Result<string> ParseStructuredResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > CampaignRollupLimits.SummaryUtf8Bytes * 8)
        {
            return Invalid();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 4 });

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return Invalid();
            }

            string? summary = null;

            int propertyCount = 0;

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                propertyCount++;

                if (!property.NameEquals("summary") || property.Value.ValueKind is not JsonValueKind.String)
                {
                    return Invalid();
                }

                summary = property.Value.GetString();
            }

            if (propertyCount != 1 || summary is null)
            {
                return Invalid();
            }

            Result validated = Validate(summary);

            return validated.IsSuccess ? Result<string>.Success(summary) : validated.Error;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return Invalid();
        }
    }

    private static bool IsDelimiter(char value) => char.IsWhiteSpace(value)
        || value is '"' or '\'' or '`' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or ',' or ';';

    private static bool IsForbiddenToken(ReadOnlySpan<char> token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        if ((token[0] is '/' && token.Length > 1)
            || token.StartsWith("\\\\", StringComparison.Ordinal)
            || (token.Length >= 3 && char.IsAsciiLetter(token[0]) && token[1] is ':' && token[2] is '/' or '\\')
            || token.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (token.Length < 256)
        {
            return false;
        }

        return IsEncodedText(token);
    }

    private static List<(int Start, int End)> FindEncodedBlocks(string content)
    {
        List<(int Start, int End)> blocks = [];

        int lineStart = 0;

        int runStart = -1;

        int runEnd = 0;

        int encodedCharacters = 0;

        for (int index = 0; index <= content.Length; index++)
        {
            if (index < content.Length && content[index] is not '\n')
            {
                continue;
            }

            ReadOnlySpan<char> line = content.AsSpan(lineStart, index - lineStart).Trim();

            if (line.Length >= 64 && IsEncodedText(line))
            {
                if (runStart < 0)
                {
                    runStart = lineStart;
                }

                runEnd = index < content.Length ? index + 1 : index;

                encodedCharacters += line.Length;
            }
            else
            {
                if (encodedCharacters >= 256)
                {
                    blocks.Add((runStart, runEnd));
                }

                runStart = -1;

                encodedCharacters = 0;
            }

            lineStart = index + 1;
        }

        if (encodedCharacters >= 256)
        {
            blocks.Add((runStart, runEnd));
        }

        return blocks;
    }

    private static bool IsEncodedText(ReadOnlySpan<char> token)
    {
        foreach (char value in token)
        {
            if (!char.IsAsciiLetterOrDigit(value) && value is not ('+' or '/' or '=' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static Error Invalid() => new(ErrorCodes.Covenant.InvalidContent,
        "Campaign continuity must be one bounded summary without host paths or attachment bytes.");
}
