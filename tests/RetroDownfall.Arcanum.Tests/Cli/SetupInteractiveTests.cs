using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #19 — aborting at every step must leave no configuration, context, registry, or secret
/// change, and a completed interactive run must produce the same committed result as the
/// non-interactive form.
/// </summary>
[Collection("GlobalConsole")]
public sealed class SetupInteractiveTests : IDisposable
{

    private const string ProviderSecret = "sk-interactive-provider-secret";

    private readonly string _workspaceRoot = CreateDirectory();

    public void Dispose()
    {

        try
        {

            if (Directory.Exists(_workspaceRoot))
            {

                Directory.Delete(_workspaceRoot, recursive: true);

            }

        }
        catch (IOException)
        {

            // Best-effort cleanup.

        }

    }

    /// <summary>
    /// Ends input at answer <paramref name="stopAfter"/>, which is exactly what Ctrl+D does and what
    /// Ctrl+C is mapped to. Every prefix of a complete run is exercised.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Aborting_at_any_step_changes_nothing(int stopAfter)
    {

        SetupWorld world = NewWorld();

        ScriptedPrompt prompt = new(CompleteScript(_workspaceRoot), stopAfter);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world, prompt),
            ["setup"]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

        Assert.Equal(0, world.PresetApplies);

        Assert.Empty(world.ProviderCredentials);

        Assert.Null(world.WebResearchCredential);

        Assert.Null(world.SavedContext);

        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Declining_the_final_plan_changes_nothing()
    {

        SetupWorld world = NewWorld();

        string[] script = [.. CompleteScript(_workspaceRoot)];

        script[^1] = "n";

        ScriptedPrompt prompt = new(script, script.Length);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world, prompt),
            ["setup"]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

