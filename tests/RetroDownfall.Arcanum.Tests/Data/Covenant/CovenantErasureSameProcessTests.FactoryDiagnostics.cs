using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
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

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Task<Result<DataRetentionApplyResult>> applying = harness.ApplyFactoryAsync(plan.PlanId);

        Result<DataRetentionApplyResult> result;

        try
        {
            await pause.WaitUntilPausedAsync();

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

        if (afterLaunch) faults.AfterFactoryLaunchReload = Fail;

        else faults.AfterFactoryStarted = _ => Fail();

        TestCapturingLogger<DataRetentionService> log = new();

        await using SameProcessHarness harness = await SameProcessHarness.CreateAsync(
            storeFaults: faults,
            serviceOverrides: services => CaptureRetentionDiagnostics(services, log));

        DataRetentionPlan plan = await harness.PlanFactoryAsync();

        Result<DataRetentionApplyResult> result = await harness.ApplyFactoryAsync(plan.PlanId);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, result.Error.Code);

        LongRunningOperation operation = await harness.ReadFactoryOperationAsync();

        AssertFactoryDiagnostic(log, "UnexpectedFailure", afterLaunch ? operation.Id : null, LogLevel.Error);
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
