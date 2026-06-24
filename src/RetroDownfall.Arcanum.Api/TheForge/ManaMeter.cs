using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Api.TheForge;

public sealed class ManaMeter : IManaMeter
{

    private readonly InferenceTokenizerResolver _resolver;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public ManaMeter(
        InferenceTokenizerResolver resolver,
        IOptionsMonitor<ArcanumSettings> settings)
    {

        _resolver = resolver;

        _settings = settings;

    }

    public int CountTokens(string text)
    {

        if (string.IsNullOrEmpty(text))
        {

            return 0;

        }

        string encoding = _settings.CurrentValue.Intelligence?.TokenizerEncoding ?? InferenceTokenizerResolver.DefaultEncodingName;

        Microsoft.ML.Tokenizers.Tokenizer tokenizer = _resolver.ResolveTokenizer(encoding);

        return tokenizer.CountTokens(text);
    }

}
