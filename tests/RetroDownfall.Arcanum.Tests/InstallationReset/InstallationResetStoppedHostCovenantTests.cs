using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

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
/// store and an installed-startup probe) and a no-op daemon mutation.
///
/// <para>The switch is varied because the question is whether it matters. It does not: the
/// installation-reset graph never reads it, and neither does the Covenant gate the serving host
/// leases from. What decides the outcome is that the stopped-host data service is built without a
/// Covenant gate, so it cannot attach the Covenant inventory that a global plan has to carry and
/// cannot run the protected erasure that a global apply has to run (§10.20.3). Both refusals happen
/// before anything changes, which is what these cases pin.</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class InstallationResetStoppedHostCovenantTests : IDisposable
{
    private const string CovenantInventoryUnavailableMessage =
        "The local Covenant inventory is unavailable.";

    private const string NoRegisteredCampaignMessage =
        "No registered Campaign contains the current directory.";

    private readonly GrimoireFixture _fixture;

    private readonly ArcanumTestHomeScope _home = new("arcanum-reset-covenant");

    private readonly InMemoryOsCredentialStore _credentials = new();

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
    public async Task The_stopped_host_plan_for_the_global_scope_carries_no_Covenant_inventory_whatever_the_switch_says(
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

        // The plan is the ordinary factory plan and nothing more: no Covenant family was inventoried,
        // because nothing in this composition can take the read capability that inventories it.
        Assert.Null(plan.Value.Covenant);
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
    /// of at a refusal before anything changed. The plan's missing inventory is today's only guard
    /// against that, so this holds whichever way the gap is eventually closed.
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
    public async Task The_stopped_host_data_service_refuses_the_global_apply_and_leaves_Covenant_bearing_data_alone(
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

        // A missing Covenant lifecycle is a typed refusal, never permission to erase only the ordinary
        // data (§10.19.13): the row the label protects, and the label itself, are exactly where they were.
        Assert.True(applied.IsFailure, "The stopped-host composition erased data without the Covenant lifecycle.");

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        Assert.Equal(1, await LabeledSagaMemorySeed.CountRowsAsync(_fixture, "saga_memories", "Id", memoryId));

        Assert.Equal(1, await LabeledSagaMemorySeed.CountRowsAsync(_fixture, "artifact_sensitivity", "ArtifactId", memoryId));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_global_dry_run_refuses_before_any_effect_whatever_the_switch_says(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(
            covenantFeature,
            "--json",
            "data",
            "factory-reset",
            "--global",
            "--dry-run");

        AssertRefusedWithoutEffect(result, before);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_global_apply_refuses_before_any_effect_whatever_the_switch_says(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(
            covenantFeature,
            "--json",
            "--yes",
            "data",
            "factory-reset",
            "--global",
            "--apply",
            "--force");

        AssertRefusedWithoutEffect(result, before);
    }

    /// <summary>
    /// <c>--all</c> is refused entirely, not just its global half: it captures the current Campaign
    /// first, so outside a registered Campaign it never reaches the Covenant question and fails with
    /// the workspace-resolution error.
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

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(covenantFeature, AllScopeArguments(apply));

        AssertRefusedWithoutEffect(result, before, NoRegisteredCampaignMessage);

        Assert.DoesNotContain(CovenantInventoryUnavailableMessage, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inside a registered Campaign the workspace half resolves, and the same missing Covenant
    /// inventory that refuses <c>--global</c> refuses the whole <c>--all</c> plan before any effect.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_all_scope_inside_a_registered_Campaign_is_refused_for_the_missing_Covenant_inventory_before_any_effect(
        bool covenantFeature,
        bool apply)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        await RegisterCampaignAtCurrentDirectoryAsync();

        Dictionary<string, string> before = CaptureStableFiles();

        CliTestResult result = await RunAsync(covenantFeature, AllScopeArguments(apply));

        AssertRefusedWithoutEffect(result, before, CovenantInventoryUnavailableMessage);
    }

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

    private void AssertRefusedWithoutEffect(CliTestResult result, Dictionary<string, string> before) =>
        AssertRefusedWithoutEffect(result, before, CovenantInventoryUnavailableMessage);

    private void AssertRefusedWithoutEffect(
        CliTestResult result,
        Dictionary<string, string> before,
        string expectedMessage)
    {
        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.True(result.ExitCode == (int)CliExitCode.GenericError, described);

        // The code alone is shared by the refusals on this path (the existing Grimoire cannot be
        // opened without creating or repairing state, the registered Campaign inventory is
        // unavailable, no registered Campaign contains the directory, and the Covenant inventory is
        // missing), so a refusal is identified by its own message: a Grimoire that stopped opening
        // must not be able to satisfy these cases.
        Assert.Contains(ErrorCodes.Data.InventoryUnavailable, result.Output, StringComparison.Ordinal);

        Assert.Contains(expectedMessage, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("planId", result.Output, StringComparison.Ordinal);

        Assert.Equal(before, CaptureStableFiles());

        Assert.Empty(Directory.GetFiles(
            ArcanumPaths.GrimoireDirectory,
            "*.factory-reset.active.json",
            SearchOption.TopDirectoryOnly));
    }

    private async Task InstallPriorGrimoireAsync(bool covenantFeature)
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
    }

    private Task<CliTestResult> RunAsync(bool covenantFeature, params string[] args) =>
        CliTestHarness.RunAsync(Compose(covenantFeature), args);

    private ServiceCollection Compose(bool covenantFeature)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arcanum:Features:Covenant"] = covenantFeature ? "true" : "false",
        });

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IOsCredentialStore>();

        services.AddSingleton<IOsCredentialStore>(_credentials);

        services.RemoveAll<IInstallationResetPreDataMutation>();

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

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
    /// but the ones <see cref="IsLockOrIdleSqliteSidecar"/> names.
    /// </summary>
    private Dictionary<string, string> CaptureStableFiles() =>
        Directory.GetFiles(_home.Root, "*", SearchOption.AllDirectories)
            .Where(static path => !IsLockOrIdleSqliteSidecar(path))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                static path => path,
                static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    /// <summary>
    /// The maintenance lock, which a refused command legitimately creates and releases, and the
    /// write-ahead sidecars SQLite leaves beside a database it was asked to read: the shared-memory
    /// index, which holds no data, and a write-ahead log with no frames in it. A log that holds a
    /// frame, and the database file itself, still count.
    /// </summary>
    private static bool IsLockOrIdleSqliteSidecar(string path) =>
        path.EndsWith(".lock", StringComparison.Ordinal)
            || path.EndsWith("-shm", StringComparison.Ordinal)
            || (path.EndsWith("-wal", StringComparison.Ordinal) && new FileInfo(path).Length == 0);
}
