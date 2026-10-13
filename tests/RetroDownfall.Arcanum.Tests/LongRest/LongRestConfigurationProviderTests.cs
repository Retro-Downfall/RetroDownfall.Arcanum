using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestConfigurationProviderTests
{
    [Fact]
    public void Core_view_captures_exact_recipe_and_destination_from_one_immutable_epoch()
    {
        LongRestConfigurationEpochTests.NotifyingOptions options = new(LongRestConfigurationEpochTests.Settings());

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        ILongRestConfigurationProvider provider = epoch;

        LongRestSweepConfiguration first = provider.Capture();

        Assert.True(first.Enabled);

        Assert.True(first.CovenantEnabled);

        Assert.True(first.Recipe.IsSuccess);

        Assert.Equal(epoch.Capture().Stamp, first.Stamp);

        Assert.Equal("provider", first.Recipe.Value.SummaryProvider);

        Assert.Equal("model", first.Recipe.Value.SummaryModel);

        Assert.Equal("embedding", first.Recipe.Value.EmbeddingModel);

        Assert.Equal(64, first.Recipe.Value.EmbeddingDimensions);

        Assert.Equal("7CE7BE06199B7D32316FBC38993697D98A1CDFB2F92E224CA77C4AE2E01078A1", first.Recipe.Value.SummaryDestinationDigest.ToString());

        Assert.Equal(first.Recipe.Value.SummaryDestinationDigest, first.Recipe.Value.EmbeddingDestinationDigest);

        ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

        changed.Providers[0].Endpoint = "https://changed.test/v1";

        options.Set(changed);

        LongRestSweepConfiguration second = provider.Capture();

        Assert.Equal("CF01CD6D1B06FDE79617ECB8013677387EA15A9AC3668E353075BC8E91B0DBF9", second.Recipe.Value.SummaryDestinationDigest.ToString());

        options.Set(LongRestConfigurationEpochTests.Settings());

        Assert.False(provider.IsCurrent(first.Stamp));

        Assert.Equal(first.Recipe.Value, provider.Capture().Recipe.Value);

        Assert.Equal(2, provider.Capture().Stamp.Epoch);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("embedding")]
    [InlineData("endpoint")]
    public void Disabled_or_unconfigured_views_never_supply_an_executable_recipe(string failure)
    {
        ArcanumSettings settings = LongRestConfigurationEpochTests.Settings();

        if (failure == "disabled") settings.Features.LongRest = false;

        if (failure == "embedding") settings.Integrations.Embeddings.Model = "";

        if (failure == "endpoint") settings.Providers[0].Endpoint = "not an endpoint";

        using LongRestConfigurationEpoch epoch = new(new LongRestConfigurationEpochTests.NotifyingOptions(settings), Guid.NewGuid());

        LongRestSweepConfiguration view = ((ILongRestConfigurationProvider)epoch).Capture();

        Assert.Equal(failure != "disabled", view.Enabled);

        Assert.True(view.Recipe.IsFailure);
    }

    [Fact]
    public void Serving_composition_uses_the_same_configuration_singleton_for_Core_and_Api()
    {
        ServiceCollection services = [];

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new LongRestConfigurationEpochTests.NotifyingOptions(LongRestConfigurationEpochTests.Settings()));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<LongRestConfigurationEpoch>(), provider.GetService<ILongRestConfigurationProvider>());
    }
}
