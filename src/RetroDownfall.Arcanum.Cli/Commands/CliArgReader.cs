namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Shared flag-value parsing for CLI commands: the `@filename` convention for reading inline
/// text-or-file option values, and `key=value` pair parsing for repeatable options.
///
/// The `@filename` prefix on a flag value (e.g. `--body @spell.md`) is a CLI-wide convention for
/// non-interactive commands, distinct from the `chat` REPL's inline `@path` staging within prompt
/// text. Both read file contents; the flag-value form is positional to an option, the REPL form is
/// inline within free text.
/// </summary>
internal static class CliArgReader
{

    public static bool TryReadInlineOrFile(string? value, out string result, out string? error)
    {

        if (string.IsNullOrEmpty(value))
        {
            result = string.Empty;

            error = null;

            return true;
        }

        if (value[0] != '@')
        {
            result = value;

            error = null;

            return true;
        }

        string path = value[1..];

        try
        {
            result = File.ReadAllText(path);

            error = null;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            result = string.Empty;

            error = $"Could not read file '{path}': {ex.Message}";

            return false;
        }

    }

    public static bool TryParseGuid(string? value, out Guid id)
    {
        return Guid.TryParse(value, out id);
    }

    public static bool TryParseKeyValuePairs(string[]? values, out Dictionary<string, string> result, out string? error)
    {

        result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (values is null)
        {
            error = null;

            return true;
        }

        foreach (string entry in values)
        {

            int eq = entry.IndexOf('=');

            if (eq <= 0)
            {
                error = $"Invalid key=value pair: '{entry}'. Expected format key=value.";

                return false;
            }

            string key = entry[..eq];

            string value = entry[(eq + 1)..];

            result[key] = value;

        }

        error = null;

        return true;

    }

}
