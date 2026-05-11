using System.Globalization;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Shared parser for the OpenAI-shaped inference flags exposed on the <c>ask</c> and
/// <c>chat</c> CLI commands. Returns nullable values that map 1:1 onto
/// <see cref="RetroDownfall.Arcanum.Core.Intelligence.PingRequest"/>.
/// </summary>
public static class InferenceFlagBinder
{

    public readonly record struct Parsed(
        float? Temperature,
        float? TopP,
        int? MaxOutputTokens,
        long? Seed,
        IReadOnlyList<string>? Stop,
        string? ResponseFormat,
        float? PresencePenalty,
        float? FrequencyPenalty);

    public static bool TryParse(
        IInferenceFlagInputs settings,
        IThemePalette palette,
        out Parsed parsed,
        out int exitCode)
    {

        parsed = default;

        exitCode = 0;

        float? temperature = ParseFloat(settings.Temperature, "--temperature", palette, ref exitCode);

        float? topP = ParseFloat(settings.TopP, "--top-p", palette, ref exitCode);

        int? maxOutput = ParseInt(settings.MaxTokens, "--max-tokens", palette, ref exitCode);

        long? seed = ParseLong(settings.Seed, "--seed", palette, ref exitCode);

        float? presence = ParseFloat(settings.PresencePenalty, "--presence-penalty", palette, ref exitCode);

        float? frequency = ParseFloat(settings.FrequencyPenalty, "--frequency-penalty", palette, ref exitCode);

        if (exitCode != 0)
        {
            return false;
        }

        string? responseFormat = NormalizeResponseFormat(settings.ResponseFormat);

        if (responseFormat is null && !string.IsNullOrWhiteSpace(settings.ResponseFormat))
        {
            AnsiConsole.MarkupLine(
                palette.ErrorLabelMarkup(
                    Markup.Escape("--response-format"),
                    Markup.Escape("must be one of: text, json_object, json_schema.")));

            exitCode = 1;

            return false;
        }

        IReadOnlyList<string>? stop = ParseStop(settings.Stop);

        parsed = new Parsed(
            temperature,
            topP,
            maxOutput,
            seed,
            stop,
            responseFormat,
            presence,
            frequency);

        return true;

    }

    private static float? ParseFloat(string? raw, string flag, IThemePalette palette, ref int exitCode)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            return value;
        }

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape(flag),
                Markup.Escape($"must be a number (got '{raw}').")));

        exitCode = 1;

        return null;

    }

    private static int? ParseInt(string? raw, string flag, IThemePalette palette, ref int exitCode)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape(flag),
                Markup.Escape($"must be an integer (got '{raw}').")));

        exitCode = 1;

        return null;

    }

    private static long? ParseLong(string? raw, string flag, IThemePalette palette, ref int exitCode)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape(flag),
                Markup.Escape($"must be a 64-bit integer (got '{raw}').")));

        exitCode = 1;

        return null;

    }

    private static string? NormalizeResponseFormat(string? raw)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "text" => "text",
            "json" => "json_object",
            "json_object" => "json_object",
            "json_schema" => "json_schema",
            _ => null,
        };

    }

    private static IReadOnlyList<string>? ParseStop(string[]? raw)
    {

        if (raw is null || raw.Length == 0)
        {
            return null;
        }

        List<string> filtered = new(raw.Length);

        foreach (string item in raw)
        {
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }

            filtered.Add(item);
        }

        return filtered.Count == 0 ? null : filtered;

    }

}

/// <summary>
/// Surface that an <c>ask</c> / <c>chat</c> settings record exposes to <see cref="InferenceFlagBinder"/>.
/// Strings are used so Spectre's reflection-light binder doesn't need to round-trip through the
/// invariant culture float parsing rules.
/// </summary>
public interface IInferenceFlagInputs
{

    string? Temperature { get; }

    string? TopP { get; }

    string? MaxTokens { get; }

    string? Seed { get; }

    string[]? Stop { get; }

    string? ResponseFormat { get; }

    string? PresencePenalty { get; }

    string? FrequencyPenalty { get; }

}
