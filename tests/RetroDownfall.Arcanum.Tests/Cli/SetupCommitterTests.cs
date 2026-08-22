using System.Collections.Immutable;

using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #19 — the commit stage owns every irreversible write, so every way it can end early must
/// unwind what it can and report what it could not. These tests drive <see cref="SetupCommitter"/>
/// directly, because the two failure modes they pin — a cancellation raised mid-commit, and a
/// credential the operator asked to clear — cannot be produced by the wizard's prompt script.
/// </summary>
public sealed class SetupCommitterTests
{

    private const string ProviderName = "alpha";

    private const string ProviderSecret = "sk-committer-secret";

    [Fact]
    public async Task Cancellation_during_the_configuration_write_deletes_the_credential_this_run_created()
    {

        using CancellationTokenSource cts = new();

        CommitWorld world = new();

        world.OnConfigurationWrite = () =>
        {

            cts.Cancel();

            throw new OperationCanceledException(cts.Token);

        };

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Store),
            Plan(credentialPresent: false, willReplaceExisting: false),
            cts.Token);

        Assert.False(outcome.Committed);

        Assert.Empty(world.ProviderCredentials);

        Assert.Contains("Deleted the provider credential this run created.", outcome.RolledBack);

        Assert.NotNull(outcome.Failure);

        Assert.Contains("cancel", outcome.Failure, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Cancellation_during_the_preset_apply_restores_the_configuration_it_already_wrote()
    {

        using CancellationTokenSource cts = new();

        CommitWorld world = new();

        world.OnPresetApply = () =>
        {

            cts.Cancel();

            throw new OperationCanceledException(cts.Token);

        };

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Store),
            Plan(credentialPresent: false, willReplaceExisting: false),
            cts.Token);

        Assert.False(outcome.Committed);

        // The compensating write and delete must run on an uncancelled token, or the rollback the
        // cancellation triggered is itself cancelled and the installation stays half-applied.
        Assert.Contains("Restored the previous configuration.", outcome.RolledBack);

        Assert.Empty(world.ProviderCredentials);

        Assert.Contains("cancel", outcome.Failure!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Cancellation_before_anything_is_applied_still_surfaces_as_cancellation()
    {

        using CancellationTokenSource cts = new();

        CommitWorld world = new();

        world.OnConfigurationWrite = () =>
        {

            cts.Cancel();

            throw new OperationCanceledException(cts.Token);

        };

        // Nothing irreversible has run, so the wizard's "no change was made" message and exit 130
        // are accurate and must not be traded for a partial-commit report.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CommitAsync(
                world,
                Draft(SetupCredentialAction.Unchanged),
                Plan(credentialPresent: true, willReplaceExisting: false),
                cts.Token));

        Assert.Equal(0, world.ConfigurationWrites);

    }

    [Fact]
    public async Task Cancellation_that_replaced_a_credential_reports_the_unrestorable_value()
    {

        using CancellationTokenSource cts = new();

        CommitWorld world = new();

        world.ProviderCredentials[ProviderName] = "sk-previous-value";

        world.OnConfigurationWrite = () =>
        {

            cts.Cancel();

            throw new OperationCanceledException(cts.Token);

        };

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Store),
            Plan(credentialPresent: true, willReplaceExisting: true),
            cts.Token);

        Assert.False(outcome.Committed);

        Assert.Contains(
            $"arcanum key provider set {ProviderName}",
            outcome.Recovery!,
            StringComparison.Ordinal);

        Assert.DoesNotContain("sk-previous-value", outcome.Recovery!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_cleared_credential_is_reported_as_unrecoverable_when_the_write_fails()
    {

        CommitWorld world = new()
        {

            ConfigurationWriteFailure = new Error(
                "Configuration.Invalid",
                "The candidate configuration failed validation."),

        };

        world.ProviderCredentials[ProviderName] = "sk-previous-value";

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Clear),
            Plan(credentialPresent: true, willReplaceExisting: false),
            CancellationToken.None);

        Assert.False(outcome.Committed);

        Assert.Empty(world.ProviderCredentials);

        Assert.NotNull(outcome.Recovery);

        Assert.Contains(
            $"arcanum key provider set {ProviderName}",
            outcome.Recovery,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_cleared_credential_is_reported_when_the_restored_configuration_still_names_it()
    {

        CommitWorld world = new()
        {

            PresetFailure = new Error(
                "Preset.PrerequisitesMissing",
                "Preset 'general-assistant' is not applicable."),

        };

        world.ProviderCredentials[ProviderName] = "sk-previous-value";

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Clear),
            Plan(credentialPresent: true, willReplaceExisting: false),
            CancellationToken.None);

        Assert.False(outcome.Committed);

        // "Restored the previous configuration." is only true of the configuration file; the
        // credential that configuration names is gone, so the recovery text must say so.
        Assert.Contains("Restored the previous configuration.", outcome.RolledBack);

        Assert.NotNull(outcome.Recovery);

        Assert.Contains(
            $"arcanum key provider set {ProviderName}",
            outcome.Recovery,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Successful_setup_writes_context_through_the_outer_owned_exclusive_seam()
    {

        CommitWorld world = new();

        SetupCommitOutcome outcome = await CommitAsync(
            world,
            Draft(SetupCredentialAction.Unchanged),
            Plan(credentialPresent: true, willReplaceExisting: false),
            CancellationToken.None);

        Assert.True(outcome.Committed);

        Assert.Equal(0, world.UnprotectedContextSaves);

        Assert.Equal(1, world.ExclusiveContextSaves);

        Assert.Equal("gpt-test", world.SavedContext?.Model);

    }

    private static Task<SetupCommitOutcome> CommitAsync(
        CommitWorld world,
        SetupDraft draft,
        SetupPlan plan,
        CancellationToken cancellationToken)
    {

        SetupCommitter committer = new(
            world,
            world,
            world,
            world,
            world,
            world,
            world);

        return committer.CommitAsync(
            new SetupPlanContext(
                new ConfigurationCommandSnapshot(
                    world.OriginalSettings,
                    ConfigurationAccessMode.LocalBootstrap,
                    []),
                null,
                plan.ProviderCredential,
                plan.WebResearchCredential),
            draft,
            plan,
            cancellationToken);

    }

    private static SetupDraft Draft(SetupCredentialAction providerCredential) =>
        new()
        {

            ProviderName = ProviderName,

            ProviderEndpoint = "https://provider.test/v1",

            Model = "gpt-test",

            ProviderCredential = providerCredential,

            ProviderCredentialValue = providerCredential == SetupCredentialAction.Store
                ? ProviderSecret
                : null,

        };

    private static SetupPlan Plan(bool credentialPresent, bool willReplaceExisting) =>
        new(
            new ArcanumSettings { DefaultModel = "gpt-test" },
            new SetupPlanPayload(
                SetupStep.Review.ToString(),
                IsApplicable: true,
                IsIdempotent: false,
                [],
                [],
                new SetupConnectivityPayload("Reachable", 1, 1, true, "reachable"),
                [],
                [],
                new SetupCompletionSummaryPayload(
                    "general-assistant",
                    "alpha / gpt-test",
                    "Public",
                    "none",
                    [],
                    [],
                    "ward enabled",
                    "loopback",
                    "arcanum run \"Hello\"")),
            new SetupCredentialPresence(credentialPresent, false),
            SetupCredentialPresence.Absent,
            willReplaceExisting,
            WebResearchCredentialWillReplaceExisting: false);

    /// <summary>
    /// One fake for every authority the committer writes through. Every write honours the token it
    /// is handed, so a rollback that forwards an already-cancelled token fails visibly instead of
    /// silently appearing to succeed.
    /// </summary>
    private sealed class CommitWorld :
        IConfigurationCommandService,
        IConfigurationCommandExclusiveWriter,
        IConfigurationPresetService,
        IProviderCredentialStore,
        IWebResearchCredentialStore,
        ICliContextStore,
        ICliContextExclusiveWriter,
        ISetupPlanner
    {

        public ArcanumSettings OriginalSettings { get; } = new();

        public Dictionary<string, string> ProviderCredentials { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string? WebResearchCredential { get; private set; }

        public Error? ConfigurationWriteFailure { get; init; }

        public Error? PresetFailure { get; init; }

        public Action? OnConfigurationWrite { get; set; }

        public Action? OnPresetApply { get; set; }

        public int ConfigurationWrites { get; private set; }

        public string ConfigurationPath => "/tmp/arcanum-test/arcanum.json";

        public string FilePath => "/tmp/arcanum-test/cli-context.json";

        public CliContextDocument? SavedContext { get; private set; }

        public int UnprotectedContextSaves { get; private set; }

        public int ExclusiveContextSaves { get; private set; }

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

            cancellationToken.ThrowIfCancellationRequested();

            OnConfigurationWrite?.Invoke();

            if (ConfigurationWriteFailure is { } failure)
            {

                return Task.FromResult(Result.Failure(failure));

            }

            ConfigurationWrites++;

            return Task.FromResult(Result.Success());

        }

        public Task<Result> WriteUnderExclusiveAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings settings,
            CancellationToken cancellationToken) =>
            WriteAsync(snapshot, settings, cancellationToken);

        public IReadOnlyList<ConfigurationPresetDefinition> List() =>
            ConfigurationPresetCatalog.All;

        public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() =>
            ConfigurationPresetCatalog.Glossary;

        public ConfigurationPresetDefinition? Find(string idOrName) =>
            ConfigurationPresetCatalog.Find(idOrName);

        public Task<Result<ConfigurationPresetPlan>> DiffAsync(
            string idOrName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The committer never diffs a preset.");

        public Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
            string idOrName,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            OnPresetApply?.Invoke();

            if (PresetFailure is { } failure)
            {

                return Task.FromResult(Result<ConfigurationPresetApplyResult>.Failure(failure));

            }

            ConfigurationPresetDefinition preset =
                ConfigurationPresetCatalog.Find(idOrName) ?? ConfigurationPresetCatalog.All[0];

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
            throw new NotSupportedException("The committer never resets a preset.");

        public Task<Result<ConfigurationPresetInspection>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The committer never inspects a preset.");

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

            cancellationToken.ThrowIfCancellationRequested();

            ProviderCredentials[providerName] = apiKey;

            return Task.CompletedTask;

        }

        public Task DeleteApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

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

            cancellationToken.ThrowIfCancellationRequested();

            WebResearchCredential = apiKey;

            return Task.CompletedTask;

        }

        public Task DeletePerplexityApiKeyAsync(CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            WebResearchCredential = null;

            return Task.CompletedTask;

        }

        public CliContextDocument Load() => SavedContext ?? CliContextDocument.Empty;

        public void Save(CliContextDocument document)
        {

            UnprotectedContextSaves++;

            SavedContext = document;

        }

        public void SaveUnderExclusive(CliContextDocument document)
        {

            ExclusiveContextSaves++;

            SavedContext = document;

        }

        Task<Result<SetupPlanContext>> ISetupPlanner.ReadAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The committer test supplies its reviewed context.");

        SetupDraft ISetupPlanner.Seed(SetupPlanContext context) =>
            throw new NotSupportedException("The committer test supplies its draft.");

        Task<SetupCredentialPresence> ISetupPlanner.ProviderCredentialPresenceAsync(
            string? providerName,
            string? credentialEnvironmentVariable,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The committer test supplies its credential plan.");

        Task<SetupPlan> ISetupPlanner.PlanAsync(
            SetupPlanContext context,
            SetupDraft draft,
            SetupConnectivityResult connectivity,
            SetupStep step,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The committer test supplies its plan.");

        Task<Result> ISetupPlanner.RevalidateAsync(
            SetupPlanContext context,
            SetupDraft draft,
            SetupPlan plan,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

    }

}
