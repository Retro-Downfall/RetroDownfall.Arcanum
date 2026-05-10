using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Microsoft.ML.Tokenizers;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Resolves a process-cached Tiktoken tokenizer (o200k_base) for pre-flight token counting.
/// </summary>
public sealed class InferenceTokenizerResolver(ILogger<InferenceTokenizerResolver> logger)
{

    private const string O200kEncodingName = "o200k_base";

    private readonly ConcurrentDictionary<string, Tokenizer> _cache = new(StringComparer.Ordinal);

    public Tokenizer ResolveTokenizer(AiProviderKind providerKind, string resolvedModel)
    {

        logger.LogDebug(
            "Resolving inference tokenizer for provider {ProviderKind}, model {ResolvedModel}.",
            providerKind,
            resolvedModel);

        return _cache.GetOrAdd(
            O200kEncodingName,
            static _ => TiktokenTokenizer.CreateForEncoding(O200kEncodingName));

    }

}
