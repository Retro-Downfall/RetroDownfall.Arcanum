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
                    "apiKey": "test-key",
                    "promptCaching": {
                      "controlMode": "providerManaged",
                      "reportsCachedInputUsage": true
                    },
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
                        },
                        "promptCaching": {
                          "controlMode": "none",
                          "reportsCachedInputUsage": false
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

        PromptCachingProfile providerCaching = Assert.IsType<PromptCachingProfile>(
            settings.Providers[0].PromptCaching);
        Assert.Equal(PromptCachingControlMode.ProviderManaged, providerCaching.ControlMode);
        Assert.True(providerCaching.ReportsCachedInputUsage);

        PromptCachingProfile modelCaching = Assert.IsType<PromptCachingProfile>(
            settings.Providers[0].Models[0].PromptCaching);
        Assert.Equal(PromptCachingControlMode.None, modelCaching.ControlMode);
        Assert.False(modelCaching.ReportsCachedInputUsage);

    }

    [Fact]
    public void Configure_binds_nullable_reasoning_price_via_source_generator()
    {
        const string json =
            """
            {
              "Arcanum": {
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
            """;
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(json));
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        ServiceCollection services = new();
        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ModelPricingEntry pricing =
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Pricing.DefaultPricing;

        Assert.Equal(10m, pricing.InputPer1M);
        Assert.Equal(20m, pricing.OutputPer1M);
        Assert.Equal(80m, pricing.ReasoningPer1M);
        Assert.Equal(1m, pricing.CachedPer1M);
    }

    [Fact]
    public void Configure_binds_coding_tool_bounds_and_opaque_check_profiles_via_source_generator()
    {
        const string json =
            """
            {
              "Arcanum": {
                "codingTools": {
                  "search": {
                    "maxPatternChars": 2048,
                    "regexTimeoutMilliseconds": 125,
                    "maxElapsedMilliseconds": 9000,
                    "maxFiles": 750,
                    "maxBytes": 8388608,
                    "maxTraversalSteps": 50000,
                    "maxMatches": 250,
                    "maxPreviewChars": 320
                  },
                  "patch": {
                    "maxPatchBytes": 2097152,
                    "maxFiles": 48,
                    "maxHunks": 256,
                    "maxLinesPerHunk": 4000,
                    "fuzzyMatchWindowLines": 40,
                    "maxResultItems": 96
                  },
                  "workspaceCheck": {
                    "enabled": true,
                    "timeoutSeconds": 420,
                    "maxCustomProfiles": 12,
                    "maxFixedArgumentsPerProfile": 16,
                    "maxArgumentTokenChars": 128,
                    "maxOptionsPerProfile": 8,
                    "maxAllowedValuesPerOption": 6,
                    "maxDiagnostics": 300,
                    "maxOutputBytes": 524288,
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

        CodingToolsSettings codingTools =
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value.CodingTools;

        Assert.Equal(2048, codingTools.Search.MaxPatternChars);
        Assert.Equal(8_388_608, codingTools.Search.MaxBytes);
        Assert.Equal(2_097_152, codingTools.Patch.MaxPatchBytes);
        Assert.Equal(40, codingTools.Patch.FuzzyMatchWindowLines);
        Assert.True(codingTools.WorkspaceCheck.Enabled);
        Assert.Equal(420, codingTools.WorkspaceCheck.TimeoutSeconds);
        Assert.Equal("/opt/dotnet/dotnet", codingTools.WorkspaceCheck.ExecutableCatalog.DotNet.Path);

        WorkspaceCheckProfileSettings profile =
            Assert.Single(codingTools.WorkspaceCheck.CustomProfiles).Value;

        Assert.Equal("dotnet", profile.ExecutableId);
        Assert.Equal(WorkspaceCheckKind.Build, profile.Kind);
        Assert.Equal(WorkspaceCheckDiagnosticParserKind.MsBuild, profile.Parser);
        Assert.Equal(["build"], profile.FixedArguments);
        Assert.Equal(
            ["--configuration", "Release"],
            profile.Options["configuration"].AllowedValues["release"]);
    }

    [Theory]
    [InlineData(typeof(CodingToolsSettings))]
    [InlineData(typeof(WorkspaceSearchSettings))]
    [InlineData(typeof(WorkspacePatchSettings))]
    [InlineData(typeof(WorkspaceCheckSettings))]
    [InlineData(typeof(WorkspaceCheckExecutableCatalogSettings))]
    [InlineData(typeof(WorkspaceCheckProfileSettings))]
    [InlineData(typeof(WorkspaceCheckProfileOptionSettings))]
    [InlineData(typeof(WorkspaceCheckKind))]
    [InlineData(typeof(WorkspaceCheckDiagnosticParserKind))]
    public void ConfigurationJsonContext_registers_coding_tool_contract(Type type)
    {
        Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(type));
    }

}
