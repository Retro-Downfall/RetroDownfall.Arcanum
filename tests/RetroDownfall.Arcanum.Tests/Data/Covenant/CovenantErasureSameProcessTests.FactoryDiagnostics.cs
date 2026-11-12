using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantErasureSameProcessTests
{
    [SkippableFact]
    public async Task Factory_launch_lease_loss_names_the_actual_operation_and_phase()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CoordinatorPause pause = new();

        RouteStoreFaults faults = new(RouteStoreFault.None) { FactoryCheckpointPause = pause };

        TestCapturingLogger<DataRetentionService> log = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            fastLeaseHeartbeat: true,
            storeFaults: faults,
            serviceOverrides: services => CaptureRetentionDiagnostics(services, log));

        await InitializeGuildIdentityAsync(harness);

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> applying = harness.ApplyFactoryAsync(plan.PlanId);

        Result<DataRetentionApplyResult> result;

        try
        {
            Task paused = pause.WaitUntilPausedAsync();

            if (await Task.WhenAny(paused, applying) == applying)
            {
                Result<DataRetentionApplyResult> early = await applying;

                Assert.Fail(early.IsFailure ? $"Factory apply ended before the checkpoint: {early.Error.Code}: {early.Error.Message}" : "Factory apply unexpectedly completed before the checkpoint.");
            }

            await paused;

            await harness.ReplaceRunningFactoryLeaseOwnerAsync("replacement-diagnostic-owner");

            result = await applying.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            pause.Release();
        }

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        AssertFactoryDiagnostic(log, "LaunchLease", operation.Id, LogLevel.Warning);

        Assert.Equal("replacement-diagnostic-owner", operation.LeaseOwner);

        Assert.Equal(0, faults.MaintenanceFailureTransitionAttempts);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_unexpected_failure_reports_only_the_operation_identity_actually_observed(bool afterLaunch)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.None);

        static Task Fail() => throw new InvalidOperationException("PRIVATE-factory-diagnostic /private/secret/catalog");

        bool started = false;

        bool reloaded = false;

        if (afterLaunch)
        {
            faults.AfterFactoryStarted = _ => { started = true; return Task.CompletedTask; };

            faults.AfterFactoryLaunchReload = () => { reloaded = true; return Fail(); };
        }
        else faults.AfterFactoryStarted = _ => Fail();

        TestCapturingLogger<DataRetentionService> log = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            storeFaults: faults,
            serviceOverrides: services => CaptureRetentionDiagnostics(services, log));

        await InitializeGuildIdentityAsync(harness);

        string beforeGuild = await ReadGuildInventoryAsync(harness);

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(plan.PlanId);

        Assert.True(result.IsFailure);

        if (result.Error.Code == ErrorCodes.Data.PlanChanged)
        {
            DataRetentionPlan later = await harness.PlanFactoryAsync();

            Assert.Fail($"Plan changed before injected fault. Started={started}; Reloaded={reloaded}; "
                + $"preview={plan.PlanId}; later={later.PlanId}; previewItems={string.Join("; ", plan.Items)}; "
                + $"laterItems={string.Join("; ", later.Items)}; previewConflicts={string.Join("; ", plan.Conflicts)}; "
                + $"laterConflicts={string.Join("; ", later.Conflicts)}; beforeGuild={beforeGuild}; "
                + $"afterGuild={await ReadGuildInventoryAsync(harness)}; error={result.Error.Message}");
        }

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        AssertFactoryDiagnostic(log, "UnexpectedFailure", afterLaunch ? operation.Id : null, LogLevel.Error);
    }

    [SkippableFact]
    public async Task Factory_reset_refuses_a_Guild_identity_created_after_its_preview_without_erasing_data()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RouteStoreFaults faults = new(RouteStoreFault.None);

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            storeFaults: faults,
            serviceOverrides: services =>
            {
                ServiceDescriptor scheduler = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationFactory?.Method.ReturnType == typeof(InstallationResetRecoveryAwareHostedService<GuildLocalQuestScheduler>));

                services.Remove(scheduler);
            });

        string before = await ReadGuildInventoryAsync(harness);

        Assert.DoesNotContain("guild_workspace_authority_state=", before);

        string audit = Path.Combine(harness.LogsRoot, "audit-20000101.jsonl");

        await File.WriteAllTextAsync(audit, "retained preview data");

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Assert.DoesNotContain("guild_workspace_authority_state=", await ReadGuildInventoryAsync(harness));

        Guid launchedOperationId = Guid.Empty;

        bool initialized = false;

        faults.AfterFactoryStarted = async _ =>
        {
            launchedOperationId = (await harness.ReadFactoryOperationAsync()).Id;

            await InitializeGuildIdentityAsync(harness);

            initialized = true;
        };

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(plan.PlanId);

        Assert.True(initialized);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.NotEqual(Guid.Empty, launchedOperationId);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        Assert.Equal(launchedOperationId, operation.Id);

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        Assert.Contains("guild_workspace_authority_state=1", await ReadGuildInventoryAsync(harness));

        Assert.Equal("retained preview data", await File.ReadAllTextAsync(audit));
    }

    private static async Task InitializeGuildIdentityAsync(SameProcessHarness harness)
    {
        Result initialized = await harness.Services.GetRequiredService<GuildLocalHostPolicy>().InitializeIdentityAsync(default);

        Assert.True(initialized.IsSuccess, initialized.IsFailure ? initialized.Error.Message : null);
    }

    private static async Task<string> ReadGuildInventoryAsync(SameProcessHarness harness)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        if (opened.IsFailure) return opened.Error.Code;

        await using var connection = opened.Value;

        await using var query = connection.Connection.CreateCommand();

        query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'guild_%' ORDER BY name";

        List<string> names = [];

        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        List<string> counts = [];

        foreach (string name in names)
        {
            query.CommandText = $"SELECT count(*) FROM \"{name.Replace("\"", "\"\"")}\"";

            long count = Convert.ToInt64(await query.ExecuteScalarAsync());

            if (count != 0) counts.Add($"{name}={count}");
        }

        return string.Join(",", counts);
    }

    private static void AssertFactoryDiagnostic(
        TestCapturingLogger<DataRetentionService> log,
        string phase,
        Guid? operationId,
        LogLevel level)
    {
        TestLogEntry diagnostic = Assert.Single(log.Entries,
            entry => entry.Message.Contains("Healthy-catalog factory erasure", StringComparison.Ordinal));

        Assert.Equal(level, diagnostic.Level);

        Assert.Null(diagnostic.Exception);

        Assert.DoesNotContain("PRIVATE-factory-diagnostic", diagnostic.Message);

        Assert.DoesNotContain("/private/secret/catalog", diagnostic.Message);

        Assert.Equal(phase, Assert.Single(diagnostic.Properties!, property => property.Key == "DiagnosticStage").Value);

        Assert.Equal(operationId, Assert.Single(diagnostic.Properties!, property => property.Key == "OperationId").Value);

        Assert.Contains(operationId?.ToString() ?? "null", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }
}
