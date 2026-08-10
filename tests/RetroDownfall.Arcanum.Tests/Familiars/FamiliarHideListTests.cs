using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// The hide list is a display filter and nothing more. Every listing surface reads
/// <see cref="ModelInfoBuilder"/>, so applying it there covers <c>/api/models</c>,
/// <c>/v1/models</c>, <c>arcanum model list</c>, the Command Center drop-down, and The Forge's
/// Arsenal from one place — while resolution, which does not read it, keeps a hidden model callable
/// by name.
/// </summary>
public sealed class FamiliarHideListTests
{

    [Fact]
    public void An_empty_hide_list_offers_every_declared_model()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            Settings(declared: ["claude-sonnet", "claude-opus"], hidden: []));

        Assert.Equal(["claude-sonnet", "claude-opus"], models.Select(static m => m.Model));

    }

    [Fact]
    public void A_hidden_model_is_omitted_from_every_listing()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            Settings(declared: ["claude-sonnet", "claude-legacy"], hidden: ["claude-legacy"]));

        Assert.Equal(["claude-sonnet"], models.Select(static m => m.Model));

    }

    [Fact]
    public void Hide_list_matching_is_case_insensitive()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            Settings(declared: ["Claude-Legacy"], hidden: ["claude-legacy"]));

        Assert.Empty(models);

    }

    /// <summary>
    /// Matching is exact, not prefix or substring — hiding <c>claude-legacy</c> must not also hide
    /// <c>claude-legacy-2</c>, which is a different model.
    /// </summary>
    [Fact]
    public void Hide_list_matching_is_exact_not_a_prefix()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            Settings(declared: ["claude-legacy", "claude-legacy-2"], hidden: ["claude-legacy"]));

        Assert.Equal(["claude-legacy-2"], models.Select(static m => m.Model));

    }

    /// <summary>
    /// An entry naming a model this provider does not currently offer is kept, not pruned, so a
    /// model that disappears for a release comes back still hidden.
    /// </summary>
    [Fact]
    public void An_entry_for_a_model_that_is_not_offered_hides_nothing_and_breaks_nothing()
    {

        List<ModelInfoDto> models = ModelInfoBuilder.BuildModelInfoList(
            Settings(declared: ["claude-sonnet"], hidden: ["claude-retired"]));

        Assert.Equal(["claude-sonnet"], models.Select(static m => m.Model));

    }

    /// <summary>
    /// The regression guard for the whole feature: a model the operator never wrote down becomes
    /// available with no <c>arcanum.json</c> edit, purely because it is not on the hide list.
    /// </summary>
    [Fact]
    public void A_newly_released_model_is_available_with_no_configuration_edit()
    {

        ArcanumSettings settings = Settings(declared: [], hidden: ["claude-legacy"]);

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "claude-released-this-morning",
            out ProviderSettings? provider,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Equal("ClaudeCode-subscription", provider!.Name);

        Assert.Equal("claude-released-this-morning", resolvedModel);

    }

    /// <summary>
    /// An explicit <c>"models": null</c> in <c>arcanum.json</c> is a legal Familiar row —
    /// <c>ConfigurationValidator</c> accepts it and every other consumer reads it as "no models" —
    /// so the listing surfaces must too rather than throwing a 500.
    /// </summary>
    [Fact]
    public void A_provider_with_a_null_model_list_contributes_nothing_and_does_not_throw()
    {

        ArcanumSettings settings = Settings(declared: [], hidden: []);

        settings.Providers![0].Models = null!;

        Assert.Empty(ModelInfoBuilder.BuildModelInfoList(settings));

    }

    private static ArcanumSettings Settings(string[] declared, string[] hidden) =>
        new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Models = [.. declared.Select(static name => new ModelEntry(name))],
                    HiddenModels = hidden,
                },
            ],
        };

}
