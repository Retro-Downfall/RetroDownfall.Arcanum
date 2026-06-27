using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Api.Configuration;

public sealed class ConfigurationStartupValidatorTests
{

    private static ConfigurationStartupValidator CreateFilter(ArcanumSettings settings) =>
        new(
            Options.Create(settings),
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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

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

                new ProviderSettings { Name = "ollama", Type = AiProviderKind.Ollama, Models = ["llama3"] },

            ],

        };

        ConfigurationStartupValidator filter = CreateFilter(settings);

        Action<IApplicationBuilder> next = static _ => { };

        Action<IApplicationBuilder> result = filter.Configure(next);

        Assert.Same(next, result);

    }

}
