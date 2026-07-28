using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Token-aware tool-result materializer. Uses a length/4 token estimate when a real
/// tokenizer is not injected — sufficient for bounding model-visible text.
/// </summary>
public sealed class ToolResultMaterializer : IToolResultMaterializer
{

    public const int DefaultMaxTokens = 2048;

    private const string TruncationMarkerBody =
        "[truncated: tool result exceeded token/byte budget]";

    private const string TruncationMarkerSeparator = "\n";

    private const string PreservedEndsSeparator = "\n…\n";

    public ToolResultMaterialization Materialize(string toolName, string rawText, ToolResultMaterializerOptions? options = null)
    {
        _ = toolName;

        string originalText = rawText ?? string.Empty;
        string text = Utf8Truncation.NormalizeInvalidUtf16(originalText);
        (int maxTokens, int maxBytes) = ResolveBudgets(options);
        int estimated = EstimateTokens(text);

        if (FitsBudget(text, maxTokens, maxBytes))
        {
            return new ToolResultMaterialization(
                text,
                WasTruncated: false,
                originalText.Length,
                estimated);
        }

        bool preserveEnds = options?.PreservePrefixAndSuffix ?? true;
        string marked = MaterializeTruncatedText(
            text,
            maxTokens,
            maxBytes,
            preserveEnds);

        return new ToolResultMaterialization(
            marked,
            WasTruncated: true,
            originalText.Length,
            estimated);
    }

    public ToolResultMaterialization MaterializeStructured<T>(
        string toolName,
        T result,
        JsonTypeInfo<T> jsonTypeInfo,
        ToolResultMaterializerOptions? options = null)
        where T : IStructuredToolResult<T>
    {
        _ = toolName;
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        (int maxTokens, int maxBytes) = ResolveBudgets(options);
        string serialized = JsonSerializer.Serialize(result, jsonTypeInfo);
        int originalEstimatedTokens = EstimateTokens(serialized);
        int originalCharLength = serialized.Length;

        if (FitsBudget(serialized, maxTokens, maxBytes))
        {
            return new ToolResultMaterialization(
                serialized,
                WasTruncated: false,
                originalCharLength,
                originalEstimatedTokens);
        }

        int low = 0;
        int high = result.MaterializationItemCount - 1;
        string? bestFit = null;

        while (low <= high)
        {
            int retainedCount = low + ((high - low) / 2);
            T candidate = result.RetainLeadingItems(retainedCount);

            if (candidate is null || candidate.MaterializationItemCount != retainedCount)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{nameof(IStructuredToolResult<T>.RetainLeadingItems)} "
                    + "must retain exactly the requested leading item count.");
            }

            serialized = JsonSerializer.Serialize(candidate, jsonTypeInfo);

