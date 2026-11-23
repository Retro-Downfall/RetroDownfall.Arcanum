using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Core.Configuration;

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

    private readonly object _initializationLock = new();

    private readonly OllamaQwenTokenizer _ollamaQwen = new();

    public Tokenizer ResolveTokenizer(string? encodingName) => Resolve(encodingName).Tokenizer;

    internal string? FindQualifiedOllamaManifestDigest(string model) =>
        _ollamaQwen.FindQualifiedManifestDigest(model);

    internal InferenceTextCounter? TryResolveInstalledOllama(ProviderSettings provider, string model) =>
        provider.LoadedOllamaModel is { } observed && observed.Model == model
            ? _ollamaQwen.TryResolve(model, observed.Digest)
            : null;

    internal ResolvedInferenceTokenizer Resolve(string? encodingName)
    {
        string requested = string.IsNullOrWhiteSpace(encodingName)
            ? DefaultEncodingName
            : encodingName.Trim();

        if (_cache.TryGetValue(requested, out ResolvedInferenceTokenizer? cached))
        {
            return cached;
        }

        lock (_initializationLock)
        {
            return _cache.GetOrAdd(requested, ResolveTokenizerSlow);
        }
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
    public TiktokenTokenizer Tokenizer => Counter.Tokenizer!;
}

/// <summary>Owns the exact embedded-encoding factory result; accepts no caller tokenizer or hooks.</summary>
internal sealed class InferenceTextCounter
{
    private readonly Func<string, int> _count;

    public InferenceTextCounter(string encodingName)
    {
        TiktokenTokenizer tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);

        Tokenizer = tokenizer;

        _count = text => tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: false);
    }

    public InferenceTextCounter(Func<string, int> count) => _count = count;

    public TiktokenTokenizer? Tokenizer { get; }

    public int CountTokens(string text) => _count(text);
}
