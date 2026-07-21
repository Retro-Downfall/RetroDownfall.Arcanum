using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Token-aware tool-result materializer. Uses a length/4 token estimate when a real
/// tokenizer is not injected — sufficient for bounding model-visible text.
/// </summary>
public sealed class ToolResultMaterializer : IToolResultMaterializer
{

    public const int DefaultMaxTokens = 2048;

    public ToolResultMaterialization Materialize(string toolName, string rawText, ToolResultMaterializerOptions? options = null)
    {
        _ = toolName;

        string text = rawText ?? string.Empty;
        int maxTokens = options?.MaxTokens ?? DefaultMaxTokens;
        int maxBytes = options?.MaxUtf8Bytes ?? (maxTokens * 4);
        int estimated = EstimateTokens(text);

        if (estimated <= maxTokens && Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return new ToolResultMaterialization(text, WasTruncated: false, text.Length, estimated);
        }

        bool preserveEnds = options?.PreservePrefixAndSuffix ?? true;

        string truncated = preserveEnds
            ? TruncatePrefixSuffix(text, maxTokens)
            : TruncatePrefix(text, maxTokens);

        string marked = truncated + "\n[truncated: tool result exceeded token/byte budget]";

        if (Encoding.UTF8.GetByteCount(marked) > maxBytes)
        {
            marked = Utf8Truncation.TruncateToUtf8ByteBudget(marked, maxBytes);
        }

        return new ToolResultMaterialization(marked, WasTruncated: true, text.Length, estimated);
    }

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (text.Length + 3) / 4);

    private static string TruncatePrefix(string text, int maxTokens)
    {
        int maxChars = Math.Max(16, maxTokens * 4);

        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string TruncatePrefixSuffix(string text, int maxTokens)
    {
        int maxChars = Math.Max(32, maxTokens * 4);

        if (text.Length <= maxChars)
        {
            return text;
        }

        int head = maxChars / 2;
        int tail = maxChars - head - 16;

        if (tail < 8)
        {
            return text[..maxChars];
        }

        return text[..head] + "\n…\n" + text[^tail..];
    }

}
