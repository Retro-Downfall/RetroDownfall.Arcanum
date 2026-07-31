using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// Classifies an NDJSON frame before strict source-generated <see cref="IntelligenceEvent"/>
/// deserialization. This lets clients ignore additive future event types without weakening enum
/// conversion for known frames.
/// </summary>
public static class IntelligenceEventDiscriminator
{
    public static IntelligenceEventDiscriminatorResult Inspect(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return IntelligenceEventDiscriminatorResult.Malformed;
        }

        return Inspect(Encoding.UTF8.GetBytes(json));
    }

    public static IntelligenceEventDiscriminatorResult Inspect(ReadOnlySpan<byte> utf8Json)
    {
        return InspectCore(utf8Json, out _, out _);
    }

    public static IntelligenceEventDiscriminatorResult InspectAndNormalize(Span<byte> utf8Json)
    {
        IntelligenceEventDiscriminatorResult result =
            InspectCore(utf8Json, out int valueOffset, out string? canonicalWireValue);
        if (result == IntelligenceEventDiscriminatorResult.Known
            && valueOffset >= 0
            && canonicalWireValue is not null)
        {
            Encoding.UTF8.GetBytes(canonicalWireValue, utf8Json[valueOffset..]);
        }

        return result;
    }

    private static IntelligenceEventDiscriminatorResult InspectCore(
        ReadOnlySpan<byte> utf8Json,
        out int valueOffset,
        out string? canonicalWireValue)
    {
        valueOffset = -1;
        canonicalWireValue = null;
        if (utf8Json.IsEmpty)
        {
            return IntelligenceEventDiscriminatorResult.Malformed;
        }

        try
        {
            Utf8JsonReader reader = new(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return IntelligenceEventDiscriminatorResult.Malformed;
            }

            IntelligenceEventDiscriminatorResult? result = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.CurrentDepth == 1
                    && reader.ValueTextEquals("type"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        result = IntelligenceEventDiscriminatorResult.Malformed;
                        continue;
                    }

                    string? wireValue = reader.GetString();
                    if (string.IsNullOrEmpty(wireValue)
                        || !string.Equals(wireValue, wireValue.Trim(), StringComparison.Ordinal))
                    {
                        result = IntelligenceEventDiscriminatorResult.Malformed;
                        continue;
                    }

                    if (TryGetKnownWireValue(wireValue, out string canonical))
                    {
                        result = IntelligenceEventDiscriminatorResult.Known;
                        canonicalWireValue = canonical;
                        valueOffset = !reader.ValueIsEscaped
                            && reader.ValueSpan.Length == canonical.Length
                                ? checked((int)reader.TokenStartIndex) + 1
                                : -1;
                    }
                    else
                    {
                        result = IntelligenceEventDiscriminatorResult.Unknown;
                    }
                }

                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    return reader.Read()
                        ? IntelligenceEventDiscriminatorResult.Malformed
                        : result ?? IntelligenceEventDiscriminatorResult.Malformed;
                }
            }

            return IntelligenceEventDiscriminatorResult.Malformed;
        }
        catch (JsonException)
        {
            return IntelligenceEventDiscriminatorResult.Malformed;
        }
    }

    private static bool TryGetKnownWireValue(string wireValue, out string canonicalWireValue)
    {
        canonicalWireValue = KnownWireValues.FirstOrDefault(
            candidate => string.Equals(candidate, wireValue, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return canonicalWireValue.Length > 0;
    }

    private static readonly string[] KnownWireValues =
    [
        "status",
        "sessionBound",
        "conversationBound",
        "token",
        "reasoning",
        "context",
        "result",
        "error",
        "toolCall",
        "toolResult",
        "warded",
        "wardResolved",
        "toolError",
        "attachmentRefreshed",
    ];
}

public enum IntelligenceEventDiscriminatorResult
{
    Known,
    Unknown,
    Malformed,
}