        Assert.Empty(world.ProviderCredentials);

    }

    [Fact]
    public async Task A_complete_interactive_run_commits_everything_exactly_once()
    {

        SetupWorld world = NewWorld();

        string[] script = [.. CompleteScript(_workspaceRoot)];

        ScriptedPrompt prompt = new(script, script.Length);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world, prompt),
            ["setup"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, world.ConfigurationWrites);

        Assert.Equal(1, world.PresetApplies);

        Assert.Equal(ProviderSecret, world.ProviderCredentials["alpha"]);

        Assert.Equal(_workspaceRoot, world.WrittenSettings?.Workspaces.DefaultRoot);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// Interactive prompts must never reach stdout under <c>--json</c>: Spectre's rich prompts write
    /// there, so the wizard falls back to the stderr/stdin path and stdout stays exactly one
    /// document.
    /// </summary>
    [Fact]
    public async Task An_interactive_json_run_still_emits_exactly_one_document()
    {

        SetupWorld world = NewWorld();

        ServiceCollection services = CreateServices(world);

        services.AddSingleton<ISetupPrompt, ConsoleSetupPrompt>();

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["setup", "--json"],
            string.Join(
                global::System.Environment.NewLine,
                CompleteScript(_workspaceRoot))
                + global::System.Environment.NewLine);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(document.RootElement.GetProperty("committed").GetBoolean());

        Assert.Equal(1, world.ConfigurationWrites);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_failed_provider_probe_can_be_answered_by_going_back_a_step()
    {

        SetupWorld world = NewWorld() with
        {

            Connectivity = new SetupConnectivityResult(
                SetupConnectivityStatus.Unreachable,
                7,
                0,
                false,
                "The endpoint could not be contacted."),

        };

        // Answer "no" to "continue anyway", which returns to the credential step, then end input.
        string[] script =
        [
            "local",
            "y",
            "custom",
            "alpha",
            "https://provider.test/v1",
            "gpt-test",
            "value",
            ProviderSecret,
            "n",
            "n",
        ];

        ScriptedPrompt prompt = new(script, script.Length);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world, prompt),
            ["setup"]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

        Assert.Empty(world.ProviderCredentials);

    }

    private static string[] CompleteScript(string workspaceRoot) =>
        [
            "local",                        // 1. edition
            "y",                            // 1. loopback only
            "custom",                       // 2. provider template
            "alpha",                        // 2. provider name
            "https://provider.test/v1",     // 2. endpoint
            "gpt-test",                     // 2. model
            "value",                        // 3. credential source
            ProviderSecret,                 // 3. credential value
            "n",                            // 4. web research
            workspaceRoot,                  // 6. workspace root
            string.Empty,                   // 6. campaign
            "general-assistant",            // 7. preset
            "y",                            // 8. accept the plan
        ];

    private static string CreateDirectory()
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-setup-interactive-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(path);

        return path;

    }

    private SetupWorld NewWorld() =>
        new() { OriginalWorkspaceRoot = _workspaceRoot };

    private static ServiceCollection CreateServices(SetupWorld world, ISetupPrompt? prompt = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IConfigurationCommandService>(world);

        services.AddSingleton<IConfigurationPresetService>(world);

        services.AddSingleton<IConfigurationPresetPersistence>(world);

        services.AddSingleton<IProviderCredentialStore>(world);

        services.AddSingleton<IWebResearchCredentialStore>(world);

        services.AddSingleton<ICliContextStore>(world);

        services.AddSingleton<ISetupProviderProbe>(world);

        if (prompt is not null)
        {

            services.AddSingleton(prompt);

        }

        return services;

    }

    /// <summary>
    /// Answers questions from a fixed script and then reports end-of-input, so the "operator ended
    /// the run" path is deterministic instead of depending on the test host's stdin.
    /// </summary>
    private sealed class ScriptedPrompt(IReadOnlyList<string> answers, int available) : ISetupPrompt
    {

        private int _index;

        public bool IsInteractive => true;

        public void Write(string line)
        {

        }

        public Task<string?> AskAsync(
            string question,
            string? defaultValue,
            CancellationToken cancellationToken) =>
            Task.FromResult(Next());

        public Task<bool?> ConfirmAsync(
            string question,
            bool defaultValue,
            CancellationToken cancellationToken)
        {

            string? answer = Next();

            return Task.FromResult<bool?>(
                answer is null
                    ? null
                    : answer.StartsWith('y') || answer.StartsWith('Y'));

        }

        public Task<string?> SelectAsync(
            string question,
            IReadOnlyList<SetupChoice> choices,
            string? defaultId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Next());

        public Task<string?> AskSecretAsync(
            string question,
            CancellationToken cancellationToken) =>
            Task.FromResult(Next());

        private string? Next() =>
            _index >= available || _index >= answers.Count
                ? null
                : answers[_index++];

    }

    private sealed record SetupWorld :
        IConfigurationCommandService,
        IConfigurationPresetService,
        IConfigurationPresetPersistence,
        IProviderCredentialStore,
        IWebResearchCredentialStore,
        ICliContextStore,
        ISetupProviderProbe
    {

        public required string OriginalWorkspaceRoot { get; init; }

        private ArcanumSettings? _originalSettings;

        public ArcanumSettings OriginalSettings =>
            _originalSettings ??= new ArcanumSettings
            {

                Workspaces = new WorkspaceSettings { DefaultRoot = OriginalWorkspaceRoot },

            };

        public Dictionary<string, string> ProviderCredentials { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string? WebResearchCredential { get; private set; }

        public int ConfigurationWrites { get; private set; }

        public int PresetApplies { get; private set; }

        public ArcanumSettings? WrittenSettings { get; private set; }

        public CliContextDocument? SavedContext { get; private set; }

        public SetupConnectivityResult Connectivity { get; init; } =
            new(SetupConnectivityStatus.Reachable, 8, 1, true, "reachable");

        public string ConfigurationPath => "/tmp/arcanum-test/arcanum.json";

        public string FilePath => "/tmp/arcanum-test/cli-context.json";

        public Task<Result<ConfigurationCommandSnapshot>> ReadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<ConfigurationCommandSnapshot>.Success(
                    new ConfigurationCommandSnapshot(
                        OriginalSettings,
                        ConfigurationAccessMode.LocalBootstrap,
                        [])));

        public Task<Result> ValidateAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result> WriteAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings settings,
            CancellationToken cancellationToken)
        {

            ConfigurationWrites++;

            WrittenSettings = settings;

            return Task.FromResult(Result.Success());

        }

        public IReadOnlyList<ConfigurationPresetDefinition> List() =>
            ConfigurationPresetCatalog.All;

        public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() =>
            ConfigurationPresetCatalog.Glossary;

        public ConfigurationPresetDefinition? Find(string idOrName) =>
            ConfigurationPresetCatalog.Find(idOrName);

        public Task<Result<ConfigurationPresetPlan>> DiffAsync(
            string idOrName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The wizard plans against the candidate configuration.");

        public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            PresetApplies++;

            ConfigurationPresetDefinition preset =
                ConfigurationPresetCatalog.Find(idOrName)
                ?? ConfigurationPresetCatalog.All[0];

            ConfigurationPresetCompletionSummary summary = new(
                preset.DisplayName,
                "Provider: alpha; Model: gpt-test",
                "Workspace: none; Campaign: none",
                [],
                "Ward enabled",
                "loopback host binding",
                "arcanum run \"Hello\"");

            ConfigurationPresetPlan plan = new(
                preset,
                ConfigurationPresetEffectiveState.Active,
                [],
                [],
                "hash",
                IsApplicable: true,
                IsIdempotent: false,
                summary);

            return Task.FromResult(
                Result<ConfigurationPresetApplyResult>.Success(
                    new ConfigurationPresetApplyResult(
                        plan,
                        new ConfigurationPresetInspection(
                            ConfigurationPresetEffectiveState.Active,
                            preset,
                            preset.Version,
                            DateTimeOffset.UnixEpoch,
                            "hash",
                            [],
                            summary),
                        Applied: true,
                        AlreadyApplied: false,
                        ConfigurationPresetRollbackStatus.NotRequired)));

        }

        public Task<Result<ConfigurationPresetResetResult>> ResetAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The wizard never resets a preset.");

        public Task<Result<ConfigurationPresetInspection>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The wizard inspects through the persistence snapshot.");

        Task<Result<ConfigurationPresetSnapshot>> IConfigurationPresetPersistence.ReadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<ConfigurationPresetSnapshot>.Success(
                    new ConfigurationPresetSnapshot(
                        OriginalSettings,
                        ConfigurationEnvironmentResolver.Resolve(OriginalSettings),
                        null)));

        Task<Result<ConfigurationPresetCommitResult>> IConfigurationPresetPersistence.ApplyAsync(
            ConfigurationPresetCommitRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The wizard commits through IConfigurationPresetService.");

        Task<Result<ConfigurationPresetResetCommitResult>> IConfigurationPresetPersistence.ResetAsync(
            ConfigurationPresetResetCommitRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The wizard never resets a preset.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                ProviderCredentials.TryGetValue(providerName, out string? value)
                    ? SecretStoreReadResult.Ok(value)
                    : SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(
            string providerName,
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            ProviderCredentials[providerName] = apiKey;

            return Task.CompletedTask;

        }

        public Task DeleteApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            _ = ProviderCredentials.Remove(providerName);

            return Task.CompletedTask;

        }

        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                WebResearchCredential is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(WebResearchCredential));

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default)
        {

            WebResearchCredential = apiKey;

            return Task.CompletedTask;

        }

        public Task DeletePerplexityApiKeyAsync(CancellationToken cancellationToken = default)
        {

            WebResearchCredential = null;

            return Task.CompletedTask;

        }

        public CliContextDocument Load() => SavedContext ?? CliContextDocument.Empty;

        public void Save(CliContextDocument document) => SavedContext = document;

        public Task<SetupConnectivityResult> ProbeAsync(
            string? endpoint,
            string? model,
            string? apiKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(Connectivity);

    }

}
