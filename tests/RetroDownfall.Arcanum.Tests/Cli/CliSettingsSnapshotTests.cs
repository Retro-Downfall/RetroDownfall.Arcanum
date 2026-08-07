using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The CLI's <see cref="IOptions{ArcanumSettings}"/> snapshot must be parsed by the same
/// System.Text.Json contract the host uses. The configuration binder cannot honour
/// <c>ModelEntryJsonConverter</c> (bare-string model entries vanish) and appends to array defaults
/// instead of replacing them, which silently diverges the CLI's view of arcanum.json from
/// <c>arcanum config show</c> and from the running host.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class CliSettingsSnapshotTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _originalTestHome;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

        await _workspace.DisposeAsync();

    }

    [Fact]
    public void ConfigureCliServices_matches_the_persisted_json_contract()
    {

        string configurationDirectory = Path.Combine(_workspace.Root, ".config", "arcanum");

        Directory.CreateDirectory(configurationDirectory);

        string configurationPath = Path.Combine(configurationDirectory, "arcanum.json");

        File.WriteAllText(
            configurationPath,
            """
            {
              "Arcanum": {
                "security": { "allowedImageMimeTypes": ["image/png"] },
                "providers": [
                  {
                    "name": "OpenAI",
                    "endpoint": "https://api.openai.com/v1",
                    "models": ["gpt-4o-mini", { "name": "gpt-4o", "supportsVision": true }]
                  }
                ]
              }
            }
            """);

        ConfigurationManager configuration = new();

        configuration.AddJsonFile(configurationPath, optional: false, reloadOnChange: false);

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings snapshot = provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.Equal(
            ["image/png"],
            snapshot.Security.AllowedImageMimeTypes);

        ProviderSettings openAi = Assert.Single(snapshot.Providers);

        Assert.Equal(
            ["gpt-4o-mini", "gpt-4o"],
            openAi.Models.Select(model => model.Name).ToArray());

    }

    [Fact]
    public void ConfigureCliServices_degrades_to_defaults_when_the_file_cannot_be_parsed()
    {

        string configurationDirectory = Path.Combine(_workspace.Root, ".config", "arcanum");

        Directory.CreateDirectory(configurationDirectory);

        File.WriteAllText(
            Path.Combine(configurationDirectory, "arcanum.json"),
            """{"Arcanum":{"DefaultModel":"x",}}""");

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        using ServiceProvider provider = services.BuildServiceProvider();

        // Composition must survive so `doctor` and `config validate` can name the offending pointer.
        Assert.NotNull(provider.GetRequiredService<IOptions<ArcanumSettings>>().Value);

    }

}
