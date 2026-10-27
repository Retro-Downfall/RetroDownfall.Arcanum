using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed partial class CovenantFamilyTerminalBookkeepingTests
{
    [SkippableTheory]
    [InlineData("checkpoint", "AdmissionClosed", "Covenant.RevisionConflict")]
    [InlineData("handles", "HandlesClosed", "Covenant.IntegrityFailure")]
    [InlineData("handles", "HandlesClosed", "Covenant.private-code-payload")]
    [InlineData("publish", "Publication", "Covenant.MaintenanceFailed")]
    [InlineData("writer", "WriterReopen", "Covenant.MaintenanceFailed")]
    [InlineData("gate", "Disposition", "Covenant.LifecycleConflict")]
    [InlineData("generation", "Generation", "Covenant.IntegrityFailure")]
    [InlineData("completion", "Completion", "Covenant.ManualRecoveryRequired")]
    public async Task Refused_boundaries_emit_one_closed_content_free_diagnostic(string fault, string stage, string code)
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        harness.Transition.Refused = fault;

        harness.Transition.RefusalCode = code;

        CovenantFamilyReinitializeCheckpointV1 checkpoint = Checkpoint(operation.Id);

        if (fault == "completion") harness.Operations.Throw = true;

        if (fault == "checkpoint") harness.Operations.RefuseCheckpoint = true;

        if (fault == "generation")
        {
            CovenantExclusiveRecoveryOwner owner = new(operation.Id,
                CovenantExclusiveOperation.CovenantFamilyReinitialize, CovenantOperationGateFixture.Digest(3));

            await using CovenantExclusiveLease lease = (await harness.Gate.AcquireExclusiveAsync(owner, CancellationToken.None)).Value;

            Assert.True((await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

            checkpoint = checkpoint with { Phase = CovenantFamilyReinitializePhase.ReopenedVerified };
        }

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, checkpoint, NativeHarness.Owner, CancellationToken.None);

        if (fault is "completion" or "gate") Assert.True(result.IsFailure);
        else Assert.Equal(CovenantExclusiveLeaseDisposition.KeepClosed, result.Value);

        TestLogEntry entry = Assert.Single(harness.Logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);

        Assert.Null(entry.Exception);

        Dictionary<string, object?> properties = entry.Properties!.ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal(operation.Id, properties["OperationId"]);

        Assert.Equal(stage, properties["Stage"]);

        Assert.Equal(code.Contains("private-", StringComparison.Ordinal) ? ErrorCodes.Covenant.MaintenanceFailed : code,
            properties["ErrorCode"]);

        Assert.Equal(fault == "completion" ? "UnconfirmedCompletion" : "Refused", properties["Outcome"]);

        Assert.Equal(fault == "completion" || fault == "gate"
            ? CovenantExclusiveLeaseDisposition.CommitAndReopen : CovenantExclusiveLeaseDisposition.KeepClosed,
            properties["RequestedDisposition"]);

        Assert.IsType<CovenantFamilyReinitializePhase>(properties["Phase"]);

        Assert.Contains("operation show", entry.Message);

        Assert.DoesNotContain("private-", entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(NativeHarness.Owner, entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(checkpoint.InstallationIdentity, entry.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(checkpoint.EffectDigest, entry.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(new[] { "OperationId", "Stage", "Phase", "RequestedDisposition", "Outcome", "ErrorCode", "{OriginalFormat}" }.Order(), properties.Keys.Order());
    }

    [SkippableFact]
    public async Task Admission_refusal_has_no_requested_disposition_or_durable_effect()
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        CovenantExclusiveRecoveryOwner other = CovenantOperationGateFixture.Owner(
            CovenantExclusiveOperation.CovenantFamilyReinitialize);

        await using CovenantExclusiveLease held = (await harness.Gate.AcquireExclusiveAsync(other, CancellationToken.None)).Value;

        Result<CovenantExclusiveLeaseDisposition> result = await harness.Coordinator.RunAsync(
            operation, Checkpoint(operation.Id), NativeHarness.Owner, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.False(harness.Transition.Published);

        Assert.False(harness.Transition.WriterReopened);

        Assert.Equal(0, harness.Operations.CompleteCalls);

        LongRunningOperation current = Assert.IsType<LongRunningOperation>(await harness.Store.GetAsync(operation.Id));

        Assert.Equal(operation.Revision, current.Revision);

        Assert.Equal(LongRunningOperationState.Running, current.State);

        TestLogEntry entry = Assert.Single(harness.Logger.Entries);

        Assert.Equal(LogLevel.Warning, entry.Level);

        Assert.Null(entry.Exception);

        Dictionary<string, object?> properties = entry.Properties!.ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal(operation.Id, properties["OperationId"]);

        Assert.Equal("Admission", properties["Stage"]);

        Assert.True(properties.ContainsKey("RequestedDisposition"));

        Assert.Null(properties["RequestedDisposition"]);

        Assert.False(properties.ContainsKey("Disposition"));
    }

    [SkippableFact]
    public async Task Successful_terminal_bookkeeping_emits_no_refusal_warning()
    {
        RequireNative();

        using NativeHarness harness = new(_db!);

        LongRunningOperation operation = await harness.StartAsync();

        Assert.True((await harness.Coordinator.RunAsync(operation, Checkpoint(operation.Id),
            NativeHarness.Owner, CancellationToken.None)).IsSuccess);

        Assert.Empty(harness.Logger.Entries);
    }
}
