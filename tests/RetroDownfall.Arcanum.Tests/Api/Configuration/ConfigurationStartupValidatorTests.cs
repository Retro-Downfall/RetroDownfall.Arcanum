using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api.Configuration;

public sealed class ConfigurationStartupValidatorTests
{

    private static ConfigurationStartupValidator CreateFilter(
        ArcanumSettings settings,
        IConfiguration? configuration = null) =>
        new(
            Options.Create(settings),
            configuration ?? new ConfigurationBuilder().Build(),
            new ConfigurationValidator(),
            NullLogger<ConfigurationStartupValidator>.Instance);

    [Fact]
    public void Configure_InvalidSettings_AbortsStartup()
    {

        ArcanumSettings settings = new()
        {

            DefaultModel = "missing-model",

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        ConfigurationStartupValidator filter = CreateFilter(settings);

        Assert.Throws<ConfigurationValidationException>(() => filter.Configure(static _ => { }));

    }

    [Fact]
    public void Configure_ValidSettings_ReturnsNextUnchanged()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.OpenAICompatible, Models = ["llama3"] },

            ],

        };

        ConfigurationStartupValidator filter = CreateFilter(settings);

        Action<IApplicationBuilder> next = static _ => { };

        Action<IApplicationBuilder> result = filter.Configure(next);

        Assert.Same(next, result);

    }

    [Fact]
    public void Configure_ObsoleteLlamaCppSection_AbortsStartup()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost:11434/v1",
                    Models = ["llama3"],
                },

            ],

        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:LlamaCpp:ServerExecutablePath"] = "/tmp/llama",
            })
            .Build();

        ConfigurationStartupValidator filter = CreateFilter(settings, configuration);

        ConfigurationValidationException ex =
            Assert.Throws<ConfigurationValidationException>(() => filter.Configure(static _ => { }));

        Assert.Equal("Configuration.ValidationFailed", ex.Error.Code);

        Assert.Contains(ex.Error.Details!, static e => e.Pointer == "llamaCpp");

    }

    [Fact]
    public void Configure_ObsoleteLlamaCppServerType_AbortsStartup()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [

                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost:11434/v1",
                    Models = ["llama3"],
                },

            ],

        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Providers:0:Type"] = "LlamaCppServer",
            })
            .Build();

        ConfigurationStartupValidator filter = CreateFilter(settings, configuration);

        ConfigurationValidationException ex =
            Assert.Throws<ConfigurationValidationException>(() => filter.Configure(static _ => { }));

        Assert.Contains(ex.Error.Details!, static e => e.Pointer.Contains("type", StringComparison.Ordinal));

        Assert.Contains(
            ex.Error.Details!,
            static e => e.Detail.Contains("LlamaCppServer", StringComparison.OrdinalIgnoreCase));

    }

}
