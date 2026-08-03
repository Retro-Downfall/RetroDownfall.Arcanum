using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]

public sealed class ConfigurationPresetServiceTests : IAsyncLifetime
{

    private const string TestResearchCredentialEnvironmentVariable =
        "ARCANUM_TEST_PRESET_RESEARCH_API_KEY";

    private TempWorkspace _workspace = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    private string? _originalResearchCredential;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        _originalResearchCredential = global::System.Environment.GetEnvironmentVariable(
            TestResearchCredentialEnvironmentVariable);

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

        global::System.Environment.SetEnvironmentVariable(
            TestResearchCredentialEnvironmentVariable,
            null);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable(
            "DOTNET_ENVIRONMENT",
            _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ASPNETCORE_ENVIRONMENT",
            _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        global::System.Environment.SetEnvironmentVariable(
            TestResearchCredentialEnvironmentVariable,
            _originalResearchCredential);

        await _workspace.DisposeAsync();

    }

    [Fact]

    public void Shared_dependency_injection_registration_resolves_the_production_service()
    {

        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumConfigurationPresets();

        using ServiceProvider provider = services.BuildServiceProvider();

        IConfigurationPresetService service =
            provider.GetRequiredService<IConfigurationPresetService>();

        Assert.IsType<ConfigurationPresetService>(service);

    }

    [Fact]

    public async Task Diff_unknown_preset_returns_actionable_catalog_error()
    {

        FakePresetPersistence persistence = new(ValidSettings());

        ConfigurationPresetService service = CreateService(persistence);

        Result<ConfigurationPresetPlan> result = await service.DiffAsync("missing");

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.NotFound", result.Error.Code);

        Assert.Contains("general-assistant", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Diff_completion_summary_reports_the_configured_workspace_and_no_campaign()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Workspaces.DefaultRoot = "/configured/workspace";

        ConfigurationPresetService service = CreateService(
            new FakePresetPersistence(settings));

        Result<ConfigurationPresetPlan> result = await service.DiffAsync(
            "general-assistant");

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            "Workspace: /configured/workspace; Campaign: Not selected",
            result.Value.CompletionSummary.WorkspaceAndCampaign);

    }

    [Fact]

    public async Task Apply_missing_prerequisite_never_validates_or_mutates_configuration()
    {

        ArcanumSettings settings = ValidSettings();

        settings.Workspaces.DefaultRoot = string.Empty;

        FakePresetPersistence persistence = new(settings);

        FakeCandidateValidator validator = new();

        ConfigurationPresetService service = CreateService(persistence, validator);

        Result<ConfigurationPresetApplyResult> result = await service.ApplyAsync(
            "coding-workspace");

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.PrerequisitesMissing", result.Error.Code);

        Assert.Contains(
            "arcanum config set workspaces.defaultRoot .",
            result.Error.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "No workspace is selected or configured.",
            result.Error.Message,
            StringComparison.Ordinal);

        Assert.Equal(0, validator.Calls);

        Assert.Equal(0, persistence.ApplyCalls);

    }

    [Fact]

    public async Task Apply_invalid_complete_candidate_never_persists_partial_overlay()
    {

        FakePresetPersistence persistence = new(ValidSettings());

        FakeCandidateValidator validator = new(
            Result.Failure(new Error("Configuration.Invalid", "Candidate is invalid.")));

        ConfigurationPresetService service = CreateService(persistence, validator);

        Result<ConfigurationPresetApplyResult> result = await service.ApplyAsync(
            "general-assistant");

        Assert.True(result.IsFailure);

        Assert.Equal("Configuration.Invalid", result.Error.Code);

        Assert.Equal(1, validator.Calls);

        Assert.Equal(0, persistence.ApplyCalls);

    }

    [Fact]

