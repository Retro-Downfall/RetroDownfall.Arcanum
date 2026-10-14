using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// Long Rest's chat judgment resolves the fast model first, like Campaign summaries do, and never silently falls back to the
/// default model when the fast model is set but unusable. The recipe, and therefore the epoch, follow the resolved choice.
/// </summary>
public sealed class LongRestFastModelTests
{
    [Fact]
    public void Chat_resolves_the_fast_model_when_it_is_set()
    {
        ArcanumSettings settings = WithFastModel("fast");

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        Result<LongRestProviderSelection> chat = epoch.Capture().ResolveChat();

        Assert.True(chat.IsSuccess);

        Assert.Equal("fast", chat.Value.Model);

        Assert.Equal("provider", chat.Value.Provider.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Chat_resolves_the_default_model_when_no_fast_model_is_set(string? fast)
    {
        ArcanumSettings settings = WithFastModel(fast);

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        Assert.Equal("model", epoch.Capture().ResolveChat().Value.Model);
    }

    [Fact]
    public void A_fast_model_is_trimmed_before_it_is_matched()
    {
        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(WithFastModel("  fast  ")), Guid.NewGuid());

        Assert.Equal("fast", epoch.Capture().ResolveChat().Value.Model);
    }

    [Fact]
    public void An_explicit_model_still_overrides_the_fast_model_so_an_admitted_choice_resolves_exactly()
    {
        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(WithFastModel("fast")), Guid.NewGuid());

        Assert.Equal("model", epoch.Capture().ResolveChat("model").Value.Model);
    }

    [Fact]
    public void A_fast_model_that_no_provider_advertises_fails_closed_and_never_falls_back_to_the_default_model()
    {
        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(WithFastModel("not-configured")), Guid.NewGuid());

        Assert.True(epoch.Capture().ResolveChat().IsFailure);

        LongRestSweepConfiguration view = ((ILongRestConfigurationProvider)epoch).Capture();

        Assert.True(view.Enabled);

        Assert.True(view.Recipe.IsFailure);

        // The same settings without the fast model resolve: the failure is the fast model's, not the recipe's.
        using LongRestConfigurationEpoch withoutFast = new(new LongRestConfigurationEpochTests.NotifyingOptions(WithFastModel(null)), Guid.NewGuid());

        Assert.True(((ILongRestConfigurationProvider)withoutFast).Capture().Recipe.IsSuccess);
    }

    [Fact]
    public void A_fast_model_served_by_a_provider_the_recipe_cannot_use_fails_closed_instead_of_using_the_default_model()
    {
        ArcanumSettings settings = WithFastModel("cli-fast");

        settings.Providers = [.. settings.Providers, new ProviderSettings { Name = "cli", Type = AiProviderKind.ClaudeCodeCli, Models = ["cli-fast"] }];

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        Assert.True(((ILongRestConfigurationProvider)epoch).Capture().Recipe.IsFailure);
    }

    [Fact]
    public void The_recipe_summary_identity_and_destination_follow_the_fast_model()
    {
        ArcanumSettings settings = WithFastModel("fast");

        settings.Providers = [.. settings.Providers, new ProviderSettings
        {
            Name = "quick", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://quick.test/v1", Models = ["fast"], ContextWindowLimit = 32000,
        }];

        // "fast" is advertised by the first provider too; the first match in configured order wins, exactly as for any model.
        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        LongRestRecipeIdentity recipe = ((ILongRestConfigurationProvider)epoch).Capture().Recipe.Value;

        Assert.Equal("fast", recipe.SummaryModel);

        Assert.Equal("provider", recipe.SummaryProvider);

        ArcanumSettings exclusive = WithFastModel("fast");

        exclusive.Providers[0].Models = ["model", "embedding"];

        exclusive.Providers = [.. exclusive.Providers, new ProviderSettings
        {
            Name = "quick", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://quick.test/v1", Models = ["fast"], ContextWindowLimit = 32000,
        }];

        using LongRestConfigurationEpoch moved = new(new LongRestConfigurationEpochTests.NotifyingOptions(exclusive), Guid.NewGuid());

        LongRestRecipeIdentity quick = ((ILongRestConfigurationProvider)moved).Capture().Recipe.Value;

        Assert.Equal("quick", quick.SummaryProvider);

        Assert.Equal("fast", quick.SummaryModel);

        Assert.NotEqual(recipe.SummaryDestinationDigest, quick.SummaryDestinationDigest);

        // The embedding half of the recipe is untouched by the chat choice.
        Assert.Equal(recipe.EmbeddingDestinationDigest, quick.EmbeddingDestinationDigest);

        Assert.NotEqual(LongRestDiscoveryHash.Recipe(recipe).Value, LongRestDiscoveryHash.Recipe(quick).Value);
    }

    [Fact]
    public void Setting_or_changing_the_fast_model_changes_the_recipe_digest_and_advances_the_epoch()
    {
        LongRestConfigurationEpochTests.NotifyingOptions options = new(WithFastModel(null));

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        ILongRestConfigurationProvider provider = epoch;

        LongRestSweepConfiguration before = provider.Capture();

        Assert.Equal("model", before.Recipe.Value.SummaryModel);

        options.Set(WithFastModel("fast"));

        LongRestSweepConfiguration after = provider.Capture();

        Assert.Equal("fast", after.Recipe.Value.SummaryModel);

        Assert.NotEqual(before.Stamp.ValueDigest, after.Stamp.ValueDigest);

        Assert.Equal(before.Stamp.Epoch + 1, after.Stamp.Epoch);

        Assert.False(provider.IsCurrent(before.Stamp));

        Assert.NotEqual(LongRestDiscoveryHash.Recipe(before.Recipe.Value).Value, LongRestDiscoveryHash.Recipe(after.Recipe.Value).Value);

        // Removing it restores the default model's recipe but never the earlier stamp.
        options.Set(WithFastModel(null));

        LongRestSweepConfiguration restored = provider.Capture();

        Assert.Equal("model", restored.Recipe.Value.SummaryModel);

        Assert.Equal(LongRestDiscoveryHash.Recipe(before.Recipe.Value).Value, LongRestDiscoveryHash.Recipe(restored.Recipe.Value).Value);

        Assert.False(provider.IsCurrent(before.Stamp));
    }

    [Fact]
    public void The_selected_fast_model_prices_with_its_own_rate()
    {
        ArcanumSettings settings = WithFastModel("fast");

        settings.Cost.Pricing.ModelPricing["fast"] = new ModelPricingEntry { InputPer1M = 0.5m, OutputPer1M = 0.75m };

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        LongRestProviderSelection chat = epoch.Capture().ResolveChat().Value;

        Assert.Equal(0.5m, chat.Pricing.InputPer1M);

        Assert.Equal(0.75m, chat.Pricing.OutputPer1M);
    }

    private static ArcanumSettings WithFastModel(string? fast)
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        settings.Providers[0].Models = ["model", "embedding", "fast"];

        settings.FastModel = fast;

        return settings;
    }
}
