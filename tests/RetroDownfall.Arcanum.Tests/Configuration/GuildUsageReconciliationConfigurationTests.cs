using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class GuildUsageReconciliationConfigurationTests
{
    [Fact]
    public void Provider_round_trip_preserves_explicit_source()
    {
        const string json = "{\"name\":\"router\",\"endpoint\":\"https://openrouter.ai/api/v1\",\"guildUsageReconciliation\":\"OpenRouterGeneration\"}";

        ProviderSettings provider = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.ProviderSettings)!;

        string saved = JsonSerializer.Serialize(provider, ConfigurationJsonContext.Default.ProviderSettings);

        using JsonDocument document = JsonDocument.Parse(saved);

        Assert.Equal("OpenRouterGeneration", document.RootElement.GetProperty("guildUsageReconciliation").GetString());
    }
    [Fact]
    public void Physical_client_freeze_retains_source_after_caller_mutation()
    {
        ProviderSettings original = new() { GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration };

        ProviderSettings frozen = PromptCacheClientIdentity.Freeze(original);

        original.GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        Assert.Equal(GuildUsageReconciliationSource.OpenRouterGeneration, frozen.GuildUsageReconciliation);
    }

    [Fact]
    public void Old_provider_configuration_defaults_to_none()
    {
        ProviderSettings provider = JsonSerializer.Deserialize("{}", ConfigurationJsonContext.Default.ProviderSettings)!;

        Assert.Equal(GuildUsageReconciliationSource.None, provider.GuildUsageReconciliation);
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", true)]
    [InlineData("https://openrouter.ai:443/api/v1/", true)]
    [InlineData("http://openrouter.ai/api/v1", false)]
    [InlineData("https://openrouter.ai/api/v1?x=1", false)]
    [InlineData("https://user@openrouter.ai/api/v1", false)]
    [InlineData("https://openrouter.ai/api/v2", false)]
    [InlineData("https://evil.example/api/v1", false)]
    [InlineData("https://openrouter.ai:444/api/v1", false)]
    [InlineData("https://openrouter.ai/api/v1#fragment", false)]
    public void Opted_in_provider_requires_canonical_endpoint(string endpoint, bool valid)
    {
        ArcanumSettings settings = new() { Providers = [new ProviderSettings
        {
            Name = "router", Endpoint = endpoint, Models = ["model"],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
        }] };

        Assert.Equal(valid, new ConfigurationValidator().Validate(settings).IsSuccess);
    }

    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli)]
    [InlineData(AiProviderKind.CodexCli)]
    public void Familiar_cannot_opt_in(AiProviderKind kind)
    {
        ArcanumSettings settings = new() { Providers = [new ProviderSettings
        {
            Name = "familiar", Type = kind,
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
        }] };

        Assert.True(new ConfigurationValidator().Validate(settings).IsFailure);
    }

    [Fact]
    public void Undefined_source_is_invalid()
    {
        ArcanumSettings settings = new() { Providers = [new ProviderSettings
        {
            Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["model"],
            GuildUsageReconciliation = (GuildUsageReconciliationSource)42,
        }] };

        Assert.True(new ConfigurationValidator().Validate(settings).IsFailure);
    }
}
