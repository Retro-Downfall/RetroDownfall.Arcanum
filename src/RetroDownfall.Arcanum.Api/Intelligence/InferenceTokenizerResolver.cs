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
            InferenceTextCounter created = new(requested);

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
                    new InferenceTextCounter(DefaultEncodingName),
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
    InferenceTextCounter Counter,
    string RequestedEncoding,
    string ActualEncoding,
    bool UsedFallback)
{
    public TiktokenTokenizer Tokenizer => Counter.Tokenizer;
}

/// <summary>Owns the exact embedded-encoding factory result; accepts no caller tokenizer or hooks.</summary>
internal sealed class InferenceTextCounter(string encodingName)
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);

    public TiktokenTokenizer Tokenizer => _tokenizer;

    public int CountTokens(string text) =>
        _tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: false);
}
