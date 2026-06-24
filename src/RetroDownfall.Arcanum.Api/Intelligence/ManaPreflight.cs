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

    private readonly BoundedLruCache<MessageTokenCacheKey, int> _messageTokenCache;

    public ManaPreflight(IOptionsMonitor<ArcanumSettings> settings)
    {

        int capacity = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            settings.CurrentValue.Grimoire?.MaxMessagesPerConversationLoad ?? new GrimoireSettings().MaxMessagesPerConversationLoad);

        _messageTokenCache = new BoundedLruCache<MessageTokenCacheKey, int>(capacity);

    }

    public bool ShouldSkipCompressionPreflight(IReadOnlyList<MeAiChatMessage> messages, int minMessages) =>
        messages.Count <= minMessages;

    public int CountTokens(
        IReadOnlyList<MeAiChatMessage> messages,
        Tokenizer tokenizer,
        int perMessageOverheadTokens,
        string encodingName)
    {

        int total = 0;

        foreach (MeAiChatMessage message in messages)
        {

            total += perMessageOverheadTokens;

            string? text = ExtractTextForCounting(message);

            if (string.IsNullOrEmpty(text))
            {

                continue;

            }

            MessageTokenCacheKey key = new(encodingName, ComputeContentHashHex(text));

            if (_messageTokenCache.TryGetValue(key, out int cached))
            {

                total += cached;

                continue;

            }

            int messageTokens = tokenizer.CountTokens(text);

            _messageTokenCache.Set(key, messageTokens);

            total += messageTokens;

        }

        return total;

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

        string? direct = message.Text;

        if (!string.IsNullOrEmpty(direct))
        {

            return direct;

        }

        IList<AIContent> contents = message.Contents;

        if (contents.Count == 0)
        {

            return null;

        }

        var sb = new StringBuilder(256);

        foreach (AIContent item in contents)
        {

            if (item is TextContent tc)
            {

                if (!string.IsNullOrEmpty(tc.Text))
                {

                    _ = sb.Append(tc.Text);

                }

            }
            else
            {

                string? s = item.ToString();

                if (!string.IsNullOrEmpty(s))
                {

                    _ = sb.Append(s);

                }

            }

        }

        return sb.Length == 0 ? null : sb.ToString();

    }

    private readonly record struct MessageTokenCacheKey(string EncodingName, string ContentHashHex);

}
