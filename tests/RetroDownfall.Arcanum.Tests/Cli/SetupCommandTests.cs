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
/// Issue #19 — the guided wizard must keep every edit in an in-memory draft until the final
/// confirmation, must leave the installation untouched on abort or failure, must never expose a
/// secret in argv, stdout, stderr, or JSON, and must produce the same validated configuration shape
/// as <c>arcanum config</c>.
/// </summary>
[Collection("GlobalConsole")]
public sealed class SetupCommandTests : IDisposable
{

    private const string ProviderSecret = "sk-setup-provider-secret";

    private const string ResearchSecret = "pplx-setup-research-secret";

    /// <summary>
    /// The canonical validator rejects a workspace root that does not exist, so the wizard tests use
    /// real directories rather than asserting against a configuration Arcanum would refuse. xUnit
    /// creates one instance per test, so these are per-test directories.
    /// </summary>
    private readonly string _workspaceRoot = CreateDirectory("workspace");

    private readonly string _originalWorkspaceRoot = CreateDirectory("original");

    public void Dispose()
    {

        foreach (string directory in new[] { _workspaceRoot, _originalWorkspaceRoot })
        {

            try
            {

                if (Directory.Exists(directory))
                {

                    Directory.Delete(directory, recursive: true);

                }

            }
            catch (IOException)
            {

                // Best-effort cleanup.

            }

        }

    }

    private static string CreateDirectory(string name)
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-setup-{name}-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(path);

