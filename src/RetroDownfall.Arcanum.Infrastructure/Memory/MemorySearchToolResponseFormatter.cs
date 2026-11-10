using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

internal static class MemorySearchToolResponseFormatter
{
    internal static Result<MemorySearchResponse> Format(MemorySearchResponse response, long allocationBytes)
    {
        MemorySearchResponse bounded = response with
        {
            Query = Excerpt(response.Query, 512),
            Results = [.. response.Results.Select(hit => hit with
            {
                Content = Excerpt(hit.Content, 1024),
                Title = Excerpt(hit.Title, 128),
                Provenance = Excerpt(hit.Provenance, 512),
                Retention = Excerpt(hit.Retention, 256),
            })],
        };

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(bounded, ArcanumCoreJsonContext.Default.MemorySearchResponse);

        return encoded.LongLength <= allocationBytes ? bounded
            : new Error(ErrorCodes.Guild.PayloadTooLarge, "Memory search metadata exceeds one response allocation; request a smaller limit or narrower scope.");
    }

    private static string Excerpt(string value, int byteLimit)
    {
        if (Encoding.UTF8.GetByteCount(value) <= byteLimit) return value;

        const string marker = " [TRUNCATED excerpt; inspect the source for full content]";

        int count = 0;

        StringBuilder text = new();

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (count + rune.Utf8SequenceLength > byteLimit - Encoding.UTF8.GetByteCount(marker)) break;

            text.Append(rune.ToString());

            count += rune.Utf8SequenceLength;
        }

        return text.Append(marker).ToString();
    }
}
