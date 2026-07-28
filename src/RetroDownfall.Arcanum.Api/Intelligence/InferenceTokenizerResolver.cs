using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Resolves a process-cached Tiktoken tokenizer by encoding name (code-owned default
/// <c>o200k_base</c>) for pre-flight token counting. The cache remains keyed by the requested
/// encoding for internal capability profiles; unknown names fall back to <c>o200k_base</c> with a
/// warning.
/// </summary>
public sealed class InferenceTokenizerResolver(ILogger<InferenceTokenizerResolver> logger)
{

    internal const string DefaultEncodingName = "o200k_base";

    private readonly ConcurrentDictionary<string, ResolvedInferenceTokenizer> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Tokenizer ResolveTokenizer(string? encodingName) => Resolve(encodingName).Tokenizer;

    internal ResolvedInferenceTokenizer Resolve(string? encodingName)
    {

        string requested = string.IsNullOrWhiteSpace(encodingName)
            ? DefaultEncodingName
            : encodingName.Trim();

        if (_cache.TryGetValue(requested, out ResolvedInferenceTokenizer? cached))
        {
            return cached;
        }

        return _cache.GetOrAdd(requested, ResolveTokenizerSlow);

    }

    private ResolvedInferenceTokenizer ResolveTokenizerSlow(string requested)
    {

        try
        {
            Tokenizer created = TiktokenTokenizer.CreateForEncoding(requested);

            logger.LogDebug("Created Tiktoken tokenizer for encoding {EncodingName}.", requested);

            return new ResolvedInferenceTokenizer(created, requested, requested, UsedFallback: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to create Tiktoken tokenizer for encoding {EncodingName}; falling back to {DefaultEncoding}.",
                requested,
                DefaultEncodingName);

            ResolvedInferenceTokenizer fallback = _cache.GetOrAdd(
                DefaultEncodingName,
                static _ => new ResolvedInferenceTokenizer(
                    TiktokenTokenizer.CreateForEncoding(DefaultEncodingName),
                    DefaultEncodingName,
                    DefaultEncodingName,
                    UsedFallback: false));

            return fallback with
            {
                RequestedEncoding = requested,
                UsedFallback = true,
            };
        }

    }

}

internal sealed record ResolvedInferenceTokenizer(
    Tokenizer Tokenizer,
    string RequestedEncoding,
    string ActualEncoding,
    bool UsedFallback);
