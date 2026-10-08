using System.Text;

using System.Text.Json;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Reads one diagnostic tool argument object from inline JSON, an <c>@file</c>, or redirected
/// standard input without allowing an unbounded client-side buffer.
/// </summary>

internal static class ToolArgumentReader
{
    internal const int MaxArgumentBytes = 1024 * 1024;

    public static bool TryRead(
        string? value,
        out JsonElement arguments,
        out string? error)
    {
        string json;

        if (value is null)
        {
            if (!Console.IsInputRedirected)
            {
                json = "{}";
            }
            else if (!TryReadCapped(Console.In, out json, out error))
            {
                arguments = default;

                return false;
            }
        }
        else if (value.StartsWith('@'))
        {
            string path = value[1..];

            if (string.IsNullOrWhiteSpace(path))
            {
                arguments = default;

                error = "The @file argument must include a file path.";

                return false;
            }

            try
            {
                using StreamReader reader = new(path, Encoding.UTF8, true);

                if (!TryReadCapped(reader, out json, out error))
                {
                    arguments = default;

                    return false;
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                arguments = default;

                error = $"Could not read tool arguments file '{path}': {ex.Message}";

                return false;
            }
        }
        else
        {
            json = value;

            if (Encoding.UTF8.GetByteCount(json) > MaxArgumentBytes)
            {
                arguments = default;

                error = $"Tool arguments exceed the {MaxArgumentBytes}-byte input limit.";

                return false;
            }
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    MaxDepth = 64,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments = default;

                error = "Tool arguments must be a JSON object.";

                return false;
            }

            arguments = document.RootElement.Clone();

            error = null;

            return true;
        }
        catch (JsonException ex)
        {
            arguments = default;

            error = $"Tool arguments must be a valid JSON object: {ex.Message}";

            return false;
        }
    }

    private static bool TryReadCapped(
        TextReader reader,
        out string value,
        out string? error)
    {
        CappedTextRead read = CappedInputReader.ReadText(reader, MaxArgumentBytes);

        if (read.TooLarge)
        {
            value = string.Empty;

            error = $"Tool arguments exceed the {MaxArgumentBytes}-byte input limit.";

            return false;
        }

        value = read.Text.Length == 0 ? "{}" : read.Text;

        error = null;

        return true;
    }
}
