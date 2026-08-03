using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Compendium.Ux;

using Xunit;

namespace RetroDownfall.Compendium.Tests.Compendium;

[Collection("ProcessEnvironment")]

public sealed class ServiceCollectionConfiguratorTests
{

    [Fact]

    public void Production_composition_uses_the_shared_configuration_preset_service()
    {

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            Guid.NewGuid().ToString("N"));

        string? originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        string? originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        try
        {

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);

            using ServiceProvider provider = ServiceCollectionConfigurator.Build();

            IConfigurationPresetService service =
                provider.GetRequiredService<IConfigurationPresetService>();

            Assert.Equal(6, service.List().Count);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                originalDotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                originalAspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                originalTestHome);

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

        }

    }

}
