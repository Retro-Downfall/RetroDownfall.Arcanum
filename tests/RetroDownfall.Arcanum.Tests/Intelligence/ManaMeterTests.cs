using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Mana is token and cost accounting, so <see cref="IManaMeter"/> is declared beside
/// <see cref="IModelTokenEstimator"/> in <c>RetroDownfall.Arcanum.Core.Intelligence</c> and its
/// implementation beside <c>ManaPreflight</c> in <c>RetroDownfall.Arcanum.Api.Intelligence</c>. It
/// counts tokens for a model; it is neither an agent contract nor an authored resource.
/// </summary>
/// <remarks>
/// The namespace assertions are here because that failure is silent — a type left behind still compiles,
/// since every consumer keeps its old <c>using</c>. The counting assertions are here because the
/// interface is a one-method facade over <see cref="IModelTokenEstimator"/>: nothing but a comparison
/// against the estimator's own answer distinguishes "routes through the configured provider profile"
/// from "silently fell through to the unconfigured fallback".
/// </remarks>
public sealed class ManaMeterTests
{

    private const string CoreIntelligenceNamespace = "RetroDownfall.Arcanum.Core.Intelligence";

    private const string ApiIntelligenceNamespace = "RetroDownfall.Arcanum.Api.Intelligence";

    [Fact]
    public void Token_accounting_contract_is_declared_beside_the_token_estimator()
    {

        Assert.Equal(CoreIntelligenceNamespace, typeof(IManaMeter).Namespace);

        Assert.Equal(typeof(IModelTokenEstimator).Namespace, typeof(IManaMeter).Namespace);

    }

    [Fact]
    public void Token_accounting_implementation_is_declared_beside_the_other_mana_surfaces()
    {

        Assert.Equal(ApiIntelligenceNamespace, typeof(ManaMeter).Namespace);

        Assert.Equal(typeof(ManaPreflight).Namespace, typeof(ManaMeter).Namespace);

    }

    [Fact]
    public void Counting_an_empty_string_costs_nothing()
    {

        Assert.Equal(0, CreateMeter(out _).CountTokens(string.Empty));

    }

    [Fact]
    public void Counting_uses_the_profile_of_the_provider_configured_for_the_default_model()
    {

        const string text = "Unicode: \U0001F469\U0001F3FD‍\U0001F4BB café \U0001F680";

        IManaMeter meter = CreateMeter(out ModelTokenEstimator estimator);

        int expected = estimator
            .EstimateText(ConfiguredProvider(), "gpt-4o", text)
            .TokenCount;

        Assert.True(expected > 0, "The fixture text must cost tokens for the comparison to mean anything.");

        Assert.Equal(expected, meter.CountTokens(text));

    }

    private static IManaMeter CreateMeter(out ModelTokenEstimator estimator)
    {

        estimator = new ModelTokenEstimator(
            new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        ArcanumSettings settings = new()
        {
            DefaultModel = "gpt-4o",
            Providers = [ConfiguredProvider()],
        };

        return new ManaMeter(estimator, new TestOptionsMonitor<ArcanumSettings>(settings));

    }

    private static ProviderSettings ConfiguredProvider() =>
        new()
        {
            Name = "openai-compatible",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1",
            Models = [new ModelEntry("gpt-4o")],
            ContextWindowLimit = 128_000,
        };

}
