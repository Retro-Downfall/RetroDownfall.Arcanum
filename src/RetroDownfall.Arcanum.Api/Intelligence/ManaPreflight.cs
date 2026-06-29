using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Pre-flight token counting for context compression with per-message memoization.
/// </summary>
public sealed class ManaPreflight
{

    // W3.3 Fix 6: the LRU capacity is reloaded on IOptionsMonitor.OnChange. The
    // single BoundedLruCache field is swapped atomically via Volatile.Write; in-
    // flight CountTokens calls finish against the old cache and new calls pick up
    // the resized one. Losing the old cache contents on a swap is acceptable (the
    // token count is recomputed on the next miss). The OnChange subscription lives
    // for the singleton's process lifetime; ManaPreflight is registered as a
    // singleton and does not implement IDisposable.
    private BoundedLruCache<MessageTokenCacheKey, int> _messageTokenCache;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public ManaPreflight(IOptionsMonitor<ArcanumSettings> settings)
    {

        _settings = settings;

        _messageTokenCache = new BoundedLruCache<MessageTokenCacheKey, int>(ResolveCapacity(settings.CurrentValue));

        _ = settings.OnChange(OnSettingsChanged);

    }

    private static int ResolveCapacity(ArcanumSettings settings)
    {

        return ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            settings.Grimoire?.MaxMessagesPerConversationLoad ?? new GrimoireSettings().MaxMessagesPerConversationLoad);

    }

    private void OnSettingsChanged(ArcanumSettings settings, string? _)
    {

        BoundedLruCache<MessageTokenCacheKey, int> resized = new(ResolveCapacity(settings));

        Volatile.Write(ref _messageTokenCache, resized);

    }

    public bool ShouldSkipCompressionPreflight(IReadOnlyList<MeAiChatMessage> messages, int minMessages) =>
        messages.Count <= minMessages;

    public int CountTokens(
        IReadOnlyList<MeAiChatMessage> messages,
        Tokenizer tokenizer,
        int perMessageOverheadTokens,
        string encodingName)
    {

        BoundedLruCache<MessageTokenCacheKey, int> cache = Volatile.Read(ref _messageTokenCache);

        // W3.5: accumulate in long and saturate to int.MaxValue. A very large history previously
        // could overflow the int and wrap negative, making (total <= effectiveLimit) true and
        // SKIPPING compression exactly when it was most needed.
        long total = 0L;

        foreach (MeAiChatMessage message in messages)
        {

            total += perMessageOverheadTokens;

            string? text = ExtractTextForCounting(message);

            if (string.IsNullOrEmpty(text))
            {

                continue;

            }

            MessageTokenCacheKey key = new(encodingName, ComputeContentHashHex(text));

            if (cache.TryGetValue(key, out int cached))
            {

                total += cached;

                continue;

            }

            int messageTokens = tokenizer.CountTokens(text);

            cache.Set(key, messageTokens);

            total += messageTokens;

        }

        return total > int.MaxValue ? int.MaxValue : (int)total;

    }

    private static string ComputeContentHashHex(string text)
    {

        int byteCount = Encoding.UTF8.GetByteCount(text);

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {

            _ = Encoding.UTF8.GetBytes(text, rented);

            Span<byte> hash = stackalloc byte[32];

            _ = SHA256.TryHashData(rented.AsSpan(0, byteCount), hash, out _);

            return Convert.ToHexString(hash[..16]);

        }
        finally
        {

            ArrayPool<byte>.Shared.Return(rented);

        }

    }

    private static string? ExtractTextForCounting(MeAiChatMessage message)
    {

        // W3.5: enumerate ALL contents (not just message.Text). A message that carries both text
        // and non-text parts (e.g. an assistant turn with tool calls) previously counted only the
        // text via the message.Text short-circuit, under-counting the non-text payload.
        IList<AIContent> contents = message.Contents;

        if (contents.Count == 0)
        {

            return message.Text;

        }

        var sb = new StringBuilder(256);

        foreach (AIContent item in contents)
        {

            string? s = item is TextContent tc ? tc.Text : item.ToString();

            if (!string.IsNullOrEmpty(s))
            {

                _ = sb.Append(s);

            }

        }

        return sb.Length == 0 ? message.Text : sb.ToString();

    }

    private readonly record struct MessageTokenCacheKey(string EncodingName, string ContentHashHex);

}
