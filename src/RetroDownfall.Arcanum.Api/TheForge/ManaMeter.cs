using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Api.TheForge;

public sealed class ManaMeter : IManaMeter
{

    private readonly IModelTokenEstimator _estimator;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public ManaMeter(
        IModelTokenEstimator estimator,
        IOptionsMonitor<ArcanumSettings> settings)
    {

        _estimator = estimator;

        _settings = settings;

    }

    public int CountTokens(string text)
    {

        if (string.IsNullOrEmpty(text))
        {

            return 0;

        }

        ArcanumSettings current = _settings.CurrentValue;
        if (ProviderResolver.TryResolveProviderForModel(
                current,
                current.DefaultModel,
                out ProviderSettings? provider,
                out string model)
            && provider is not null)
        {
            return _estimator.EstimateText(provider, model, text).TokenCount;
        }

        ProviderSettings fallback = new()
        {
            Name = "unconfigured",
            Type = AiProviderKind.OpenAICompatible,
            Models = [new ModelEntry("unknown")],
        };
        return _estimator.EstimateText(fallback, "unknown", text).TokenCount;
    }

}
