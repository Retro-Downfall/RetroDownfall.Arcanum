using System.Globalization;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Reads a supported observation; parsing does not establish source authority or finality.</summary>
internal static class GuildOpenRouterSpendParser
{
    private const int MaximumBytes = 64 * 1024;

    internal static Result<GuildSpendAmountsRequest> Parse(ReadOnlyMemory<byte> utf8, string expectedGenerationId, string expectedModel)
    {
        if (utf8.Length > MaximumBytes || !ValidGenerationId(expectedGenerationId) || string.IsNullOrWhiteSpace(expectedModel))
        {
            return Unavailable();
        }

        try
        {
            // JsonDocument may defer decoding unused strings. Validate all bytes, including ignored fields.
            _ = new UTF8Encoding(false, true).GetCharCount(utf8.Span);

            if (!ValidJsonStrings(utf8.Span)) return Unavailable();

            using var document = JsonDocument.Parse(utf8);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryUniqueProperty(document.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !TryUniqueProperty(data, "id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != expectedGenerationId ||
                !TryUniqueProperty(data, "model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != expectedModel ||
                !TryUniqueProperty(data, "api_type", out var apiType) || apiType.ValueKind != JsonValueKind.String || apiType.GetString() != "completions" ||
                !TryUniqueProperty(data, "is_byok", out var byok) || byok.ValueKind != JsonValueKind.False ||
                !TryCount(data, "native_tokens_prompt", out long input) || !TryCount(data, "native_tokens_completion", out long output) ||
                !TryCount(data, "native_tokens_cached", out long cached) || !TryCount(data, "native_tokens_reasoning", out long reasoning) ||
                !TryCount(data, "native_tokens_completion_images", out long images) || images != 0 ||
                !TryCount(data, "num_input_audio_prompt", out long audio) || audio != 0 ||
                !TryCount(data, "num_media_prompt", out long promptMedia) || promptMedia != 0 ||
                !TryCount(data, "num_media_completion", out long completionMedia) || completionMedia != 0 ||
                !TryUniqueProperty(data, "total_cost", out var cost) || cost.ValueKind != JsonValueKind.Number ||
                !ExactDecimalText.TryParse(cost.GetRawText(), out decimal usd))
            {
                return Unavailable();
            }

            var amounts = new GuildSpendAmountsRequest(input, output, cached, reasoning, usd);

            var validation = GuildSpendCorrectionPolicy.Validate(new GuildSpendFacts(0, 0, 0, 0, 0, false, false, false), amounts);

            return validation.IsSuccess ? amounts : Unavailable();
        }
        catch (JsonException)
        {
            return Unavailable();
        }
        catch (DecoderFallbackException)
        {
            return Unavailable();
        }
    }

    private static bool ValidGenerationId(string id)
    {
        if (id is null || id.Length is < 5 or > 128 || !id.StartsWith("gen-", StringComparison.Ordinal)) return false;

        foreach (char c in id.AsSpan(4))
        {
            if (c is not (>= '0' and <= '9') and not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not '-') return false;
        }

        return true;
    }

    private static bool ValidJsonStrings(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    // Force decoding even for ignored nested strings: JSON's escaped UTF-16 must be paired.
                    _ = reader.GetString();
                }
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryUniqueProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;

        bool found = false;

        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;

            if (found) return false;

            found = true;

            value = property.Value;
        }

        return found;
    }

    private static bool TryCount(JsonElement parent, string name, out long count)
    {
        count = 0;

        return TryUniqueProperty(parent, name, out var value) && value.ValueKind == JsonValueKind.Number &&
            long.TryParse(value.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0;
    }

    private static Error Unavailable() => new(ErrorCodes.Guild.InvalidRequest, "Generation metadata is unavailable.");
}