            if (FitsBudget(serialized, maxTokens, maxBytes))
            {
                bestFit = serialized;
                low = retainedCount + 1;
            }
            else
            {
                high = retainedCount - 1;
            }
        }

        if (bestFit is not null)
        {
            return new ToolResultMaterialization(
                bestFit,
                WasTruncated: true,
                originalCharLength,
                originalEstimatedTokens);
        }

        string fallback = JsonSerializer.Serialize(
            new MinimalStructuredToolResultEnvelope(),
            McpJsonSerializerContext.Default.MinimalStructuredToolResultEnvelope);

        return new ToolResultMaterialization(
            fallback,
            WasTruncated: true,
            originalCharLength,
            originalEstimatedTokens);
    }

    private static (int MaxTokens, int MaxBytes) ResolveBudgets(ToolResultMaterializerOptions? options)
    {
        int maxTokens = Math.Max(0, options?.MaxTokens ?? DefaultMaxTokens);
        int defaultBytes = SaturatingTokenCharBudget(maxTokens);
        int maxBytes = Math.Max(0, options?.MaxUtf8Bytes ?? defaultBytes);

        return (maxTokens, maxBytes);
    }

    private static bool FitsBudget(string text, int maxTokens, int maxBytes) =>
        EstimateTokens(text) <= maxTokens && Encoding.UTF8.GetByteCount(text) <= maxBytes;

    private static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text)
            ? 0
            : (int)Math.Min(
                int.MaxValue,
                ((long)text.Length + 3L) / 4L);

    private static string MaterializeTruncatedText(
        string text,
        int maxTokens,
        int maxBytes,
        bool preserveEnds)
    {
        int maxChars = SaturatingTokenCharBudget(maxTokens);
        string boundedMarker = TakePrefix(
            TruncationMarkerBody,
            maxChars,
            maxBytes);

        if (!string.Equals(
                boundedMarker,
                TruncationMarkerBody,
                StringComparison.Ordinal))
        {
            return boundedMarker;
        }

        int markerReserveChars = SaturatingAdd(
            TruncationMarkerSeparator.Length,
            TruncationMarkerBody.Length);
        int markerReserveBytes = SaturatingAdd(
            Encoding.UTF8.GetByteCount(TruncationMarkerSeparator),
            Encoding.UTF8.GetByteCount(TruncationMarkerBody));
        int contentCharBudget = SaturatingSubtract(
            maxChars,
            markerReserveChars);
        int contentByteBudget = SaturatingSubtract(
            maxBytes,
            markerReserveBytes);

        if (contentCharBudget == 0 || contentByteBudget == 0)
        {
            return TruncationMarkerBody;
        }

        string content = preserveEnds
            ? TakePrefixAndSuffix(
                text,
                contentCharBudget,
                contentByteBudget)
            : TakePrefix(
                text,
                contentCharBudget,
                contentByteBudget);

        return content.Length == 0
            ? TruncationMarkerBody
            : content + TruncationMarkerSeparator + TruncationMarkerBody;
    }

    private static string TakePrefix(
        string text,
        int maxChars,
        int maxBytes)
    {
        if (string.IsNullOrEmpty(text)
            || maxChars <= 0
            || maxBytes <= 0)
        {
            return string.Empty;
        }

        int charCount = Utf8Truncation.SafeCharSliceLength(
            text,
            maxChars);

        if (charCount == 0)
        {
            return string.Empty;
        }

        charCount = Utf8Truncation.ChooseSafeCharCount(
            text.AsSpan(0, charCount),
            maxBytes);

        return charCount == 0
            ? string.Empty
            : text[..charCount];
    }

    private static string TakePrefixAndSuffix(
        string text,
        int maxChars,
        int maxBytes)
    {
        int separatorBytes =
            Encoding.UTF8.GetByteCount(PreservedEndsSeparator);

        if (maxChars <= PreservedEndsSeparator.Length
            || maxBytes <= separatorBytes)
        {
            return TakePrefix(text, maxChars, maxBytes);
        }

        int availableChars = maxChars - PreservedEndsSeparator.Length;
        int availableBytes = maxBytes - separatorBytes;
        int headCharBudget = SaturatingAdd(availableChars, 1) / 2;
        int tailCharBudget = availableChars - headCharBudget;

        int firstScalarChars = Utf8Truncation.SafeCharSliceLength(
            text,
            1);
        firstScalarChars =
            firstScalarChars == 0
                ? Math.Min(2, text.Length)
                : firstScalarChars;
        int lastScalarStart = Utf8Truncation.SafeCharSliceLength(
            text,
            text.Length - 1);
        int lastScalarChars = text.Length - lastScalarStart;

        if (headCharBudget < firstScalarChars
            || tailCharBudget < lastScalarChars)
        {
            return TakePrefix(text, maxChars, maxBytes);
        }

        int minimumHeadBytes = Encoding.UTF8.GetByteCount(
            text.AsSpan(0, firstScalarChars));
        int minimumTailBytes = Encoding.UTF8.GetByteCount(
            text.AsSpan(lastScalarStart));

        if (availableBytes
            < SaturatingAdd(
                minimumHeadBytes,
                minimumTailBytes))
        {
            return TakePrefix(text, maxChars, maxBytes);
        }

        int desiredHeadByteBudget =
            SaturatingAdd(availableBytes, 1) / 2;
        int headByteBudget = Math.Clamp(
            desiredHeadByteBudget,
            minimumHeadBytes,
            availableBytes - minimumTailBytes);

        string head = TakePrefix(
            text,
            headCharBudget,
            headByteBudget);
        int tailByteBudget =
            availableBytes - Encoding.UTF8.GetByteCount(head);
        int tailStart = ChooseSafeSuffixStart(
            text,
            tailCharBudget,
            tailByteBudget);

        if (head.Length == 0
            || tailStart >= text.Length
            || head.Length >= tailStart)
        {
            return TakePrefix(text, maxChars, maxBytes);
        }

        return head
            + PreservedEndsSeparator
            + text[tailStart..];
    }

    private static int ChooseSafeSuffixStart(
        string text,
        int maxChars,
        int maxBytes)
    {
        if (string.IsNullOrEmpty(text)
            || maxChars <= 0
            || maxBytes <= 0)
        {
            return text.Length;
        }

        int low = 0;
        int high = Math.Min(maxChars, text.Length);
        int bestStart = text.Length;

        while (low <= high)
        {
            int requestedChars = low + ((high - low) / 2);
            int proposedStart = text.Length - requestedChars;
            int safeStart = Utf8Truncation.SafeCharSliceLength(
                text,
                proposedStart);
            int actualChars = text.Length - safeStart;

            if (actualChars <= maxChars
                && Encoding.UTF8.GetByteCount(text.AsSpan(safeStart))
                    <= maxBytes)
            {
                bestStart = safeStart;
                low = requestedChars + 1;
            }
            else
            {
                high = requestedChars - 1;
            }
        }

        return bestStart;
    }

    private static int SaturatingTokenCharBudget(int maxTokens) =>
        maxTokens > int.MaxValue / 4
            ? int.MaxValue
            : maxTokens * 4;

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right
            ? int.MaxValue
            : left + right;

    private static int SaturatingSubtract(int left, int right) =>
        left <= right
            ? 0
            : left - right;
}
