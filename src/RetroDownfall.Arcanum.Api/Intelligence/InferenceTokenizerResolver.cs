using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Resolves a process-cached Tiktoken tokenizer by encoding name (default <c>o200k_base</c>) for
/// pre-flight token counting. The cache is keyed on the requested encoding so operators who set
/// <c>Arcanum:Intelligence:TokenizerEncoding</c> get the expected tokenizer; unknown encoding
/// names fall back to <c>o200k_base</c> with a warning so the hub never throws on misconfig.
/// </summary>
public sealed class InferenceTokenizerResolver(ILogger<InferenceTokenizerResolver> logger)
{

    internal const string DefaultEncodingName = "o200k_base";

    private readonly ConcurrentDictionary<string, Tokenizer> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Tokenizer ResolveTokenizer(string? encodingName)
    {

        string requested = string.IsNullOrWhiteSpace(encodingName)
            ? DefaultEncodingName
            : encodingName.Trim();

        if (_cache.TryGetValue(requested, out Tokenizer? cached))
        {
            return cached;
        }

        return _cache.GetOrAdd(requested, ResolveTokenizerSlow);

    }

    private Tokenizer ResolveTokenizerSlow(string requested)
    {

        try
        {
            Tokenizer created = TiktokenTokenizer.CreateForEncoding(requested);

            logger.LogDebug("Created Tiktoken tokenizer for encoding {EncodingName}.", requested);

            return created;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to create Tiktoken tokenizer for encoding {EncodingName}; falling back to {DefaultEncoding}.",
                requested,
                DefaultEncodingName);

            return _cache.GetOrAdd(
                DefaultEncodingName,
                static _ => TiktokenTokenizer.CreateForEncoding(DefaultEncodingName));
        }

    }

}