    public async Task Apply_records_versioned_owner_only_provenance_and_reapply_is_idempotent()
    {

        FakePresetPersistence persistence = new(ValidSettings());

        FakeTimeProvider time = new();

        DateTimeOffset appliedAt = DateTimeOffset.Parse("2026-08-03T14:30:00Z");

        time.SetUtcNow(appliedAt);

        ConfigurationPresetService service = CreateService(
            persistence,
            timeProvider: time);

        Result<ConfigurationPresetApplyResult> first = await service.ApplyAsync(
            "general-assistant");

        Result<ConfigurationPresetApplyResult> second = await service.ApplyAsync(
            "general-assistant");

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(first.Value.Applied);

        Assert.False(first.Value.AlreadyApplied);

        Assert.Equal(ConfigurationPresetRollbackStatus.SnapshotCreated, first.Value.RollbackStatus);

        Assert.Equal(ConfigurationPresetEffectiveState.Active, first.Value.Inspection.State);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.False(second.Value.Applied);

        Assert.True(second.Value.AlreadyApplied);

        Assert.Equal(ConfigurationPresetRollbackStatus.NotRequired, second.Value.RollbackStatus);

        Assert.Equal(1, persistence.ApplyCalls);

        ConfigurationPresetProvenance provenance = Assert.IsType<ConfigurationPresetProvenance>(
            persistence.Snapshot.Provenance);

        Assert.Equal("general-assistant", provenance.PresetId);

        Assert.Equal(1, provenance.Version);

        Assert.Equal(appliedAt, provenance.AppliedAt);

        Assert.Equal(
            ConfigurationPresetCatalog.Find("general-assistant")!.OwnedSettings.Length,
            provenance.BaselineValues.Length);

        Assert.Equal(provenance.BaselineValues.Length, provenance.AppliedValues.Length);

        Assert.DoesNotContain(
            provenance.BaselineValues,
            static value => value.Path.StartsWith("providers", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]

    public async Task Research_uses_secure_store_readiness_without_exposing_the_credential()
    {

        const string secret = "credential-must-never-appear";

        FakePresetPersistence persistence = new(ValidSettings());

        FakeCredentialStore credentialStore = new(SecretStoreReadResult.Ok(secret));

        ConfigurationPresetService service = CreateService(
            persistence,
            credentialStore: credentialStore);

        Result<ConfigurationPresetPlan> result = await service.DiffAsync("research");

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.IsApplicable);

        Assert.Equal(1, credentialStore.ReadCalls);

        Assert.DoesNotContain(
            secret,
            string.Join(' ', result.Value.Prerequisites.Select(static status => status.Detail)),
            StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("general-assistant")]

    [InlineData("coding-workspace")]

    [InlineData("private-offline")]

    [InlineData("automation")]

    [InlineData("advanced-custom")]

    public async Task Diff_for_non_research_presets_does_not_probe_the_research_store(
        string presetId)
    {

        ArcanumSettings settings = ValidSettings();

        settings.Workspaces.DefaultRoot = "/work";

        settings.Cost.Budget.Enabled = true;

        settings.Cost.Budget.DailyLimitUsd = 1m;

        FakeCredentialStore credentialStore = new(SecretStoreReadResult.Ok("unused"));

        ConfigurationPresetService service = CreateService(
            new FakePresetPersistence(settings),
            credentialStore: credentialStore);

        Result<ConfigurationPresetPlan> result = await service.DiffAsync(presetId);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, credentialStore.ReadCalls);

    }

    [Fact]

    public async Task Apply_for_non_research_preset_does_not_probe_the_research_store()
    {

        FakeCredentialStore credentialStore = new(SecretStoreReadResult.Ok("unused"));

        ConfigurationPresetService service = CreateService(
            new FakePresetPersistence(ValidSettings()),
            credentialStore: credentialStore);

        Result<ConfigurationPresetApplyResult> result = await service.ApplyAsync(
            "general-assistant");

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, credentialStore.ReadCalls);

    }

    [Fact]

    public async Task Inspect_and_reset_do_not_probe_the_store_after_research_was_applied()
    {

        FakeCredentialStore credentialStore = new(SecretStoreReadResult.Ok("secure-value"));

        ConfigurationPresetService service = CreateService(
            new FakePresetPersistence(ValidSettings()),
            credentialStore: credentialStore);

        Result<ConfigurationPresetApplyResult> applied = await service.ApplyAsync("research");

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, credentialStore.ReadCalls);

        Result<ConfigurationPresetInspection> inspected = await service.InspectAsync();

        Result<ConfigurationPresetResetResult> reset = await service.ResetAsync();

        Assert.True(inspected.IsSuccess, inspected.Error.Message);

        Assert.True(reset.IsSuccess, reset.Error.Message);

