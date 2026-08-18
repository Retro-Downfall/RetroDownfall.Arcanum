using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]

public sealed class ConfigurationPresetPersistenceTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

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

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _workspace.Root);

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

        await _workspace.DisposeAsync();

    }

    [Fact]

    public async Task Apply_writes_owner_only_provenance_and_rollback_without_copying_provider_secrets()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetCommitResult> result = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                plan.CandidateSettings,
                provenance,
                ConfigurationPresetHash.ComputeSettings(current)),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(ConfigurationPresetRollbackStatus.SnapshotCreated, result.Value.RollbackStatus);

        ConfigurationPresetSnapshot snapshot = result.Value.Snapshot;

        Assert.True(snapshot.PersistedSettings.Features.Attachments);

        ProviderSettings provider = Assert.Single(snapshot.PersistedSettings.Providers);

        Assert.Equal("http://127.0.0.1:11434/v1", provider.Endpoint);

        Assert.Equal("LOCAL_PROVIDER_KEY", provider.CredentialEnvironmentVariable);

        Assert.True(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

        Assert.True(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

        string stateJson = await File.ReadAllTextAsync(
            ArcanumPaths.ConfigurationPresetStateFile);

        string rollbackJson = await File.ReadAllTextAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile);

        Assert.DoesNotContain("127.0.0.1:11434", stateJson, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("LOCAL_PROVIDER_KEY", stateJson, StringComparison.Ordinal);

        Assert.DoesNotContain("127.0.0.1:11434", rollbackJson, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("LOCAL_PROVIDER_KEY", rollbackJson, StringComparison.Ordinal);

        AssertOwnerOnly(ArcanumPaths.ConfigurationPresetStateFile);

        AssertOwnerOnly(ArcanumPaths.ConfigurationPresetRollbackFile);

    }

    [Fact]

    public async Task Apply_fails_closed_when_journal_permissions_cannot_be_verified()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (path, isDirectory) =>
                isDirectory
                || !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(ArcanumPaths.ConfigurationPresetJournalFile),
                    StringComparison.Ordinal);

        try
        {

            Result<ConfigurationPresetCommitResult> result = await CreatePersistence(writer)
                .ApplyAsync(
                    new ConfigurationPresetCommitRequest(
                        plan.CandidateSettings,
                        Provenance(plan),
                        ConfigurationPresetHash.ComputeSettings(current)),
                    CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Preset.SidecarWriteFailed", result.Error.Code);

            Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

            Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

            Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

            Assert.False(
                ConfigurationBootstrapper.LoadPersistedArcanumSettings()
                    .Features.Attachments);

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

    }

    [Fact]

    public async Task Apply_restores_configuration_and_sidecars_when_finalization_fails()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPersistenceHooks hooks = new()
        {

            ContinueAfterConfigurationWrite = static () => false,

        };

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer, hooks);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        Result<ConfigurationPresetCommitResult> result = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                plan.CandidateSettings,
                Provenance(plan),
                ConfigurationPresetHash.ComputeSettings(current)),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.ApplyFailed.RolledBack", result.Error.Code);

        ArcanumSettings restored = ConfigurationBootstrapper.LoadArcanumSettings();

        Assert.False(restored.Features.Attachments);

        Assert.Equal("local-model", restored.DefaultModel);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Reset_restores_only_unchanged_owned_values_and_preserves_drift_and_unrelated_edits()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings baseline = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model",
            wardEnabled: false);

        Assert.True((await writer.WriteAsync(baseline, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(baseline);

        ArcanumSettings applied = plan.CandidateSettings;

        ConfigurationPresetProvenance provenance = Provenance(plan);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetCommitResult> appliedResult = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                applied,
                provenance,
                ConfigurationPresetHash.ComputeSettings(baseline)),
            CancellationToken.None);

        Assert.True(appliedResult.IsSuccess);

        ArcanumSettings customized = applied with
        {

            Cli = applied.Cli with { ShowManaBar = false },

            Features = applied.Features with { Attachments = false },

        };

        Assert.True((await writer.WriteAsync(customized, CancellationToken.None)).IsSuccess);

        Result<ConfigurationPresetResetCommitResult> reset = await persistence.ResetAsync(
            new ConfigurationPresetResetCommitRequest(
                provenance,
                ConfigurationPresetHash.ComputeSettings(customized)),
            CancellationToken.None);

        Assert.True(reset.IsSuccess, reset.IsFailure ? reset.Error.Message : null);

        Assert.Equal(
            plan.Plan.Preset.OwnedSettings.Length - 1,
            reset.Value.RestoredSettingCount);

        Assert.Equal(1, reset.Value.PreservedDriftCount);

        Assert.False(reset.Value.Snapshot.PersistedSettings.Security.Ward.Enabled);

        Assert.False(reset.Value.Snapshot.PersistedSettings.Features.Attachments);

        Assert.False(reset.Value.Snapshot.PersistedSettings.Cli.ShowManaBar);

        Assert.Null(reset.Value.Snapshot.Provenance);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Apply_rejects_stale_snapshot_without_mutating_configuration()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        Result<ConfigurationPresetCommitResult> result = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                plan.CandidateSettings,
                Provenance(plan),
                "stale-hash"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.ConfigurationChanged", result.Error.Code);

        ArcanumSettings unchanged = ConfigurationBootstrapper.LoadArcanumSettings();

        Assert.False(unchanged.Features.Attachments);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Apply_rejects_unowned_mutations_in_a_supplied_candidate()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        ArcanumSettings forgedCandidate = plan.CandidateSettings with
        {

            Cli = plan.CandidateSettings.Cli with { ShowManaBar = false },

        };

        Result<ConfigurationPresetCommitResult> result = await CreatePersistence(writer)
            .ApplyAsync(
                new ConfigurationPresetCommitRequest(
                    forgedCandidate,
                    Provenance(plan),
                    ConfigurationPresetHash.ComputeSettings(current)),
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.CandidateInvalid", result.Error.Code);

        ArcanumSettings retained = ConfigurationBootstrapper
            .LoadPersistedArcanumSettings();

        Assert.True(retained.Cli.ShowManaBar);

        Assert.False(retained.Features.Attachments);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Prepared_journal_contains_only_owned_values_and_no_unrelated_configuration()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        string? preparedJournal = null;

        ConfigurationPresetPersistenceHooks hooks = new()
        {

            ContinueAfterConfigurationWrite = () =>
            {

                preparedJournal = File.ReadAllText(
                    ArcanumPaths.ConfigurationPresetJournalFile);

                return false;

            },

        };

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer, hooks);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        Result<ConfigurationPresetCommitResult> result = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                plan.CandidateSettings,
                Provenance(plan),
                ConfigurationPresetHash.ComputeSettings(current)),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.NotNull(preparedJournal);

        Assert.DoesNotContain(
            "127.0.0.1:11434",
            preparedJournal,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("LOCAL_PROVIDER_KEY", preparedJournal, StringComparison.Ordinal);

        Assert.DoesNotContain("providers", preparedJournal, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Rollback_preserves_a_concurrent_unrelated_user_edit()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPersistenceHooks hooks = new()
        {

            ContinueAfterConfigurationWrite = () =>
            {

                ArcanumSettings afterPreset =
                    ConfigurationBootstrapper.LoadPersistedArcanumSettings();

                ArcanumSettings userEdit = afterPreset with
                {

                    Cli = afterPreset.Cli with { ShowManaBar = false },

                };

                Result write = writer
                    .WriteAsync(userEdit, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                Assert.True(write.IsSuccess, write.Error.Message);

                return false;

            },

        };

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer, hooks);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        Result<ConfigurationPresetCommitResult> result = await persistence.ApplyAsync(
            new ConfigurationPresetCommitRequest(
                plan.CandidateSettings,
                Provenance(plan),
                ConfigurationPresetHash.ComputeSettings(current)),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        ArcanumSettings restored = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

        Assert.False(restored.Features.Attachments);

        Assert.False(restored.Cli.ShowManaBar);

    }

    [Fact]

    public async Task Cancellation_after_configuration_write_rolls_back_before_propagating()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        using CancellationTokenSource cancellation = new();

        ConfigurationPresetPersistenceHooks hooks = new()
        {

            ContinueAfterConfigurationWrite = () =>
            {

                cancellation.Cancel();

                return false;

            },

        };

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer, hooks);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            persistence.ApplyAsync(
                new ConfigurationPresetCommitRequest(
                    plan.CandidateSettings,
                    Provenance(plan),
                    ConfigurationPresetHash.ComputeSettings(current)),
                cancellation.Token));

        ArcanumSettings restored = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

        Assert.False(restored.Features.Attachments);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Cancellation_surfaces_failed_rollback_and_retains_the_recovery_journal()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        using CancellationTokenSource cancellation = new();

        string aliasPath = Path.Combine(_workspace.Root, "preset-write-hard-link.json");

        ConfigurationPresetPersistenceHooks hooks = new()
        {

            ContinueAfterConfigurationWrite = () =>
            {

                Assert.True(HardLinkTestSupport.TryCreate(
                    aliasPath,
                    ArcanumPaths.ConfigurationFile));

                cancellation.Cancel();

                return false;

            },

        };

        Result<ConfigurationPresetCommitResult> result = await CreatePersistence(writer, hooks)
            .ApplyAsync(
                new ConfigurationPresetCommitRequest(
                    plan.CandidateSettings,
                    Provenance(plan),
                    ConfigurationPresetHash.ComputeSettings(current)),
                cancellation.Token);

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.ApplyFailed.RollbackFailed", result.Error.Code);

        Assert.True(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

        Assert.True(
            ConfigurationBootstrapper
                .LoadPersistedArcanumSettings()
                .Features
                .Attachments);

    }

    [Fact]

    public async Task Read_rejects_provenance_that_claims_paths_outside_the_versioned_preset()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetProvenance forged = new(
            "general-assistant",
            1,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            "forged-hash",
            [new ConfigurationPresetBaselineValue("host.port", "5001")],
            [new ConfigurationPresetBaselineValue("host.port", "6124")]);

        await WriteProvenanceAsync(ArcanumPaths.ConfigurationPresetStateFile, forged);

        await WriteProvenanceAsync(ArcanumPaths.ConfigurationPresetRollbackFile, forged);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetSnapshot> result = await persistence.ReadAsync();

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.RollbackSnapshotInvalid", result.Error.Code);

        Assert.Equal(5001, ConfigurationBootstrapper.LoadPersistedArcanumSettings().Host.Port);

    }

    [Fact]

    public async Task Read_rejects_oversized_sidecars_before_deserialization()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        ConfigurationPresetProvenance provenance = Provenance(
            GeneralAssistantPlan(current));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            provenance,
            ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetProvenance);

        byte[] oversized = new byte[(1024 * 1024) + 1];

        json.CopyTo(oversized, 0);

        Array.Fill(oversized, (byte)' ', json.Length, oversized.Length - json.Length);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllBytesAsync(ArcanumPaths.ConfigurationPresetStateFile, oversized);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile,
            provenance);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetSnapshot> result = await persistence.ReadAsync();

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.SidecarInvalid", result.Error.Code);

    }

    [Fact]

    public async Task Read_rejects_null_required_sidecar_values_without_throwing()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(current, CancellationToken.None)).IsSuccess);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            ArcanumPaths.ConfigurationPresetStateFile,
            """
            {
              "presetId": "general-assistant",
              "version": 1,
              "appliedAt": "2026-08-03T12:00:00Z",
              "ownedValuesHash": null,
              "baselineValues": null,
              "appliedValues": null
            }
            """);

        Result<ConfigurationPresetSnapshot> result = await CreatePersistence(writer)
            .ReadAsync();

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.SidecarInvalid", result.Error.Code);

    }

    [SkippableFact]

    public async Task Read_rejects_a_symbolic_link_sidecar()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "This asserts POSIX behaviour that Windows does not model.");

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        Assert.True((await writer.WriteAsync(
            plan.CandidateSettings,
            CancellationToken.None)).IsSuccess);

        string targetPath = Path.Combine(
            _workspace.Root,
            "preset-state-target.json");

        await WriteProvenanceAsync(targetPath, provenance);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.CreateSymbolicLink(
            ArcanumPaths.ConfigurationPresetStateFile,
            targetPath);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile,
            provenance);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetSnapshot> result = await persistence.ReadAsync();

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.SidecarInvalid", result.Error.Code);

    }

    [Fact]

    public async Task Recovery_restores_only_owned_values_and_preserves_a_manual_edit()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings baseline = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        Assert.True((await writer.WriteAsync(baseline, CancellationToken.None)).IsSuccess);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(baseline);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        ArcanumSettings interrupted = plan.CandidateSettings with
        {

            Cli = plan.CandidateSettings.Cli with { ShowManaBar = false },

        };

        Assert.True((await writer.WriteAsync(interrupted, CancellationToken.None)).IsSuccess);

        ConfigurationPresetJournalDocument journal = new(
            "apply",
            plan.BaselineValues,
            plan.AppliedValues,
            ConfigurationPresetHash.ComputeCanonicalValues(plan.BaselineValues),
            ConfigurationPresetHash.ComputeCanonicalValues(plan.AppliedValues),
            PreviousProvenance: null,
            provenance,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"));

        await WriteJournalAsync(journal);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile,
            provenance);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetSnapshot> result = await persistence.ReadAsync();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(result.Value.PersistedSettings.Features.Attachments);

        Assert.False(result.Value.PersistedSettings.Cli.ShowManaBar);

        Assert.Equal(
            "http://127.0.0.1:11434/v1",
            Assert.Single(result.Value.PersistedSettings.Providers).Endpoint);

        Assert.Null(result.Value.Provenance);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetStateFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Recovery_keeps_a_committed_apply_and_post_commit_owned_drift()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings baseline = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model",
            wardEnabled: false);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(baseline);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        ArcanumSettings postCommitEdit = plan.CandidateSettings with
        {

            Cli = plan.CandidateSettings.Cli with { ShowManaBar = false },

            Features = plan.CandidateSettings.Features with { Attachments = false },

        };

        Assert.True((await writer.WriteAsync(
            postCommitEdit,
            CancellationToken.None)).IsSuccess);

        ConfigurationPresetJournalDocument journal = new(
            "apply",
            plan.BaselineValues,
            plan.AppliedValues,
            ConfigurationPresetHash.ComputeCanonicalValues(plan.BaselineValues),
            ConfigurationPresetHash.ComputeCanonicalValues(plan.AppliedValues),
            PreviousProvenance: null,
            provenance,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"));

        await WriteJournalAsync(journal);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetStateFile,
            provenance);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile,
            provenance);

        Result<ConfigurationPresetSnapshot> result = await CreatePersistence(writer)
            .ReadAsync();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(result.Value.PersistedSettings.Features.Attachments);

        Assert.True(result.Value.PersistedSettings.Security.Ward.Enabled);

        Assert.False(result.Value.PersistedSettings.Cli.ShowManaBar);

        ConfigurationPresetProvenance recovered = Assert.IsType<ConfigurationPresetProvenance>(
            result.Value.Provenance);

        Assert.Equal(provenance.PresetId, recovered.PresetId);

        Assert.Equal(provenance.Version, recovered.Version);

        Assert.Equal(provenance.AppliedAt, recovered.AppliedAt);

        Assert.Equal(provenance.OwnedValuesHash, recovered.OwnedValuesHash);

        Assert.Equal(provenance.BaselineValues.ToArray(), recovered.BaselineValues.ToArray());

        Assert.Equal(provenance.AppliedValues.ToArray(), recovered.AppliedValues.ToArray());

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Recovery_keeps_a_committed_reset_and_post_commit_owned_drift()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings baseline = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model",
            wardEnabled: false);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(baseline);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        ArcanumSettings postCommitEdit = baseline with
        {

            Cli = baseline.Cli with { ShowManaBar = false },

            Features = baseline.Features with { Attachments = true },

        };

        Assert.True((await writer.WriteAsync(
            postCommitEdit,
            CancellationToken.None)).IsSuccess);

        ConfigurationPresetJournalDocument journal = new(
            "reset",
            plan.AppliedValues,
            plan.BaselineValues,
            ConfigurationPresetHash.ComputeCanonicalValues(plan.AppliedValues),
            ConfigurationPresetHash.ComputeCanonicalValues(plan.BaselineValues),
            provenance,
            NextProvenance: null,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"));

        await WriteJournalAsync(journal);

        Result<ConfigurationPresetSnapshot> result = await CreatePersistence(writer)
            .ReadAsync();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.PersistedSettings.Features.Attachments);

        Assert.False(result.Value.PersistedSettings.Security.Ward.Enabled);

        Assert.False(result.Value.PersistedSettings.Cli.ShowManaBar);

        Assert.Null(result.Value.Provenance);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Recovery_completes_a_committed_apply_when_arcanum_json_is_unparseable()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings baseline = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model",
            wardEnabled: false);

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(baseline);

        ConfigurationPresetProvenance provenance = Provenance(plan);

        Assert.True((await writer.WriteAsync(
            plan.CandidateSettings,
            CancellationToken.None)).IsSuccess);

        ConfigurationPresetJournalDocument journal = new(
            "apply",
            plan.BaselineValues,
            plan.AppliedValues,
            ConfigurationPresetHash.ComputeCanonicalValues(plan.BaselineValues),
            ConfigurationPresetHash.ComputeCanonicalValues(plan.AppliedValues),
            PreviousProvenance: null,
            provenance,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"));

        await WriteJournalAsync(journal);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetStateFile,
            provenance);

        await File.WriteAllTextAsync(ArcanumPaths.ConfigurationFile, "{ \"Arcanum\": ");

        Result<ConfigurationPresetSnapshot> result = await CreatePersistence(writer)
            .ReadAsync();

        Assert.True(result.IsFailure);

        Assert.True(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

    }

    [Fact]

    public async Task Read_rejects_state_and_rollback_records_that_differ_only_by_timestamp()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings current = Settings(
            saga: false,
            attachments: false,
            defaultModel: "local-model");

        ConfigurationPresetPlanningResult plan = GeneralAssistantPlan(current);

        ConfigurationPresetProvenance state = Provenance(plan);

        ConfigurationPresetProvenance mismatchedRollback = state with
        {

            AppliedAt = state.AppliedAt.AddSeconds(1),

        };

        Assert.True((await writer.WriteAsync(
            plan.CandidateSettings,
            CancellationToken.None)).IsSuccess);

        await WriteProvenanceAsync(ArcanumPaths.ConfigurationPresetStateFile, state);

        await WriteProvenanceAsync(
            ArcanumPaths.ConfigurationPresetRollbackFile,
            mismatchedRollback);

        FileConfigurationPresetPersistence persistence = CreatePersistence(writer);

        Result<ConfigurationPresetSnapshot> result = await persistence.ReadAsync();

        Assert.True(result.IsFailure);

        Assert.Equal("Preset.RollbackSnapshotInvalid", result.Error.Code);

    }

    [Fact]

    public async Task Configuration_transaction_serializes_callers_and_honors_cancellation()
    {

        TaskCompletionSource acquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = ArcanumConfigurationTransaction.RunAsync(
            async () =>
            {

                acquired.SetResult();

                await release.Task;

                return 1;

            });

        await acquired.Task;

        bool secondEntered = false;

        using CancellationTokenSource cancellation = new();

        Task<int> second = ArcanumConfigurationTransaction.RunAsync(
            () =>
            {

                secondEntered = true;

                return Task.FromResult(2);

            },
            cancellation.Token);

        try
        {

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

            Assert.False(secondEntered);

        }
        finally
        {

            release.TrySetResult();

        }

        Assert.Equal(1, await first);

        int third = await ArcanumConfigurationTransaction.RunAsync(
            () => Task.FromResult(3));

        Assert.Equal(3, third);

    }

    [Theory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task Configuration_transaction_bounds_a_contended_acquisition(bool cancellable)
    {

        using CancellationTokenSource cancellation = new();

        CancellationToken waiting = cancellable
            ? cancellation.Token
            : CancellationToken.None;

        TaskCompletionSource acquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> holder = ArcanumConfigurationTransaction.RunAsync(
            async () =>
            {

                acquired.SetResult();

                await release.Task;

                return 1;

            });

        await acquired.Task;

        try
        {

            Task<int> blocked = ArcanumConfigurationTransaction.RunAsync(
                () => Task.FromResult(2),
                waiting,
                TimeSpan.FromMilliseconds(250));

            Task settled = await Task.WhenAny(
                blocked,
                Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(blocked, settled);

            await Assert.ThrowsAsync<ArcanumConfigurationLockException>(() => blocked);

        }
        finally
        {

            release.TrySetResult();

        }

        Assert.Equal(1, await holder);

    }

    [SkippableFact]

    public void Journal_cleanup_reports_a_denied_delete_instead_of_throwing()
    {

        string directory = Path.Combine(_workspace.Root, "undeletable-journal");

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "arcanum.preset.journal.json");

        File.WriteAllText(path, "{}");

        using FileStream? exclusiveHandle = OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
            : null;

        try
        {

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);

            }

            Skip.IfNot(
                DeletionIsDenied(path),
                "This host still allows the delete, so the cleanup guard cannot be exercised.");

            Assert.False(FileConfigurationPresetPersistence.TryDeleteKnownFile(path));

            Assert.True(File.Exists(path));

        }
        finally
        {

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            }

        }

    }

    private static bool DeletionIsDenied(string path)
    {

        try
        {

            File.Delete(path);

            return false;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return true;

        }

    }

    private static ConfigurationWriter CreateWriter() =>
        new(NullLogger<ConfigurationWriter>.Instance);

    private static async Task WriteJournalAsync(
        ConfigurationPresetJournalDocument journal)
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await using FileStream stream = File.Create(
            ArcanumPaths.ConfigurationPresetJournalFile);

        await JsonSerializer.SerializeAsync(
            stream,
            journal,
            ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetJournalDocument);

    }

    private static async Task WriteProvenanceAsync(
        string path,
        ConfigurationPresetProvenance provenance)
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await using FileStream stream = File.Create(path);

        await JsonSerializer.SerializeAsync(
            stream,
            provenance,
            ConfigurationPresetPersistenceJsonContext.Default.ConfigurationPresetProvenance);

    }

    private static FileConfigurationPresetPersistence CreatePersistence(
        ConfigurationWriter writer,
        ConfigurationPresetPersistenceHooks? hooks = null) =>
        new(
            writer,
            new ConfigurationValidator(),
            NullLogger<FileConfigurationPresetPersistence>.Instance,
            hooks);

    private static ArcanumSettings Settings(
        bool saga,
        bool attachments,
        string defaultModel,
        bool wardEnabled = true) =>
        new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "local",

                    Endpoint = "http://127.0.0.1:11434/v1",

                    CredentialEnvironmentVariable = "LOCAL_PROVIDER_KEY",

                    Models = ["local-model"],

                },
            ],

            DefaultModel = defaultModel,

            Features = new FeatureSettings
            {

                Saga = saga,

                Attachments = attachments,

            },

            Security = new SecuritySettings
            {

                Ward = new WardPolicySettings
                {

                    Enabled = wardEnabled,

                },

            },

        };

    private static ConfigurationPresetPlanningResult GeneralAssistantPlan(
        ArcanumSettings settings)
    {

        ConfigurationPresetDefinition preset =
            ConfigurationPresetCatalog.Find("general-assistant")!;

        ConfigurationPresetSnapshot snapshot = new(
            settings,
            ConfigurationEnvironmentResolver.Resolve(
                settings,
                new Dictionary<string, string?>()),
            Provenance: null);

        Result<ConfigurationPresetPlanningResult> result =
            new ConfigurationPresetPlanner().Plan(preset, snapshot);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.Plan.IsApplicable);

        return result.Value;

    }

    private static ConfigurationPresetProvenance Provenance(
        ConfigurationPresetPlanningResult plan) =>
        new(
            plan.Plan.Preset.Id,
            plan.Plan.Preset.Version,
            DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
            plan.Plan.OwnedValuesHash,
            plan.BaselineValues,
            plan.AppliedValues);

    private static void AssertOwnerOnly(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        UnixFileMode mode = File.GetUnixFileMode(path);

        UnixFileMode disallowed =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;

        Assert.Equal((UnixFileMode)0, mode & disallowed);

    }

}
