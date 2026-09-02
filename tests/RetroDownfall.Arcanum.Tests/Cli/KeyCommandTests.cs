using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

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

        FakeWebResearchCredentialStore webResearch = new();

        FakeSecretStore secrets = new();

        providers.Stored["alpha"] = ProviderSecret;

        CliTestResult result = CliTestHarness.Run(
            CreateServices(providers, webResearch, secrets),
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

        Assert.Equal(0, providers.OrdinaryReadCount);

        Assert.True(providers.PeekReadCount > 0);

        Assert.Equal(0, providers.WriteCount);

        Assert.Equal(0, webResearch.OrdinaryReadCount);

        Assert.True(webResearch.PeekReadCount > 0);

        Assert.Equal(0, webResearch.WriteCount);

        Assert.Equal(0, secrets.OrdinaryReadCount);

        Assert.True(secrets.PeekReadCount > 0);

        Assert.Equal(0, secrets.WriteCount);

    }

    [Fact]
    public async Task Show_peeks_the_master_key_without_persisting_or_repairing_it()
    {

        FakeSecretStore secrets = new()
        {

            MasterApiKey = "master-peek-secret",

        };

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(secretStore: secrets),
            ["key", "show"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("master-peek-secret", result.Error, StringComparison.Ordinal);

        Assert.Equal(0, secrets.OrdinaryReadCount);

        Assert.Equal(1, secrets.PeekReadCount);

        Assert.Equal(0, secrets.WriteCount);

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
            ["--yes", "key", "provider", "delete", "alpha"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(providers.Stored.ContainsKey("alpha"));

        Assert.Equal("sk-other", providers.Stored["beta"]);

    }

    /// <summary>An irreversible delete must ask before it acts.</summary>
    [Fact]
    public async Task Provider_delete_requires_confirmation_before_touching_any_store()
    {

        FakeProviderCredentialStore providers = new();

        providers.Stored["alpha"] = ProviderSecret;

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["key", "provider", "delete", "alpha"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

        Assert.True(providers.Stored.ContainsKey("alpha"));

        Assert.Equal(0, providers.WriteCount);

    }

    /// <summary>
    /// ProviderCredentialStore.DeleteApiKeyAsync deletes the encrypted mirror, then throws
    /// InvalidOperationException naming the account that survives in the OS credential store when
    /// the OS delete failed. That message matched no CliFailureMapper arm, so the operator was told
    /// only "An unexpected CLI error occurred." and never learned the keychain entry survived.
    /// </summary>
    [Fact]
    public async Task Provider_delete_surfaces_the_surviving_keychain_account_on_os_store_failure()
    {

        const string SurvivingAccountMessage =
            "The encrypted mirror was deleted, but the OS credential store could not delete the "
            + "credential for provider account arcanum/inference-key-alpha.";

        FakeProviderCredentialStore providers = new()
        {
            DeleteFailure = new InvalidOperationException(SurvivingAccountMessage),
        };

        providers.Stored["alpha"] = ProviderSecret;

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(providers),
            ["--yes", "key", "provider", "delete", "alpha"]);

        Assert.NotEqual((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(SurvivingAccountMessage, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("unexpected CLI error", result.Error, StringComparison.OrdinalIgnoreCase);

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

    [Theory]
    [InlineData("A running host owns the maintenance lock.")]
    [InlineData("The maintenance lock topology is unsafe.")]
    [InlineData("An installation factory reset is active.")]
    public async Task Refused_exclusive_ownership_blocks_every_key_mutation_before_store_effects(
        string refusal)
    {

        foreach (string operation in new[]
                 {

                     "master-set",

                     "provider-set",

                     "provider-delete",

                     "web-set",

                     "web-delete",

                 })
        {

            FakeProviderCredentialStore providers = new();

            FakeWebResearchCredentialStore webResearch = new();

            FakeSecretStore secrets = new();

            RecordingGrimoireCliInitialization initialization = new(refusal);

            (string[] Arguments, string? Input) invocation = operation switch
            {
                "master-set" => (["key", "set"], "master-secret\n"),
                "provider-set" => (["key", "provider", "set", "alpha"], ProviderSecret + "\n"),
                "provider-delete" => (["--yes", "key", "provider", "delete", "alpha"], null),
                "web-set" => (["key", "provider", "set", "perplexity"], "pplx-secret\n"),
                _ => (["--yes", "key", "provider", "delete", "perplexity"], null),
            };

            CliTestResult result = await CliTestHarness.RunAsync(
                CreateServices(providers, webResearch, secrets, initialization),
                invocation.Arguments,
                invocation.Input);

            Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

            // The refusal's own remedy text must reach the operator, not the generic
            // "An unexpected CLI error occurred." that RunMutationAsync used to let every
            // InvalidOperationException — including these initialization-layer lock and reset
            // refusals — flatten into.
            Assert.Contains(refusal, result.Error, StringComparison.Ordinal);

            Assert.DoesNotContain("unexpected CLI error", result.Error, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(1, initialization.ExclusiveCalls);

            Assert.Equal(0, initialization.BootstrapCalls);

            Assert.Equal(0, providers.WriteCount);

            Assert.Equal(0, webResearch.WriteCount);

            Assert.Equal(0, secrets.WriteCount);

        }

    }

    [Fact]
    public async Task Read_only_key_commands_never_request_exclusive_ownership()
    {

        RecordingGrimoireCliInitialization initialization = new(
            "Read-only key commands must not enter the writer boundary.");

        FakeProviderCredentialStore providers = new();

        providers.Stored["alpha"] = ProviderSecret;

        FakeSecretStore secrets = new() { MasterApiKey = "master-secret" };

        _ = await CliTestHarness.RunAsync(
            CreateServices(providers, secretStore: secrets, initialization: initialization),
            ["key", "show"]);

        _ = await CliTestHarness.RunAsync(
            CreateServices(providers, secretStore: secrets, initialization: initialization),
            ["key", "list", "--json"]);

        _ = await CliTestHarness.RunAsync(
            CreateServices(providers, secretStore: secrets, initialization: initialization),
            ["key", "provider", "status", "alpha"]);

        Assert.Equal(0, initialization.ExclusiveCalls);

    }

    [Fact]
    public async Task Key_mutation_keeps_exclusive_ownership_until_the_async_store_write_finishes()
    {

        TaskCompletionSource writeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseWrite = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RecordingGrimoireCliInitialization initialization = new();

        FakeProviderCredentialStore providers = new()
        {

            SaveGate = async () =>
            {

                Assert.True(initialization.IsInsideExclusiveCallback);

                writeEntered.TrySetResult();

                await releaseWrite.Task;

            },

        };

        Task<CliTestResult> run = CliTestHarness.RunAsync(
            CreateServices(providers, initialization: initialization),
            ["key", "provider", "set", "alpha"],
            ProviderSecret + "\n");

        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(initialization.IsInsideExclusiveCallback);

        Assert.False(initialization.CallbackCompleted);

        Assert.False(run.IsCompleted);

        releaseWrite.TrySetResult();

        CliTestResult result = await run;

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(initialization.CallbackCompleted);

        Assert.False(initialization.IsInsideExclusiveCallback);

        Assert.Equal(ProviderSecret, providers.Stored["alpha"]);

    }

    private static ServiceCollection CreateServices(
        FakeProviderCredentialStore? providerStore = null,
        FakeWebResearchCredentialStore? webResearchStore = null,
        FakeSecretStore? secretStore = null,
        IGrimoireCliInitialization? initialization = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            initialization ?? new RecordingGrimoireCliInitialization());

        services.AddSingleton<IProviderCredentialStore>(
            providerStore ?? new FakeProviderCredentialStore());

        services.AddSingleton<IWebResearchCredentialStore>(
            webResearchStore ?? new FakeWebResearchCredentialStore());

        services.AddSingleton<ISecretStore>(secretStore ?? new FakeSecretStore());

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

        public int OrdinaryReadCount { get; private set; }

        public int PeekReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Func<Task>? SaveGate { get; init; }

        /// <summary>
        /// Reproduces ProviderCredentialStore.DeleteApiKeyAsync's OsCredentialStoreStatus.Failed
        /// arm: the encrypted mirror is deleted first (below), then this throws naming the account
        /// that survives in the OS store.
        /// </summary>
        public Exception? DeleteFailure { get; init; }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            OrdinaryReadCount++;

            return Read(providerName);

        }

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            PeekReadCount++;

            return Read(providerName);

        }

        public async Task SaveApiKeyAsync(
            string providerName,
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            WriteCount++;

            if (SaveGate is not null)
            {

                await SaveGate().ConfigureAwait(false);

            }

            Stored[providerName] = apiKey;

        }

        public Task DeleteApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            WriteCount++;

            _ = Stored.Remove(providerName);

            if (DeleteFailure is not null)
            {

                throw DeleteFailure;

            }

            return Task.CompletedTask;

        }

        private Task<SecretStoreReadResult> Read(string providerName) =>
            Task.FromResult(
                Corrupt.Contains(providerName)
                    ? SecretStoreReadResult.Corrupted("corrupt")
                    : Stored.TryGetValue(providerName, out string? value)
                        ? SecretStoreReadResult.Ok(value)
                        : SecretStoreReadResult.Missing());

    }

    private sealed class FakeWebResearchCredentialStore : IWebResearchCredentialStore
    {

        public string? Stored { get; private set; }

        public int OrdinaryReadCount { get; private set; }

        public int PeekReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default)
        {

            OrdinaryReadCount++;

            return Read();

        }

        public Task<SecretStoreReadResult> PeekPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default)
        {

            PeekReadCount++;

            return Read();

        }

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            WriteCount++;

            Stored = apiKey;

            return Task.CompletedTask;

        }

        public Task DeletePerplexityApiKeyAsync(CancellationToken cancellationToken = default)
        {

            WriteCount++;

            Stored = null;

            return Task.CompletedTask;

        }

        private Task<SecretStoreReadResult> Read() =>
            Task.FromResult(
                Stored is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(Stored));

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public string? MasterApiKey { get; init; }

        public int OrdinaryReadCount { get; private set; }

        public int PeekReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public Task<string?> GetApiKeyAsync()
        {

            OrdinaryReadCount++;

            return Task.FromResult(MasterApiKey);

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync()
        {

            OrdinaryReadCount++;

            return MasterRead();

        }

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            PeekReadCount++;

            return MasterRead();

        }

        public Task SaveApiKeyAsync(string apiKey)
        {

            WriteCount++;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
        {

            OrdinaryReadCount++;

            return Task.FromResult(SecretStoreReadResult.Missing());

        }

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
        {

            PeekReadCount++;

            return Task.FromResult(SecretStoreReadResult.Missing());

        }

        private Task<SecretStoreReadResult> MasterRead() =>
            Task.FromResult(
                string.IsNullOrWhiteSpace(MasterApiKey)
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(MasterApiKey));

    }

}