        Assert.Equal(1, credentialStore.ReadCalls);

    }

    [Fact]

    public async Task Research_environment_credential_satisfies_readiness_without_store_access()
    {

        global::System.Environment.SetEnvironmentVariable(
            TestResearchCredentialEnvironmentVariable,
            "environment-secret");

        FakeCredentialStore credentialStore = new(SecretStoreReadResult.Ok("store-secret"));

        ConfigurationPresetService service = CreateService(
            new FakePresetPersistence(ValidSettings()),
            credentialStore: credentialStore);

        Result<ConfigurationPresetPlan> result = await service.DiffAsync("research");

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.IsApplicable);

        Assert.Equal(0, credentialStore.ReadCalls);

        Assert.DoesNotContain(
            "environment-secret",
            string.Join(' ', result.Value.Prerequisites.Select(static status => status.Detail)),
            StringComparison.Ordinal);

    }

    [Fact]

    public async Task Reset_without_active_provenance_is_a_successful_no_op()
    {

        FakePresetPersistence persistence = new(ValidSettings());

        ConfigurationPresetService service = CreateService(persistence);

        Result<ConfigurationPresetResetResult> result = await service.ResetAsync();

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.Reset);

        Assert.Equal(0, result.Value.RestoredSettingCount);

        Assert.Equal(0, result.Value.PreservedDriftCount);

        Assert.Equal(ConfigurationPresetRollbackStatus.NotRequired, result.Value.RollbackStatus);

        Assert.Equal(0, persistence.ResetCalls);

    }

    [Fact]

    public async Task Reset_delegates_owned_value_restoration_and_returns_custom_inspection()
    {

        FakePresetPersistence persistence = new(ValidSettings());

        ConfigurationPresetService service = CreateService(persistence);

        Result<ConfigurationPresetApplyResult> applied = await service.ApplyAsync(
            "general-assistant");

        Result<ConfigurationPresetResetResult> reset = await service.ResetAsync();

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.True(reset.IsSuccess, reset.Error.Message);

        Assert.True(reset.Value.Reset);

        Assert.Equal(1, persistence.ResetCalls);

        Assert.Equal(ConfigurationPresetEffectiveState.Custom, reset.Value.Inspection.State);

    }

    private static ConfigurationPresetService CreateService(
        FakePresetPersistence persistence,
        FakeCandidateValidator? validator = null,
        TimeProvider? timeProvider = null,
        IWebResearchCredentialStore? credentialStore = null) =>
        new(
            persistence,
            validator ?? new FakeCandidateValidator(),
            timeProvider ?? TimeProvider.System,
            credentialStore);

    private static ArcanumSettings ValidSettings() =>
        new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "local",

                    Endpoint = "http://127.0.0.1:11434/v1",

                    Models =
                    [
                        new ModelEntry
                        {

                            Name = "test-model",

                        },
                    ],

                },
            ],

            DefaultModel = "test-model",

            Integrations = new IntegrationSettings
            {

                WebResearch = new WebResearchIntegrationSettings
                {

                    CredentialEnvironmentVariable =
                        TestResearchCredentialEnvironmentVariable,

                },

            },

        };

    private sealed class FakeCandidateValidator(Result? result = null)
        : IConfigurationPresetCandidateValidator
    {

        private readonly Result _result = result ?? Result.Success();

        public int Calls { get; private set; }

        public Task<Result> ValidateAsync(
            ArcanumSettings candidate,
            CancellationToken cancellationToken = default)
        {

            Calls++;

            return Task.FromResult(_result);

        }

    }

    private sealed class FakePresetPersistence : IConfigurationPresetPersistence
    {

        public FakePresetPersistence(ArcanumSettings settings)
        {

            Snapshot = CreateSnapshot(settings, null);

        }

        public ConfigurationPresetSnapshot Snapshot { get; private set; }

        public int ApplyCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public Task<Result<ConfigurationPresetSnapshot>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ConfigurationPresetSnapshot>.Success(Snapshot));

        public Task<Result<ConfigurationPresetCommitResult>> ApplyAsync(
            ConfigurationPresetCommitRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyCalls++;

            Snapshot = CreateSnapshot(request.CandidateSettings, request.Provenance);

            return Task.FromResult(Result<ConfigurationPresetCommitResult>.Success(
                new ConfigurationPresetCommitResult(
                    ConfigurationPresetRollbackStatus.SnapshotCreated,
                    Snapshot)));

        }

        public Task<Result<ConfigurationPresetResetCommitResult>> ResetAsync(
            ConfigurationPresetResetCommitRequest request,
            CancellationToken cancellationToken = default)
        {

            ResetCalls++;

            ArcanumSettings reset = ConfigurationPathAccessor.Clone(Snapshot.PersistedSettings);

            foreach (ConfigurationPresetBaselineValue baseline in request.Provenance.BaselineValues)
            {

                ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                    reset,
                    baseline.Path,
                    baseline.CanonicalJson);

                reset = update.Settings!;

            }

            Snapshot = CreateSnapshot(reset, null);

            return Task.FromResult(Result<ConfigurationPresetResetCommitResult>.Success(
                new ConfigurationPresetResetCommitResult(
                    ConfigurationPresetRollbackStatus.SnapshotCreated,
                    Snapshot,
                    request.Provenance.BaselineValues.Length,
                    PreservedDriftCount: 0)));

        }

        private static ConfigurationPresetSnapshot CreateSnapshot(
            ArcanumSettings settings,
            ConfigurationPresetProvenance? provenance) =>
            new(
                settings,
                ConfigurationEnvironmentResolver.Resolve(
                    settings,
                    new Dictionary<string, string?>()),
                provenance);

    }

    private sealed class FakeCredentialStore(SecretStoreReadResult readResult)
        : IWebResearchCredentialStore
    {

        public int ReadCalls { get; private set; }

        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default)
        {

            ReadCalls++;

            return Task.FromResult(readResult);

        }

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeletePerplexityApiKeyAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

}
