using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// Guards against the configuration binding source generator silently skipping <c>init</c>-only
/// properties (dotnet/runtime#107856). Reflection <c>.Bind()</c> still works with <c>init</c>, so
/// this test requires <c>EnableConfigurationBindingGenerator</c> on the test project and calls
/// <c>Configure&lt;ArcanumSettings&gt;</c> so the generated binder is exercised.
/// </summary>
public sealed class ArcanumSettingsBindingTests
{

    [Fact]
    public void Configure_binds_providers_and_default_model_via_source_generator()
    {

        string json = """
            {
              "Arcanum": {
                "providers": [
                  {
                    "name": "Fireworks",
                    "type": "OpenAICompatible",
                    "endpoint": "https://api.fireworks.ai/inference/v1",
                    "apiKey": "test-key",
                    "models": [
                      { "name": "accounts/fireworks/models/qwen3p7-plus", "supportsVision": true }
                    ],
                    "contextWindowLimit": 25600
                  }
                ],
                "defaultModel": "accounts/fireworks/models/qwen3p7-plus",
                "fastModel": "accounts/fireworks/models/qwen3p7-plus"
              }
            }
            """;

        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(json));

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider sp = services.BuildServiceProvider();

        ArcanumSettings settings = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.Equal("accounts/fireworks/models/qwen3p7-plus", settings.DefaultModel);

        Assert.Equal("accounts/fireworks/models/qwen3p7-plus", settings.FastModel);

        Assert.Single(settings.Providers);

        Assert.Equal("Fireworks", settings.Providers[0].Name);

        Assert.Equal(AiProviderKind.OpenAICompatible, settings.Providers[0].Type);

        Assert.Equal(25600, settings.Providers[0].ContextWindowLimit);

        Assert.Single(settings.Providers[0].Models);

        Assert.Equal("accounts/fireworks/models/qwen3p7-plus", settings.Providers[0].Models[0].Name);

        Assert.True(settings.Providers[0].Models[0].SupportsVision);

    }

}