        return path;

    }

    [Fact]
    public void Root_help_lists_the_setup_command()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(NewWorld()), "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("setup", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Setup_help_documents_the_plan_apply_and_stdin_credential_surface()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(NewWorld()),
            "setup",
            "--help");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("--plan", result.Output, StringComparison.Ordinal);

        Assert.Contains("--apply", result.Output, StringComparison.Ordinal);

        Assert.Contains("--provider-key-stdin", result.Output, StringComparison.Ordinal);

        Assert.Contains("--research-key-stdin", result.Output, StringComparison.Ordinal);

        Assert.Contains("--allow-unreachable-provider", result.Output, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--plain")]
    [InlineData("--yes")]
    [InlineData("--no-context")]
    public void Global_flags_remain_recursive_for_setup(string flag)
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(NewWorld()),
            "setup",
            "--help",
            flag);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain(
            "Unrecognized command or argument",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Plan_and_apply_together_are_rejected_before_anything_is_read()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--plan",
            "--apply");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

    }

    [Fact]
    public void Plan_writes_nothing_and_reports_the_precise_diff()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--plan",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test",
            "--preset",
            "general-assistant",
            "--workspace",
            _workspaceRoot);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

        Assert.Equal(0, world.PresetApplies);

        Assert.Empty(world.ProviderCredentials);

        Assert.Contains("workspaces.defaultRoot", result.Output, StringComparison.Ordinal);

        Assert.Contains("Completion summary", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Plan_json_is_one_document_with_the_full_completion_summary()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(NewWorld()),
            "setup",
            "--plan",
            "--json",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test",
            "--preset",
            "general-assistant");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement summary = document.RootElement
            .GetProperty("plan")
            .GetProperty("summary");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(document.RootElement.GetProperty("committed").GetBoolean());

        Assert.Equal("Public", summary.GetProperty("endpointClass").GetString());

        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("activePreset").GetString()));

        Assert.False(
            string.IsNullOrWhiteSpace(summary.GetProperty("providerAndModel").GetString()));

        Assert.False(
            string.IsNullOrWhiteSpace(summary.GetProperty("workspaceAndCampaign").GetString()));

        Assert.False(
            string.IsNullOrWhiteSpace(summary.GetProperty("toolSecurityPosture").GetString()));

        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("privacyState").GetString()));

        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("nextCommand").GetString()));

        Assert.NotEmpty(summary.GetProperty("networkCapabilities").EnumerateArray());

        Assert.NotEmpty(summary.GetProperty("memoryCapabilities").EnumerateArray());

    }

    [Fact]
    public void The_endpoint_is_masked_in_the_diff_because_it_is_a_sensitive_value()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(NewWorld()),
            "setup",
            "--plan",
            "--provider",
            "alpha",
            "--endpoint",
            "https://secret-host.test/v1",
            "--model",
            "gpt-test");

        Assert.DoesNotContain("secret-host.test", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("secret-host.test", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Apply_commits_credentials_configuration_preset_and_context_in_order()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
                "--preset",
                "general-assistant",
                "--provider-key-stdin",
                "--workspace",
                _workspaceRoot,
            ],
            ProviderSecret + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(ProviderSecret, world.ProviderCredentials["alpha"]);

        Assert.Equal(1, world.ConfigurationWrites);

        Assert.Equal(1, world.PresetApplies);

        Assert.Equal(
            ["provider-credential", "configuration", "preset"],
            world.Order);

        Assert.Equal("gpt-test", world.SavedContext?.Model);

        Assert.Equal(
            _workspaceRoot,
            world.WrittenSettings?.Workspaces.DefaultRoot);

        Assert.Equal("gpt-test", world.WrittenSettings?.DefaultModel);

        Assert.Equal(
            "https://provider.test/v1",
            world.WrittenSettings?.Providers.Single().Endpoint);

    }

    [Fact]
    public async Task A_stored_credential_never_reaches_stdout_stderr_or_json()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--json",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
                "--provider-key-stdin",
                "--research-key-stdin",
            ],
            ProviderSecret
                + global::System.Environment.NewLine
                + ResearchSecret
                + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(ProviderSecret, world.ProviderCredentials["alpha"]);

        Assert.Equal(ResearchSecret, world.WebResearchCredential);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ProviderSecret, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(ResearchSecret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(ResearchSecret, result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Validation_reuses_an_already_stored_credential_when_none_is_entered()
    {

        SetupWorld world = NewWorld();

        world.ProviderCredentials["alpha"] = ProviderSecret;

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(ProviderSecret, world.ProbedApiKey);

        Assert.Equal(ProviderSecret, world.ProviderCredentials["alpha"]);

        Assert.DoesNotContain(ProviderSecret, result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void An_unreachable_provider_blocks_the_commit_and_changes_nothing()
    {

        SetupWorld world = NewWorld() with
        {

            Connectivity = new SetupConnectivityResult(
                SetupConnectivityStatus.AuthenticationFailed,
                12,
                0,
                false,
                "The endpoint rejected the credential with HTTP 401."),

        };

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

        Assert.Equal(0, world.PresetApplies);

        Assert.Empty(world.ProviderCredentials);

        Assert.Contains("AuthenticationFailed", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void An_unreachable_provider_can_be_accepted_explicitly()
    {

        SetupWorld world = NewWorld() with
        {

            Connectivity = new SetupConnectivityResult(
                SetupConnectivityStatus.Timeout,
                5000,
                0,
                false,
                "The provider probe timed out after 5 seconds."),

        };

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--allow-unreachable-provider",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, world.ConfigurationWrites);

    }

    [Fact]
    public async Task A_failed_preset_apply_restores_the_configuration_and_deletes_the_new_credential()
    {

        SetupWorld world = NewWorld() with
        {

            PresetFailure = new Error(
                "Preset.PrerequisitesMissing",
                "Preset 'general-assistant' is not applicable."),

        };

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
                "--provider-key-stdin",
            ],
            ProviderSecret + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(world.ProviderCredentials);

        Assert.Equal(2, world.ConfigurationWrites);

        Assert.Equal(
            world.OriginalSettings.Workspaces.DefaultRoot,
            world.WrittenSettings?.Workspaces.DefaultRoot);

        Assert.Contains("rolled back", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(2, world.ExpectedCurrentSettings.Count);

        // The commit declares the pre-wizard snapshot; the rollback must declare the candidate it
        // just wrote, or the canonical writer's optimistic-concurrency check rejects the restore.
        Assert.Same(world.OriginalSettings, world.ExpectedCurrentSettings[0]);

        Assert.NotSame(world.OriginalSettings, world.ExpectedCurrentSettings[1]);

        Assert.Equal(
            "gpt-test",
            world.ExpectedCurrentSettings[1].DefaultModel);

    }

    [Fact]
    public async Task A_failed_configuration_write_deletes_the_new_credential_and_skips_the_preset()
    {

        SetupWorld world = NewWorld() with
        {

            ConfigurationWriteFailure = new Error(
                "Configuration.Invalid",
                "The candidate configuration failed validation."),

        };

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
                "--provider-key-stdin",
            ],
            ProviderSecret + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(world.ProviderCredentials);

        Assert.Equal(0, world.PresetApplies);

        Assert.Null(world.SavedContext);

    }

    [Fact]
    public async Task Replacing_an_existing_credential_reports_an_actionable_partial_commit_state()
    {

        SetupWorld world = NewWorld() with
        {

            PresetFailure = new Error("Preset.PrerequisitesMissing", "not applicable"),

        };

        world.ProviderCredentials["alpha"] = "sk-previous-value";

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateServices(world),
            [
                "setup",
                "--apply",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
                "--provider-key-stdin",
            ],
            ProviderSecret + global::System.Environment.NewLine);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(
            "arcanum key provider set alpha",
            result.Error,
            StringComparison.Ordinal);

        Assert.DoesNotContain("sk-previous-value", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("sk-previous-value", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Re_running_setup_against_a_matching_installation_is_idempotent()
    {

        SetupWorld world = NewWorld();

        world.OriginalSettings.Providers =
        [
            new ProviderSettings
            {

                Name = "alpha",

                Type = AiProviderKind.OpenAICompatible,

                Endpoint = "https://provider.test/v1",

                Models = ["gpt-test"],

            },
        ];

        world.OriginalSettings.DefaultModel = "gpt-test";

        world.PresetIdempotent = true;

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--plan",
            "--json");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement plan = document.RootElement.GetProperty("plan");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.True(plan.GetProperty("isApplicable").GetBoolean());

        Assert.Empty(plan.GetProperty("configurationChanges").EnumerateArray());

    }

    [Fact]
    public void Setup_preserves_configuration_outside_the_wizards_declared_ownership()
    {

        SetupWorld world = NewWorld();

        world.OriginalSettings.Cli.ShowManaBar = false;

        world.OriginalSettings.Retention.AutomaticSweepsEnabled = true;

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(world.WrittenSettings?.Cli.ShowManaBar);

        Assert.True(world.WrittenSettings?.Retention.AutomaticSweepsEnabled);

    }

    [Fact]
    public void A_secret_is_never_accepted_in_an_argument()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(NewWorld()),
            "setup",
            "--plan",
            "--provider-key",
            ProviderSecret);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(
            "Unrecognized command or argument",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void An_invalid_environment_reference_is_rejected_before_anything_is_written()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--provider-key-env",
            "not a variable name");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, world.ConfigurationWrites);

    }

    [Fact]
    public async Task The_research_flag_parses_bare_and_with_an_explicit_value()
    {

        SetupWorld world = NewWorld();

        CliTestResult bare = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--plan",
            "--research",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.Success, bare.ExitCode);

        Assert.DoesNotContain(
            "Unrecognized command or argument",
            bare.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("web-research", bare.Output, StringComparison.Ordinal);

        CliTestResult explicitFalse = await CliTestHarness.RunAsync(
            CreateServices(NewWorld()),
            [
                "setup",
                "--plan",
                "--research",
                "false",
                "--provider",
                "alpha",
                "--endpoint",
                "https://provider.test/v1",
                "--model",
                "gpt-test",
            ]);

        Assert.Equal((int)CliExitCode.Success, explicitFalse.ExitCode);

        Assert.DoesNotContain("web-research", explicitFalse.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// The privacy-posture flag must reach the candidate, and the canonical validator — not the
    /// wizard — decides whether the result is writable. Any-IP binding requires HTTPS, so the plan
    /// reports that blocker instead of writing a configuration Arcanum would refuse to load.
    /// </summary>
    [Fact]
    public void The_privacy_posture_flag_reaches_the_candidate_and_defers_to_canonical_validation()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--listen-any",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.DoesNotContain(
            "Unrecognized command or argument",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("host.listenAny: false -> true", result.Output, StringComparison.Ordinal);

        Assert.Contains("Host.Https.Enabled must be true", result.Output, StringComparison.Ordinal);

        Assert.Equal(0, world.ConfigurationWrites);

    }

    [Fact]
    public void Loopback_posture_is_the_default_and_commits_cleanly()
    {

        SetupWorld world = NewWorld();

        CliTestResult result = CliTestHarness.Run(
            CreateServices(world),
            "setup",
            "--apply",
            "--provider",
            "alpha",
            "--endpoint",
            "https://provider.test/v1",
            "--model",
            "gpt-test");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(world.WrittenSettings?.Host.ListenAny);

    }

    [Fact]
    public void The_draft_never_prints_credential_values()
    {

        SetupDraft draft = new()
        {

            ProviderName = "alpha",

            Model = "gpt-test",

            ProviderCredentialValue = ProviderSecret,

            WebResearchCredentialValue = ResearchSecret,

        };

        string text = draft.ToString();

        Assert.DoesNotContain(ProviderSecret, text, StringComparison.Ordinal);

        Assert.DoesNotContain(ResearchSecret, text, StringComparison.Ordinal);

        Assert.Contains("redacted", text, StringComparison.OrdinalIgnoreCase);

    }

    private SetupWorld NewWorld() =>
        new() { OriginalWorkspaceRoot = _originalWorkspaceRoot };

    private static ServiceCollection CreateServices(SetupWorld world)
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

        return services;

    }

    /// <summary>
    /// One fake standing in for every authority the wizard composes, so the assertions can be about
    /// ordering, rollback, and disclosure rather than about file layout.
    /// </summary>
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

        public List<string> Order { get; } = [];

        public int ConfigurationWrites { get; private set; }

        public int PresetApplies { get; private set; }

        public ArcanumSettings? WrittenSettings { get; private set; }

        public CliContextDocument? SavedContext { get; private set; }

        public SetupConnectivityResult Connectivity { get; init; } =
            new(SetupConnectivityStatus.Reachable, 8, 1, true, "reachable");

        public Error? ConfigurationWriteFailure { get; init; }

        public Error? PresetFailure { get; init; }

        public bool PresetIdempotent { get; set; }

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

        /// <summary>
        /// Records the expected-current snapshot each write declares. The canonical service uses it
        /// for optimistic concurrency, so a rollback that declares the pre-wizard snapshot would be
        /// rejected against the file the commit already replaced.
        /// </summary>
        public List<ArcanumSettings> ExpectedCurrentSettings { get; } = [];

        public Task<Result> WriteAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings settings,
            CancellationToken cancellationToken)
        {

            if (ConfigurationWriteFailure is { } failure)
            {

                return Task.FromResult(Result.Failure(failure));

            }

            ConfigurationWrites++;

            ExpectedCurrentSettings.Add(snapshot.Settings);

            WrittenSettings = settings;

            Order.Add("configuration");

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

            if (PresetFailure is { } failure)
            {

                return Task.FromResult(Result<ConfigurationPresetApplyResult>.Failure(failure));

            }

            PresetApplies++;

            Order.Add("preset");

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
                PresetIdempotent,
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
                        Applied: !PresetIdempotent,
                        AlreadyApplied: PresetIdempotent,
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
                        PresetIdempotent
                            ? new ConfigurationPresetProvenance(
                                "general-assistant",
                                1,
                                DateTimeOffset.UnixEpoch,
                                "hash",
                                ImmutableArray<ConfigurationPresetBaselineValue>.Empty,
                                ImmutableArray<ConfigurationPresetBaselineValue>.Empty)
                            : null)));

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

            Order.Add("provider-credential");

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

            Order.Add("web-research-credential");

            return Task.CompletedTask;

        }

        public Task DeletePerplexityApiKeyAsync(CancellationToken cancellationToken = default)
        {

            WebResearchCredential = null;

            return Task.CompletedTask;

        }

        public CliContextDocument Load() => SavedContext ?? CliContextDocument.Empty;

        public void Save(CliContextDocument document) => SavedContext = document;

        public string? ProbedApiKey { get; private set; }

        public Task<SetupConnectivityResult> ProbeAsync(
            string? endpoint,
            string? model,
            string? apiKey,
            CancellationToken cancellationToken)
        {

            ProbedApiKey = apiKey;

            return Task.FromResult(Connectivity);

        }

    }

}
