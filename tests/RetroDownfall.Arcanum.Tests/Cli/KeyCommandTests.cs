using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #47 — the credential surface must cover every Arcanum-owned credential identity, report
/// presence/status with fixed recovery guidance, and never print a stored credential.
/// </summary>
[Collection("GlobalConsole")]
public sealed class KeyCommandTests
{

    private const string ProviderSecret = "sk-inference-provider-secret";

    [Fact]
    public void Key_help_lists_the_credential_inventory_and_provider_family()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "key", "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("list", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("provider", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Inventory_reports_every_owned_credential_identity_without_values()
    {

        FakeProviderCredentialStore providers = new();

        providers.Stored["alpha"] = ProviderSecret;

        CliTestResult result = CliTestHarness.Run(
            CreateServices(providers),
            "key",
            "list",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement credentials = document.RootElement.GetProperty("credentials");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        string[] kinds = [.. credentials
            .EnumerateArray()
            .Select(static entry => entry.GetProperty("kind").GetString()!)];

        Assert.Contains("master", kinds);

        Assert.Contains("grimoire-encryption", kinds);

        Assert.Contains("file-encryption", kinds);

        Assert.Contains("web-research", kinds);

        Assert.Contains("inference-provider", kinds);

        JsonElement provider = credentials
            .EnumerateArray()
            .Single(static entry =>
                entry.GetProperty("kind").GetString() == "inference-provider");

        Assert.Equal("alpha", provider.GetProperty("displayName").GetString());

        Assert.Equal("configured", provider.GetProperty("status").GetString());

        Assert.Equal("secure-store", provider.GetProperty("source").GetString());

        Assert.Equal(
            "ARCANUM_PROVIDER_ALPHA_API_KEY",
            provider.GetProperty("environmentVariable").GetString());

        Assert.False(string.IsNullOrWhiteSpace(provider.GetProperty("recovery").GetString()));

    }

    [Fact]
    public void Inventory_reports_a_missing_inference_credential_as_missing()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(),
            "key",
            "list",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement provider = document.RootElement
            .GetProperty("credentials")
            .EnumerateArray()
            .Single(static entry =>
                entry.GetProperty("kind").GetString() == "inference-provider");

        Assert.Equal("missing", provider.GetProperty("status").GetString());

        Assert.Equal("none", provider.GetProperty("source").GetString());

    }

    [Fact]
    public void Inventory_reports_a_corrupt_credential_without_disclosing_recovery_material()
    {

        FakeProviderCredentialStore providers = new();

        providers.Corrupt.Add("alpha");

        CliTestResult result = CliTestHarness.Run(
            CreateServices(providers),
            "key",
            "list",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement provider = document.RootElement
            .GetProperty("credentials")
            .EnumerateArray()
            .Single(static entry =>
                entry.GetProperty("kind").GetString() == "inference-provider");

        Assert.Equal("corrupt", provider.GetProperty("status").GetString());

        Assert.Contains(
            "arcanum setup",
            provider.GetProperty("recovery").GetString(),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Provider_set_stores_an_inference_credential_from_redirected_stdin()
    {

        FakeProviderCredentialStore providers = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["key", "provider", "set", "alpha"],
            ProviderSecret + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(ProviderSecret, providers.Stored["alpha"]);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Provider_status_reports_presence_without_the_value()
    {

        FakeProviderCredentialStore providers = new();

        providers.Stored["alpha"] = ProviderSecret;

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["key", "provider", "status", "alpha"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Provider_delete_removes_only_the_named_inference_credential()
    {

        FakeProviderCredentialStore providers = new();

        providers.Stored["alpha"] = ProviderSecret;

        providers.Stored["beta"] = "sk-other";

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["key", "provider", "delete", "alpha"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(providers.Stored.ContainsKey("alpha"));

        Assert.Equal("sk-other", providers.Stored["beta"]);

    }

    [Fact]
    public async Task Perplexity_still_routes_to_the_web_research_credential_by_default()
    {

        FakeProviderCredentialStore providers = new();

        FakeWebResearchCredentialStore webResearch = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers, webResearch),
            ["key", "provider", "set", "perplexity"],
            "pplx-secret" + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("pplx-secret", webResearch.Stored);

        Assert.Empty(providers.Stored);

    }

    [Fact]
    public async Task An_explicit_kind_overrides_the_reserved_perplexity_routing()
    {

        FakeProviderCredentialStore providers = new();

        FakeWebResearchCredentialStore webResearch = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers, webResearch),
            ["key", "provider", "set", "perplexity", "--kind", "inference"],
            "sk-inference" + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("sk-inference", providers.Stored["perplexity"]);

        Assert.Null(webResearch.Stored);

    }

    [Fact]
    public async Task An_empty_credential_is_rejected_without_writing_anything()
    {

        FakeProviderCredentialStore providers = new();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["key", "provider", "set", "alpha"],
            global::System.Environment.NewLine);

        Assert.NotEqual((int)CliExitCode.Success, result.ExitCode);

        Assert.Empty(providers.Stored);

    }

    private static ServiceCollection CreateServices(
        FakeProviderCredentialStore? providerStore = null,
        FakeWebResearchCredentialStore? webResearchStore = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IProviderCredentialStore>(
            providerStore ?? new FakeProviderCredentialStore());

        services.AddSingleton<IWebResearchCredentialStore>(
            webResearchStore ?? new FakeWebResearchCredentialStore());

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.AddSingleton<IOptions<ArcanumSettings>>(
            new OptionsWrapper<ArcanumSettings>(
                new ArcanumSettings
                {

                    Providers =
                    [
                        new ProviderSettings
                        {

                            Name = "alpha",

                            Type = AiProviderKind.OpenAICompatible,

                            Endpoint = "https://example.test/v1",

                            Models = ["gpt-test"],

                        },
                    ],

                }));

        return services;

    }

    private sealed class FakeProviderCredentialStore : IProviderCredentialStore
    {

        public Dictionary<string, string> Stored { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Corrupt { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Corrupt.Contains(providerName)
                    ? SecretStoreReadResult.Corrupted("corrupt")
                    : Stored.TryGetValue(providerName, out string? value)
                        ? SecretStoreReadResult.Ok(value)
                        : SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(
            string providerName,
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            Stored[providerName] = apiKey;

            return Task.CompletedTask;

        }

        public Task DeleteApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            _ = Stored.Remove(providerName);

            return Task.CompletedTask;

        }

    }

    private sealed class FakeWebResearchCredentialStore : IWebResearchCredentialStore
    {

        public string? Stored { get; private set; }

        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Stored is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(Stored));

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            Stored = apiKey;

            return Task.CompletedTask;

        }

        public Task DeletePerplexityApiKeyAsync(CancellationToken cancellationToken = default)
        {

            Stored = null;

            return Task.CompletedTask;

        }

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
