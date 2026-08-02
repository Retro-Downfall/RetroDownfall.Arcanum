using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class ConfigurationWriterTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

        Assert.StartsWith(
            Path.GetFullPath(_workspace.Root),
            Path.GetFullPath(ArcanumPaths.GrimoireDirectory),
            StringComparison.Ordinal);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _originalTestHome);

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task WriteAsync_persists_credential_references_without_secret_fields()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    CredentialEnvironmentVariable = "OPENAI_API_KEY",
                },
            ],
            Integrations = new IntegrationSettings
            {
                CommLink = new CommLinkIntegrationSettings
                {
                    WebhookUrlEnvironmentVariable = "ARCANUM_COMMLINK_WEBHOOK_URL",
                },
            },
        };

        Result result = await writer.WriteAsync(settings, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        Assert.True(File.Exists(configPath));

        string json = await File.ReadAllTextAsync(configPath);

        Assert.Contains("openai", json, StringComparison.Ordinal);

        Assert.Contains("OPENAI_API_KEY", json, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_COMMLINK_WEBHOOK_URL", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"apiKey\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"webhookUrl\"", json, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task WriteAsync_emits_indented_json_with_default_values()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "openai",
                    CredentialEnvironmentVariable = "OPENAI_API_KEY",
                },
            ],
        };

        Result result = await writer.WriteAsync(settings, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        string json = await File.ReadAllTextAsync(configPath);

        Assert.Contains('\n', json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement arcanum = document.RootElement.GetProperty("Arcanum");

        foreach (string retained in new[]
                 {
                     "edition",
                     "host",
                     "providers",
                     "defaultModel",
                     "fastModel",
                     "security",
                     "workspaces",
                     "features",
                     "integrations",
                     "execution",
                     "cost",
                     "daemon",
                     "cli",
                 })
        {
            Assert.True(arcanum.TryGetProperty(retained, out _), $"Missing retained root '{retained}'.");
        }

        foreach (string removed in new[]
                 {
                     "server",
                     "intelligence",
                     "codingTools",
                     "resilience",
                     "structuredOutput",
                 })
        {
            Assert.False(arcanum.TryGetProperty(removed, out _), $"Removed root '{removed}' was serialized.");
        }

    }

    [Fact]
    public async Task WriteAsync_reports_failure_when_atomic_replace_aborts()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings original = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "original",
                    CredentialEnvironmentVariable = "ORIGINAL_API_KEY",
                },
            ],
        };

        Assert.True((await writer.WriteAsync(original, CancellationToken.None)).IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        string aliasPath = Path.Combine(_workspace.Root, "arcanum-hard-link.json");

        Assert.True(HardLinkTestSupport.TryCreate(aliasPath, configPath));

        byte[] originalBytes = await File.ReadAllBytesAsync(configPath);

        ArcanumSettings replacement = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "replacement",
                    CredentialEnvironmentVariable = "REPLACEMENT_API_KEY",
                },
            ],
        };

        Result result = await writer.WriteAsync(replacement, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(configPath));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(aliasPath));

    }

    [Fact]

    public async Task UpdateAsync_rejects_unknown_existing_configuration_without_rewriting_it()
    {

        ConfigurationWriter writer = CreateWriter();

        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        const string invalid =
            "{\"Arcanum\":{\"defaultModel\":\"preserve\",\"unknownRetentionBypass\":true}}";

        await File.WriteAllTextAsync(configPath, invalid);

        Result<ArcanumSettings> result = await writer.UpdateAsync(
            new ArcanumSettings(),
            static current => Result<ArcanumSettings>.Success(
                current with { DefaultModel = "changed" }),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(invalid, await File.ReadAllTextAsync(configPath));

    }

    [Fact]

    public async Task UpdateAsync_rejects_oversized_existing_configuration_without_rewriting_it()
    {

        ConfigurationWriter writer = CreateWriter();

        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await using (FileStream stream = new(
                         configPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {

            stream.SetLength(ConfigurationBootstrapper.MaxConfigurationBytes + 1L);

        }

        long originalLength = new FileInfo(configPath).Length;

        Result<ArcanumSettings> result = await writer.UpdateAsync(
            new ArcanumSettings(),
            static current => Result<ArcanumSettings>.Success(
                current with { DefaultModel = "changed" }),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Contains(
            "configuration exceeds",
            result.Error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(originalLength, new FileInfo(configPath).Length);

    }

    private static ConfigurationWriter CreateWriter()
    {
        return new ConfigurationWriter(NullLogger<ConfigurationWriter>.Instance);

    }

}
