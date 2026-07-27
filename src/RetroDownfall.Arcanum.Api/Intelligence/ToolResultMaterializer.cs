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

    public ToolResultMaterialization Materialize(string toolName, string rawText, ToolResultMaterializerOptions? options = null)
    {
        _ = toolName;

        string text = rawText ?? string.Empty;
        (int maxTokens, int maxBytes) = ResolveBudgets(options);
        int estimated = EstimateTokens(text);

        if (FitsBudget(text, maxTokens, maxBytes))
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
        int defaultBytes = maxTokens > int.MaxValue / 4 ? int.MaxValue : maxTokens * 4;
        int maxBytes = Math.Max(0, options?.MaxUtf8Bytes ?? defaultBytes);

        return (maxTokens, maxBytes);
    }

    private static bool FitsBudget(string text, int maxTokens, int maxBytes) =>
        EstimateTokens(text) <= maxTokens && Encoding.UTF8.GetByteCount(text) <= maxBytes;

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
