using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestConfigurationEpochTests
{
    [Theory]
    [InlineData("long-rest")]
    [InlineData("covenant")]
    [InlineData("model")]
    [InlineData("pricing")]
    [InlineData("endpoint")]
    public void Returning_to_identical_values_never_revives_an_earlier_stamp(string transition)
    {
        NotifyingOptions options = new(Settings());

        Guid boot = Guid.NewGuid();

        using LongRestConfigurationEpoch epoch = new(options, boot);
        LongRestConfigurationSnapshot first = epoch.Capture();

        ArcanumSettings changed = Settings();

        switch (transition)
        {
            case "long-rest": changed.Features.LongRest = false; break;

            case "covenant": changed.Features.Covenant = false; break;

            case "model": changed.DefaultModel = "other"; break;

            case "pricing": changed.Cost.Pricing.DefaultPricing.InputPer1M = 7m; break;

            case "endpoint": changed.Providers[0].Endpoint = "https://other.test/v1"; break;
        }

        options.Set(changed);

        options.Set(Settings());

        LongRestConfigurationSnapshot last = epoch.Capture();

        Assert.Equal(boot, last.Stamp.BootId);

        Assert.Equal(first.Stamp.ValueDigest, last.Stamp.ValueDigest);

        Assert.Equal(2, last.Stamp.Epoch);

        Assert.False(epoch.IsCurrent(first.Stamp));

        Assert.True(epoch.IsCurrent(last.Stamp));
    }

    [Fact]
    public void A_captured_selection_owns_provider_model_dimensions_and_pricing()
    {
        NotifyingOptions options = new(Settings());

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());
        LongRestConfigurationSnapshot snapshot = epoch.Capture();

        LongRestProviderSelection chat = snapshot.ResolveChat().Value;

        LongRestProviderSelection embedding = snapshot.ResolveEmbedding().Value;

        options.CurrentValue.Providers[0].Endpoint = "https://changed.test/v1";

        options.CurrentValue.Cost.Pricing.DefaultPricing.InputPer1M = 100m;

        chat.Provider.Endpoint = "https://caller.test/v1";

        chat.Pricing.InputPer1M = 200m;

        LongRestProviderSelection again = snapshot.ResolveChat().Value;

        Assert.Equal("https://provider.test/v1", again.Provider.Endpoint);

        Assert.Equal("model", again.Model);

        Assert.Equal(2m, again.Pricing.InputPer1M);

        Assert.Equal("embedding", embedding.Model);

        Assert.Equal(64, embedding.Dimensions);
    }

    [Fact]
    public void Missing_optional_integration_configuration_uses_the_closed_disabled_default()
    {
        ArcanumSettings settings = Settings();

        settings.Integrations = null!;

        settings.Features.LongRest = false;

        using LongRestConfigurationEpoch epoch = new(new NotifyingOptions(settings), Guid.NewGuid());
        Assert.False(epoch.Capture().Enabled);

        Assert.True(epoch.Capture().ResolveEmbedding().IsFailure);

        Assert.Equal("model", epoch.Capture().ResolveChat().Value.Model);
    }

    [Fact]
    public void Embedding_selection_uses_its_configured_model_independently_of_chat_advertisements()
    {
        ArcanumSettings settings = Settings();

        settings.Providers[0].Models = ["model"];

        using LongRestConfigurationEpoch epoch = new(new NotifyingOptions(settings), Guid.NewGuid());

        var selected = epoch.Capture().ResolveEmbedding();

        Assert.True(selected.IsSuccess);

        Assert.Equal("embedding", selected.Value.Model);

        Assert.Equal("provider", selected.Value.Provider.Name);

        Assert.Equal(64, selected.Value.Dimensions);
    }

    internal static ArcanumSettings Settings() => new()
    {
        DefaultModel = "model",
        Features = new() { LongRest = true, Covenant = true },
        Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://provider.test/v1", Models = ["model", "embedding"], ContextWindowLimit = 128000 }],
        Integrations = new() { Embeddings = new() { Provider = "provider", Model = "embedding", Dimensions = 64 } },
        Cost = new() { Pricing = new() { DefaultPricing = new() { InputPer1M = 2m, OutputPer1M = 3m } } },
    };

    internal sealed class NotifyingOptions(ArcanumSettings value) : IOptionsMonitor<ArcanumSettings>
    {
        private event Action<ArcanumSettings, string?>? Changed;

        public ArcanumSettings CurrentValue { get; private set; } = value;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener)
        {
            Changed += listener;

            return new Subscription(() => Changed -= listener);
        }

        public void Set(ArcanumSettings next)
        {
            CurrentValue = next;

            Changed?.Invoke(next, null);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
