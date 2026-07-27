using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

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
                    "credentialEnvironmentVariable": "FIREWORKS_API_KEY",
                    "models": [
                      {
                        "name": "accounts/fireworks/models/qwen3p7-plus",
                        "supportsVision": true,
                        "reasoning": {
                          "controlSupport": "EffortAndBudget",
                          "supportsSummary": true,
                          "supportsFull": false,
                          "supportsStreaming": true,
                          "reportsReasoningTokens": true,
                          "allowsClientOutput": true,
                          "wireDialect": "OpenRouter",
                          "maxBudgetTokens": 32768
                        }
                      }
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

        Assert.Equal(
            "FIREWORKS_API_KEY",
            settings.Providers[0].CredentialEnvironmentVariable);

        Assert.Equal(25600, settings.Providers[0].ContextWindowLimit);

        Assert.Single(settings.Providers[0].Models);

        Assert.Equal("accounts/fireworks/models/qwen3p7-plus", settings.Providers[0].Models[0].Name);

        Assert.True(settings.Providers[0].Models[0].SupportsVision);

        ReasoningCapabilities? reasoning = settings.Providers[0].Models[0].Reasoning;

        Assert.NotNull(reasoning);
        Assert.Equal(ReasoningControlSupport.EffortAndBudget, reasoning.ControlSupport);
        Assert.True(reasoning.SupportsSummary);
        Assert.False(reasoning.SupportsFull);
        Assert.True(reasoning.SupportsStreaming);
        Assert.True(reasoning.ReportsReasoningTokens);
        Assert.True(reasoning.AllowsClientOutput);
        Assert.Equal(ReasoningWireDialect.OpenRouter, reasoning.WireDialect);
        Assert.Equal(32_768, reasoning.MaxBudgetTokens);

    }

    [Fact]
    public void Configure_binds_nullable_reasoning_price_via_source_generator()
    {
        const string json =
            """
            {
              "Arcanum": {
                "cost": {
                  "pricing": {
                    "defaultPricing": {
                      "inputPer1M": 10,
                      "outputPer1M": 20,
                      "reasoningPer1M": 80,
                      "cachedPer1M": 1
                    }
                  }
                }
              }
            }
            """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(json));
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        ServiceCollection services = new();
        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ModelPricingEntry pricing =
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Cost.Pricing.DefaultPricing;

        Assert.Equal(10m, pricing.InputPer1M);
        Assert.Equal(20m, pricing.OutputPer1M);
        Assert.Equal(80m, pricing.ReasoningPer1M);
        Assert.Equal(1m, pricing.CachedPer1M);
    }

    [Fact]
    public void Configure_binds_workspace_check_feature_and_integration_via_source_generator()
    {
        const string json =
            """
            {
              "Arcanum": {
                "features": {
                  "workspaceChecks": true
                },
                "integrations": {
                  "workspaceChecks": {
                    "executableCatalog": {
                      "dotNet": {
                        "path": "/opt/dotnet/dotnet"
                      }
                    },
                    "customProfiles": {
                      "custom-build": {
                        "executableId": "dotnet",
                        "kind": "build",
                        "parser": "msBuild",
                        "fixedArguments": ["build"],
                        "options": {
                          "configuration": {
                            "allowedValues": {
                              "debug": ["--configuration", "Debug"],
                              "release": ["--configuration", "Release"]
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(json));
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        ServiceCollection services = new();
        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings settings =
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;
        WorkspaceCheckIntegrationSettings workspaceChecks =
            settings.Integrations.WorkspaceChecks;

        Assert.True(settings.Features.WorkspaceChecks);
        Assert.Equal("/opt/dotnet/dotnet", workspaceChecks.ExecutableCatalog.DotNet.Path);

        WorkspaceCheckProfileSettings profile =
            Assert.Single(workspaceChecks.CustomProfiles).Value;

        Assert.Equal("dotnet", profile.ExecutableId);
        Assert.Equal(WorkspaceCheckKind.Build, profile.Kind);
        Assert.Equal(WorkspaceCheckDiagnosticParserKind.MsBuild, profile.Parser);
        Assert.Equal(["build"], profile.FixedArguments);
        Assert.Equal(
            ["--configuration", "Release"],
            profile.Options["configuration"].AllowedValues["release"]);
    }

    [Theory]
    [InlineData(typeof(IntegrationSettings))]
    [InlineData(typeof(WorkspaceCheckIntegrationSettings))]
    [InlineData(typeof(WorkspaceCheckExecutableCatalogSettings))]
    [InlineData(typeof(WorkspaceCheckProfileSettings))]
    [InlineData(typeof(WorkspaceCheckProfileOptionSettings))]
    [InlineData(typeof(WorkspaceCheckKind))]
    [InlineData(typeof(WorkspaceCheckDiagnosticParserKind))]
    public void ConfigurationJsonContext_registers_workspace_check_integration_contract(Type type)
    {
        Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(type));
    }

}
