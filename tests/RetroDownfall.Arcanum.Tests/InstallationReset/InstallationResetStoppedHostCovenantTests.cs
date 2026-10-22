using System.Security.Cryptography;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using ArcanumMaintenanceLock =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock;

using ArcanumMaintenanceLockAcquisitionResult =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLockAcquisitionResult;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What the shipped composition does with the Covenant family when an ordinary installation reset
/// of the global scope, or of the all scope that includes it, plans and applies under stopped-host
/// authority, with <c>Arcanum:Features:Covenant</c> off and on.
/// </summary>
/// <remarks>
/// Every case composes the command line exactly as the executable does, through
/// <see cref="CliApplicationFactory.ConfigureCliServices"/>, over a real SQLCipher Grimoire, the real
/// maintenance lock, the real active store and the real stopped-host data service. The only
/// substitutions are the ones the CLI harness always makes (an in-memory operating-system credential
/// store and an installed-startup probe), a refused host transport and a no-op daemon mutation.
///
/// <para>The switch is varied because the question is whether it matters. It does not: neither the
/// installation-reset graph nor the Covenant gate the planner leases from reads it, so a family the
/// feature never used is a measured zero, not an absent inventory. What decides the outcome is the
/// installation's own evidence: a settled clean host-tools pair, an intact canonical catalog, the
/// master key, and a state row. The plan measures the family through the real gate, and the apply runs
/// the protected erasure over the process's own composition, under a claim an authenticated reset
/// record made (§10.20.3).</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed partial class InstallationResetStoppedHostCovenantTests : IDisposable
{
    private const string MasterApiKey = "stopped-host-reset-master-api-key";

    private const string NoRegisteredCampaignMessage =
        "No registered Campaign contains the current directory.";

    private readonly GrimoireFixture _fixture;

    private readonly ArcanumTestHomeScope _home = new("arcanum-reset-covenant");

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly StoppedHostResetTestSupport.RecordingPreDataMutation _mutations = new();

    public InstallationResetStoppedHostCovenantTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        _home.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_command_composition_carries_the_Covenant_switch_it_is_given(bool covenantFeature)
    {
        using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        Assert.Equal(
            covenantFeature,
            provider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Features.Covenant);

        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IInstallationResetStoppedHostDataService>());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_stopped_host_plan_for_the_global_scope_carries_a_measured_Covenant_inventory_whatever_the_switch_says(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        Result<DataRetentionPlan> plan = await UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.PlanUnderStoppedHostAuthorityAsync(
                new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
                issuer,
                token));

        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);

        Assert.Equal(DataRetentionOperation.FactoryReset, plan.Value.Request.Operation);

        // Measured, not absent: a family the feature never used is a zero that was counted.
        DataRetentionCovenantInventory inventory = Assert.IsType<DataRetentionCovenantInventory>(
            plan.Value.Covenant);

        Assert.Equal(0, inventory.Rows);

        Assert.Equal(0, inventory.ManagedFiles);

        Assert.Equal(0, inventory.LocalArtifacts);

        Assert.Equal(0, inventory.AffectedSessions);

        Assert.Equal(0, inventory.PossibleDisclosures);

        Assert.Equal(CovenantDisclosureCountKind.Exact, inventory.DisclosureCountKind);
    }

    /// <summary>
    /// A plan may carry a Covenant inventory only where the apply that follows it can run the
    /// protected lifecycle the inventory is the disclosure for.
    /// </summary>
    /// <remarks>
    /// The two halves have to move together. A stopped-host plan that measured the family while the
    /// apply still had no lifecycle would let a fresh reset publish its record, uninstall the daemon,
    /// pass the point of no return and only then be refused: it would end at
    /// <c>Data.RecoveryRequired</c> with the database intact and the host's registration gone, instead
    /// of at a refusal before anything changed. Now that both halves exist, a direct apply that no
    /// authenticated record claimed is refused before anything is touched, which is how the lifecycle
    /// shows it is there and not merely absent.
    /// </remarks>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_stopped_host_plan_carries_a_Covenant_inventory_only_where_the_apply_can_run_the_lifecycle(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        Result<DataRetentionPlan> plan = await UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.PlanUnderStoppedHostAuthorityAsync(
                new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
                issuer,
                token));

        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);

        Result<DataRetentionApplyResult> applied = await UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.ApplyUnderStoppedHostAuthorityAsync(
                new DataRetentionApplyRequest(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    plan.Value.PlanId),
                issuer,
                token));

        bool lifecycleUnavailable = applied.IsFailure
            && applied.Error.Code == ErrorCodes.Covenant.MaintenanceFailed;

        Assert.True(
            (plan.Value.Covenant is null) == lifecycleUnavailable,
            "The stopped-host plan and apply disagree about the Covenant lifecycle: the plan "
                + (plan.Value.Covenant is null ? "carries no inventory" : "carries an inventory")
                + " while the apply "
                + (lifecycleUnavailable ? "has no lifecycle to run." : "can run it."));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_stopped_host_data_service_refuses_a_global_apply_no_reset_record_claimed_and_leaves_Covenant_bearing_data_alone(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStoppedHostDataService data = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStoppedHostDataService>();

        Result<DataRetentionPlan> plan = await UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.PlanUnderStoppedHostAuthorityAsync(
                new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
                issuer,
                token));

        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);

        Result<DataRetentionApplyResult> applied = await UnderStoppedHostAuthorityAsync(
            (issuer, token) => data.ApplyUnderStoppedHostAuthorityAsync(
                new DataRetentionApplyRequest(
                    new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                    plan.Value.PlanId),
                issuer,
                token));

        // An apply that no authenticated record claimed is a typed refusal, never permission to erase
        // only the ordinary data (§10.19.13): the row the label protects, and the label itself, are
        // exactly where they were, and no durable operation was created to hold a half-done erasure.
        Assert.True(applied.IsFailure, "The stopped-host composition erased data without a claim.");

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, applied.Error.Code);

        Assert.Equal(1, await LabeledSagaMemorySeed.CountRowsAsync(_fixture, "saga_memories", "Id", memoryId));

        Assert.Equal(1, await LabeledSagaMemorySeed.CountRowsAsync(_fixture, "artifact_sensitivity", "ArtifactId", memoryId));

        Assert.Equal(0, await CountRowsAsync("LongRunningOperations"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_global_dry_run_shows_the_measured_inventory_and_changes_nothing(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(
            covenantFeature,
            "--json",
            "data",
            "factory-reset",
            "--global",
            "--dry-run");

        AssertDryRunMeasuredInventory(result, before);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_all_dry_run_inside_a_registered_Campaign_shows_the_measured_inventory_and_changes_nothing(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await RegisterCampaignAtCurrentDirectoryAsync();

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(covenantFeature, AllScopeArguments(apply: false));

        AssertDryRunMeasuredInventory(result, before);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_global_apply_runs_the_protected_erasure_and_completes_the_reset(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        _ = await StoppedHostResetTestSupport.SeedOrdinarySessionAsync(_fixture);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        ErasureObservation observation = new(_credentials);

        CliTestResult result = await RunAsync(
            covenantFeature,
            observation.Observe,
            "--json",
            "--yes",
            "data",
            "factory-reset",
            "--global",
            "--apply",
            "--force");

        AssertCompletedThroughTheProtectedLifecycle(result, observation, memoryId);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_all_apply_inside_a_registered_Campaign_runs_the_protected_erasure_and_completes_the_reset(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await RegisterCampaignAtCurrentDirectoryAsync();

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        _ = await StoppedHostResetTestSupport.SeedOrdinarySessionAsync(_fixture);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        ErasureObservation observation = new(_credentials);

        CliTestResult result = await RunAsync(
            covenantFeature,
            observation.Observe,
            AllScopeArguments(apply: true));

        AssertCompletedThroughTheProtectedLifecycle(result, observation, memoryId);
    }

    /// <summary>
    /// <c>--all</c> captures the current Campaign first, so outside a registered Campaign it never
    /// reaches the Covenant question and fails with the workspace-resolution error.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_all_scope_outside_a_registered_Campaign_fails_at_workspace_resolution_before_any_effect(
        bool covenantFeature,
        bool apply)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(covenantFeature, AllScopeArguments(apply));

        AssertRefusedWithoutEffect(result, before, ErrorCodes.Data.InventoryUnavailable, NoRegisteredCampaignMessage);
    }

    private void AssertDryRunMeasuredInventory(
        CliTestResult result,
        Dictionary<string, string> before)
    {
        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.True(result.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement root = document.RootElement;

        Assert.True(root.TryGetProperty("planId", out JsonElement planId), described);

        Assert.False(string.IsNullOrWhiteSpace(planId.GetString()), described);

        JsonElement inventory = root.GetProperty("covenantInventory");

        Assert.Equal(0, inventory.GetProperty("rows").GetInt64());

        Assert.Equal(0, inventory.GetProperty("managedFiles").GetInt64());

        Assert.Equal(0, inventory.GetProperty("localArtifacts").GetInt64());

        Assert.Equal(0, inventory.GetProperty("affectedSessions").GetInt64());

        Assert.Equal(0, inventory.GetProperty("possibleDisclosures").GetInt64());

        // The disclosure is diagnostics, in every mode: under --json a payload write would land ahead
        // of the one document a script parses.
        Assert.Contains(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Output,
            StringComparison.Ordinal);

        AssertUnchanged(before);

        AssertNoSidecars();

        AssertNoActiveResetOrJournal();
    }

    private void AssertCompletedThroughTheProtectedLifecycle(
        CliTestResult result,
        ErasureObservation observation,
        Guid memoryId)
    {
        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.True(result.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal("Completed", document.RootElement.GetProperty("phase").GetString());

        Assert.False(document.RootElement.GetProperty("resumeRequired").GetBoolean(), described);

        // The protected lifecycle ran: the coordinator advanced through its phases, and the managed
        // artifacts were processed while the ordinary data was still there to be deleted afterwards.
        Assert.True(observation.Completed(CovenantResetPhase.CanonicalApplied), described);

        Assert.True(observation.Completed(CovenantResetPhase.ManagedArtifactsProcessed), described);

        Assert.True(observation.Completed(CovenantResetPhase.HandlesClosed), described);

        Assert.Equal(1, observation.SessionsWhenManagedArtifactsFinished);

        Assert.Contains(GrimoireOfflineTransitionTerminalSuffixBoundary.Retired, observation.SuffixBoundaries);

        // What the snapshot taken just before offline cleanup deleted the database found: the labelled
        // memory and its label gone, the ordinary data gone, one completed launch at the owner version,
        // and the nested receipt reported.
        ErasureSnapshot after = Assert.IsType<ErasureSnapshot>(observation.BeforeOfflineCleanup);

        Assert.Equal(0, after.SagaMemories);

        Assert.Equal(0, after.ArtifactSensitivityRows);

        Assert.Equal(0, after.Sessions);

        Assert.Equal(1, after.CompletedFactoryResetOperations);

        Assert.Equal(2, after.FactoryResetCheckpointVersion);

        Assert.Equal(InstallationResetNestedTransitionPhase.Completed, after.ReceiptPhase);

        AssertNoActiveResetOrJournal();

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory));

        Assert.False(Directory.Exists(ArcanumPaths.SecretStoreDirectory));

        Assert.NotEqual(Guid.Empty, memoryId);
    }

    /// <summary>
    /// A stopped-host read lane leaves nothing beside a write-ahead-logged Grimoire: a read-only
    /// connection creates its log and shared-memory sidecars and cannot clean them up, so the lease
    /// that opened it removes what it made.
    /// </summary>
    private static void AssertNoSidecars()
    {
        Assert.False(
            File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-wal"),
            "The write-ahead log was left beside the Grimoire.");

        Assert.False(
            File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-shm"),
            "The shared-memory index was left beside the Grimoire.");
    }

    private void AssertNoActiveResetOrJournal()
    {
        if (Directory.Exists(ArcanumPaths.GrimoireDirectory))
        {
            Assert.Empty(Directory.GetFiles(
                ArcanumPaths.GrimoireDirectory,
                "*.factory-reset.active.json",
                SearchOption.TopDirectoryOnly));
        }
    }

    private static string[] GlobalArguments(bool apply) => apply
        ? ["--json", "--yes", "data", "factory-reset", "--global", "--apply", "--force"]
        : ["--json", "data", "factory-reset", "--global", "--dry-run"];

    private static string[] AllScopeArguments(bool apply) => apply
        ? ["--json", "--yes", "data", "factory-reset", "--all", "--apply", "--force"]
        : ["--json", "data", "factory-reset", "--all", "--dry-run"];

    private async Task RegisterCampaignAtCurrentDirectoryAsync()
    {
        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'reset-covenant', 'reset-covenant', $path, 1, '{}', '2026-10-01T00:00:00.0000000Z', '2026-10-01T00:00:00.0000000Z');
            """;

        _ = command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$path", global::System.Environment.CurrentDirectory);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// A refusal that changed nothing, identified by its code and by its own message.
    /// </summary>
    /// <remarks>
    /// The code alone is shared by several refusals on this path, so a refusal is identified by its
    /// message too: a Grimoire that stopped opening must not be able to satisfy a case about a
    /// different refusal.
    /// </remarks>
    private void AssertRefusedWithoutEffect(
        CliTestResult result,
        Dictionary<string, string> before,
        string expectedCode,
        string expectedMessage)
    {
        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.True(result.ExitCode == (int)CliExitCode.GenericError, described);

        Assert.Contains(expectedCode, result.Output, StringComparison.Ordinal);

        Assert.Contains(expectedMessage, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("planId", result.Output, StringComparison.Ordinal);

        AssertUnchanged(before);

        AssertNoSidecars();

        AssertNoActiveResetOrJournal();
    }

    private async Task InstallPriorGrimoireAsync(bool covenantFeature, bool saveMasterKey = true)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string fixtureCopy = _fixture.CopyDatabase();

        File.Copy(fixtureCopy, ArcanumPaths.GrimoireDatabaseFile, overwrite: true);

        File.Copy(
            fixtureCopy + ".kdf",
            GrimoireKdfSidecarFile.GetSidecarPath(ArcanumPaths.GrimoireDatabaseFile),
            overwrite: true);

        await using ServiceProvider seeding = Compose(covenantFeature).BuildServiceProvider();

        await seeding
            .GetRequiredService<DataProtectionSecretStore>()
            .SaveGrimoireEncryptionSecretAsync(GrimoireFixture.TestGrimoireSecret);

        if (saveMasterKey)
        {
            await seeding
                .GetRequiredService<ISecretStore>()
                .SaveApiKeyAsync(MasterApiKey);
        }
    }

    private Task<CliTestResult> RunAsync(bool covenantFeature, params string[] args) =>
        CliTestHarness.RunAsync(Compose(covenantFeature), args);

    private Task<CliTestResult> RunAsync(
        bool covenantFeature,
        Action<IServiceCollection> configure,
        params string[] args)
    {
        ServiceCollection services = Compose(covenantFeature);

        configure(services);

        return CliTestHarness.RunAsync(services, args);
    }

    private ServiceCollection Compose(bool covenantFeature)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arcanum:Features:Covenant"] = covenantFeature ? "true" : "false",
        });

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        ConfigureStoppedHostTransport(services);

        services.RemoveAll<IOsCredentialStore>();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        services.RemoveAll<IInstallationResetPreDataMutation>();

        services.AddSingleton<IInstallationResetPreDataMutation>(_mutations);

        StoppedHostResetTestSupport.UseTheProductionActiveResetRead(services, _credentials);

        return services;
    }

    private static async Task<Result<T>> UnderStoppedHostAuthorityAsync<T>(
        Func<IStoppedHostGrimoireAuthorityIssuer, CancellationToken, Task<Result<T>>> operation)
    {
        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        IStoppedHostGrimoireAuthorityIssuer issuer = new StoppedHostGrimoireAuthorityIssuer(
            held,
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.GrimoireDatabaseFile);

        return await operation(issuer, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A digest of every file under the profile that is installation state, which is to say every file
    /// but the maintenance lock a refused command legitimately creates and releases.
    /// </summary>
    private Dictionary<string, string> CaptureStableFiles() =>
        Directory.GetFiles(_home.Root, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                static path => path,
                static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    /// <summary>
    /// Requires every file under the profile to be exactly as it was, and names the ones that are not.
    /// </summary>
    private void AssertUnchanged(Dictionary<string, string> before)
    {
        Dictionary<string, string> after = CaptureStableFiles();

        string[] added = [.. after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        string[] removed = [.. before.Keys.Except(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        string[] changed =
        [
            .. before.Keys
                .Intersect(after.Keys, StringComparer.Ordinal)
                .Where(path => before[path] != after[path])
                .Order(StringComparer.Ordinal),
        ];

        string Describe(string[] paths) =>
            string.Join(", ", paths.Select(path => Path.GetRelativePath(_home.Root, path)));

        Assert.True(
            added.Length == 0 && removed.Length == 0 && changed.Length == 0,
            $"The installation changed. Added: [{Describe(added)}]. Removed: [{Describe(removed)}]. Changed: [{Describe(changed)}].");
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            global::System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What the protected erasure did, observed from the seams it already exposes.</summary>
    private sealed class ErasureObservation(IOsCredentialStore credentials)
    {
        private readonly object _sync = new();

        public List<(CovenantErasureFaultBoundary Boundary, CovenantResetPhase Phase)> Events { get; } = [];

        public List<GrimoireOfflineTransitionTerminalSuffixBoundary> SuffixBoundaries { get; } = [];

        public long? SessionsWhenManagedArtifactsFinished { get; private set; }

        public ErasureSnapshot? BeforeOfflineCleanup { get; private set; }

        public bool Completed(CovenantResetPhase phase)
        {
            lock (_sync)
            {
                return Events.Contains((CovenantErasureFaultBoundary.AfterPhaseComplete, phase));
            }
        }

        public void Observe(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            StoppedHostResetTestSupport.UseFaultSeams(
                services,
                async (boundary, phase, _) =>
                {
                    lock (_sync)
                    {
                        Events.Add((boundary, phase));
                    }

                    if (boundary is CovenantErasureFaultBoundary.AfterPhaseComplete
                        && phase is CovenantResetPhase.ManagedArtifactsProcessed)
                    {
                        SessionsWhenManagedArtifactsFinished = await StoppedHostResetTestSupport
                            .ScalarAsync("SELECT COUNT(*) FROM \"Sessions\";")
                            .ConfigureAwait(false);
                    }

                    return Result.Success();
                },
                (boundary, _) =>
                {
                    lock (_sync)
                    {
                        SuffixBoundaries.Add(boundary);
                    }

                    return Task.FromResult(Result.Success());
                });

            StoppedHostResetTestSupport.BeforeOfflineCleanup(
                services,
                async () => BeforeOfflineCleanup ??= await SnapshotAsync().ConfigureAwait(false));
        }

        private async Task<ErasureSnapshot> SnapshotAsync()
        {
            InstallationResetActiveStore store = new(ArcanumPaths.GrimoireDirectory, credentials);

            Result<InstallationResetActiveRecoveryState> inspected = await store
                .InspectAsync(CancellationToken.None)
                .ConfigureAwait(false);

            return new ErasureSnapshot(
                (int)await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM saga_memories;")
                    .ConfigureAwait(false),
                (int)await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM artifact_sensitivity;")
                    .ConfigureAwait(false),
                (int)await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM \"Sessions\";")
                    .ConfigureAwait(false),
                (int)await StoppedHostResetTestSupport.ScalarAsync(
                    "SELECT COUNT(*) FROM \"LongRunningOperations\" WHERE \"Kind\" = $kind AND \"State\" = $state;",
                    ("$kind", LongRunningOperationKinds.DataRetentionFactoryReset),
                    ("$state", (int)LongRunningOperationState.Completed)).ConfigureAwait(false),
                (int)await StoppedHostResetTestSupport.ScalarAsync(
                    "SELECT COALESCE(MAX(\"CheckpointVersion\"), 0) FROM \"LongRunningOperations\" WHERE \"Kind\" = $kind;",
                    ("$kind", LongRunningOperationKinds.DataRetentionFactoryReset)).ConfigureAwait(false),
                inspected.IsSuccess
                    ? inspected.Value.Publication?.Payload.NestedTransitionReceipt?.Phase
                    : null);
        }
    }

    private sealed record ErasureSnapshot(
        int SagaMemories,
        int ArtifactSensitivityRows,
        int Sessions,
        int CompletedFactoryResetOperations,
        int FactoryResetCheckpointVersion,
        InstallationResetNestedTransitionPhase? ReceiptPhase);
}
