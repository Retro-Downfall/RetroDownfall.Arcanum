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
        out int exitCode,
        Action<string>? writeDiagnostic = null)
    {

        parsed = default;

        exitCode = 0;

        float? temperature = ParseFloat(settings.Temperature, "--temperature", palette, writeDiagnostic, ref exitCode, 0f, 2f);

        float? topP = ParseFloat(settings.TopP, "--top-p", palette, writeDiagnostic, ref exitCode, 0f, 1f);

        int? maxOutput = ParseInt(settings.MaxTokens, "--max-tokens", palette, writeDiagnostic, ref exitCode, min: 1);

        long? seed = ParseLong(settings.Seed, "--seed", palette, writeDiagnostic, ref exitCode);

        float? presence = ParseFloat(settings.PresencePenalty, "--presence-penalty", palette, writeDiagnostic, ref exitCode, -2f, 2f);

        float? frequency = ParseFloat(settings.FrequencyPenalty, "--frequency-penalty", palette, writeDiagnostic, ref exitCode, -2f, 2f);

        if (exitCode != 0)
        {
            return false;
        }

        string? responseFormat = NormalizeResponseFormat(settings.ResponseFormat);

        if (responseFormat is null && !string.IsNullOrWhiteSpace(settings.ResponseFormat))
        {
            WriteError(
                "--response-format",
                "must be one of: text, json, json_object, json_schema.",
                palette,
                writeDiagnostic);

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

    private static float? ParseFloat(
        string? raw,
        string flag,
        IThemePalette palette,
        Action<string>? writeDiagnostic,
        ref int exitCode,
        float? min = null,
        float? max = null)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {

            if (!TryValidateFloatRange(value, flag, palette, writeDiagnostic, ref exitCode, min, max))
            {
                return null;
            }

            return value;

        }

        WriteError(
            flag,
            $"must be a number (got '{raw}').",
            palette,
            writeDiagnostic);

        exitCode = 1;

        return null;

    }

    private static int? ParseInt(
        string? raw,
        string flag,
        IThemePalette palette,
        Action<string>? writeDiagnostic,
        ref int exitCode,
        int? min = null,
        int? max = null)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {

            if (min is not null && value < min.Value)
            {

                WriteError(
                    flag,
                    $"must be at least {min.Value} (got '{raw}').",
                    palette,
                    writeDiagnostic);

                exitCode = 1;

                return null;

            }

            if (max is not null && value > max.Value)
            {

                WriteError(
                    flag,
                    $"must be at most {max.Value} (got '{raw}').",
                    palette,
                    writeDiagnostic);

                exitCode = 1;

                return null;

            }

            return value;

        }

        WriteError(
            flag,
            $"must be an integer (got '{raw}').",
            palette,
            writeDiagnostic);

        exitCode = 1;

        return null;

    }

    private static bool TryValidateFloatRange(
        float value,
        string flag,
        IThemePalette palette,
        Action<string>? writeDiagnostic,
        ref int exitCode,
        float? min,
        float? max)
    {

        if (!float.IsFinite(value))
        {

            WriteError(
                flag,
                $"must be a finite number (got '{value.ToString(CultureInfo.InvariantCulture)}').",
                palette,
                writeDiagnostic);

            exitCode = 1;

            return false;

        }

        if (min is not null && value < min.Value)
        {

            WriteError(
                flag,
                $"must be at least {min.Value.ToString(CultureInfo.InvariantCulture)} (got '{value.ToString(CultureInfo.InvariantCulture)}').",
                palette,
                writeDiagnostic);

            exitCode = 1;

            return false;

        }

        if (max is not null && value > max.Value)
        {

            WriteError(
                flag,
                $"must be at most {max.Value.ToString(CultureInfo.InvariantCulture)} (got '{value.ToString(CultureInfo.InvariantCulture)}').",
                palette,
                writeDiagnostic);

            exitCode = 1;

            return false;

        }

        return true;

    }

    private static long? ParseLong(
        string? raw,
        string flag,
        IThemePalette palette,
        Action<string>? writeDiagnostic,
        ref int exitCode)
    {

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        WriteError(
            flag,
            $"must be a 64-bit integer (got '{raw}').",
            palette,
            writeDiagnostic);

        exitCode = 1;

        return null;

    }

    private static void WriteError(
        string flag,
        string message,
        IThemePalette palette,
        Action<string>? writeDiagnostic)
    {

        if (writeDiagnostic is not null)
        {

            writeDiagnostic($"{flag}: {message}");

            return;

        }

        AnsiConsole.MarkupLine(
            palette.ErrorLabelMarkup(
                Markup.Escape(flag),
                Markup.Escape(message)));

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
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            filtered.Add(item);
        }

        return filtered.Count == 0 ? null : filtered;

    }

}

/// <summary>
/// Surface that an <c>ask</c> / <c>chat</c> command adapts its method parameters to for
/// <see cref="InferenceFlagBinder"/>. Strings are used so the binder doesn't need to round-trip
/// through invariant-culture float parsing rules at the System.CommandLine binding layer.
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

/// <summary>
/// Adapts the <c>ask</c> / <c>chat</c> command methods' individual inference-flag parameters to
/// <see cref="IInferenceFlagInputs"/> for <see cref="InferenceFlagBinder.TryParse"/>.
/// </summary>
public readonly record struct InferenceFlagInputs(
    string? Temperature,
    string? TopP,
    string? MaxTokens,
    string? Seed,
    string[]? Stop,
    string? ResponseFormat,
    string? PresencePenalty,
    string? FrequencyPenalty) : IInferenceFlagInputs;
