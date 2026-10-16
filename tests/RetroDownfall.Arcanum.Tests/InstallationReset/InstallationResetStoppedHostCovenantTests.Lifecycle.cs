using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using ArcanumMaintenanceLock =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock;

using ArcanumMaintenanceLockAcquisitionResult =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLockAcquisitionResult;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The lifecycle of a stopped-host global reset: where its process can end, what the next process
/// does about it, and what a second tenure of the same process is allowed to take for granted.
/// </summary>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    private const string StoppedAtMarker = "The process was ended at";

    /// <summary>Where the process that is applying a global reset ends.</summary>
    public enum EndOfProcess
    {
        /// <summary>The record is Prepared and the claim has not been written.</summary>
        BeforeTheClaim = 1,

        /// <summary>The claim is written and the data step never started: no operation row exists.</summary>
        ClaimWithNoOperation = 2,

        /// <summary>The operation row exists at version zero and its launch checkpoint never committed.</summary>
        UnlaunchedOperation = 3,

        /// <summary>The launch checkpoint committed and the transition journal was never opened.</summary>
        LaunchWithoutJournal = 4,

        /// <summary>The journal is open and the canonical effect has run.</summary>
        JournalAfterTheCanonicalEffect = 5,

        /// <summary>The journal holds the stored receipt and only its retirement is left.</summary>
        ReceiptStoredRetirementLeft = 6,

        /// <summary>The journal is retired and the reset record still says Prepared.</summary>
        JournalRetiredRecordPrepared = 7,

        /// <summary>The Grimoire and the secret store are gone and verification has not been recorded.</summary>
        AfterOfflineCleanup = 8,
    }

    public static TheoryData<bool, EndOfProcess> EndOfProcessCases()
    {
        TheoryData<bool, EndOfProcess> cases = [];

        foreach (bool feature in new[] { false, true })
        {
            foreach (EndOfProcess end in Enum.GetValues<EndOfProcess>())
            {
                cases.Add(feature, end);
            }
        }

        return cases;
    }

    /// <summary>
    /// Wherever the process that applies a global reset ends, the host refuses to start until a
    /// second apply finishes it, and the second apply reaches the one verdict the first had earned.
    /// </summary>
    /// <remarks>
    /// The verdict is a completed reset, except where the process ended after an operation row was
    /// created and before its launch checkpoint committed. Effects cannot happen before that
    /// checkpoint, so nothing was erased, and the resume settles the row and retires the record
    /// instead of running the ordinary tables' deletion without the protected lifecycle.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(EndOfProcessCases))]
    public async Task A_reset_whose_process_ended_resumes_to_its_one_verdict(
        bool covenantFeature,
        EndOfProcess end)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        _ = await StoppedHostResetTestSupport.SeedOrdinarySessionAsync(_fixture);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        CliTestResult interrupted = await RunAsync(
            covenantFeature,
            services => EndTheProcess(services, end),
            GlobalArguments(apply: true));

        string described = $"exit {interrupted.ExitCode}\nstdout: {interrupted.Output}\nstderr: {interrupted.Error}";

        Assert.NotEqual((int)CliExitCode.Success, interrupted.ExitCode);

        await AssertTheRecordAfterAsync(end, described);

        await AssertTheHostRefusesToStartAsync(covenantFeature);

        int daemonRemovals = _mutations.Calls;

        List<string> nestedVerdicts = [];

        CliTestResult resumed = await RunAsync(
            covenantFeature,
            services => StoppedHostResetTestSupport.InterceptDataService(
                services,
                inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                {
                    AfterApply = applied => nestedVerdicts.Add(
                        applied.IsSuccess ? "applied" : applied.Error.Code + ": " + applied.Error.Message),
                }),
            GlobalArguments(apply: true));

        described = $"exit {resumed.ExitCode}\nstdout: {resumed.Output}\nstderr: {resumed.Error}\nrecord: {await DescribeTheRecordAsync()}\nnested apply: {string.Join(" | ", nestedVerdicts)}";

        if (end is EndOfProcess.UnlaunchedOperation)
        {
            // Nothing was erased, the operation was settled, and the record was retired with the
            // account of what the attempt had already done.
            Assert.Equal((int)CliExitCode.GenericError, resumed.ExitCode);

            Assert.Contains(ErrorCodes.Data.Conflict, resumed.Output, StringComparison.Ordinal);

            Assert.Contains(InstallationResetService.ClaimRetiredNote, resumed.Error, StringComparison.Ordinal);

            Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

            Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM \"Sessions\";"));

            Assert.Equal(
                (int)LongRunningOperationState.Failed,
                await StoppedHostResetTestSupport.ScalarAsync(
                    "SELECT \"State\" FROM \"LongRunningOperations\" WHERE \"Kind\" = $kind;",
                    ("$kind", LongRunningOperationKinds.DataRetentionFactoryReset)));

            Assert.True(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

            AssertNoActiveResetOrJournal();

            return;
        }

        Assert.True(resumed.ExitCode == (int)CliExitCode.Success, described);

        using JsonDocument document = JsonDocument.Parse(resumed.Output);

        Assert.Equal("Completed", document.RootElement.GetProperty("phase").GetString());

        Assert.False(document.RootElement.GetProperty("resumeRequired").GetBoolean(), described);

        Assert.False(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

        Assert.False(Directory.Exists(ArcanumPaths.SecretStoreDirectory), described);

        Assert.True(_mutations.Calls >= daemonRemovals, described);
    }

    /// <summary>
    /// A catalog that moved between the replan and the data step is a refusal before any effect, and
    /// the claim it leaves behind is retired with the account of what the attempt had already done.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_catalog_that_moves_before_the_data_step_retires_the_claim_that_never_launched(
        bool covenantFeature)
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid memoryId = Guid.NewGuid();

        await LabeledSagaMemorySeed.SeedAsync(_fixture, memoryId);

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        CliTestResult result = await RunAsync(
            covenantFeature,
            static services => StoppedHostResetTestSupport.InterceptDataService(
                services,
                static inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                {
                    // The operator confirmed a plan; a Session appears before the data step re-checks it.
                    BeforeApply = static async (_, _) =>
                        await StoppedHostResetTestSupport.ExecuteAsync(
                            "INSERT INTO \"Sessions\" (\"Id\", \"Status\", \"CreatedAt\", \"UpdatedAt\") "
                            + "VALUES ($id, 'archived', '2026-10-16T00:00:00.0000000Z', '2026-10-16T00:00:00.0000000Z');",
                            ("$id", Guid.NewGuid().ToString("D").ToUpperInvariant())),
                }),
            GlobalArguments(apply: true));

        string described = $"exit {result.ExitCode}\nstdout: {result.Output}\nstderr: {result.Error}";

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        // The data step's own refusal, and the account that the daemon registration was removed.
        Assert.Contains(ErrorCodes.Data.PlanChanged, result.Output, StringComparison.Ordinal);

        Assert.Contains(InstallationResetService.ClaimRetiredNote, result.Error, StringComparison.Ordinal);

        Assert.Equal(1, _mutations.Calls);

        // No operation was ever created for it, nothing was erased, and the record is gone.
        Assert.Equal(0, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM \"LongRunningOperations\";"));

        Assert.Equal(1, await StoppedHostResetTestSupport.ScalarAsync("SELECT COUNT(*) FROM saga_memories;"));

        Assert.True(Directory.Exists(ArcanumPaths.GrimoireDirectory), described);

        AssertNoActiveResetOrJournal();

        // The nested apply opened the ordinary context to re-check the plan, and the facade let go of
        // every handle on the way out: nothing in this process still has the Grimoire open.
        Assert.True(
            await StoppedHostResetTestSupport.NoHandleRemainsOpenAsync(),
            "An ordinary handle was left open on the Grimoire after the facade returned.");
    }

    private void EndTheProcess(IServiceCollection services, EndOfProcess end)
    {
        switch (end)
        {
            case EndOfProcess.BeforeTheClaim:

                StoppedHostResetTestSupport.DieBeforeActiveWrite(
                    services,
                    static (current, next) => current.Payload.NestedTransitionReceipt is null
                        && next.NestedTransitionReceipt is not null);

                break;

            case EndOfProcess.ClaimWithNoOperation:

                StoppedHostResetTestSupport.InterceptDataService(
                    services,
                    static inner => new StoppedHostResetTestSupport.InterceptingStoppedHostDataService(inner)
                    {
                        BeforeApply = static (_, _) => throw new StoppedHostResetTestSupport.SimulatedProcessDeathException(
                            "the claim, before the data step"),
                    });

                break;

            case EndOfProcess.UnlaunchedOperation:

                StoppedHostResetTestSupport.DieAroundCheckpoint(
                    services,
                    dieBefore: static (expected, _) => expected == 0,
                    dieAfter: static (_, _) => false);

                break;

            case EndOfProcess.LaunchWithoutJournal:

                StoppedHostResetTestSupport.DieAroundCheckpoint(
                    services,
                    dieBefore: static (_, _) => false,
                    dieAfter: static (expected, _) => expected == 0);

                break;

            case EndOfProcess.JournalAfterTheCanonicalEffect:

                StoppedHostResetTestSupport.UseFaultSeams(
                    services,
                    static (boundary, phase, _) => Task.FromResult(
                        boundary is CovenantErasureFaultBoundary.AfterPhaseEffect
                            && phase is CovenantResetPhase.CanonicalApplied
                            ? Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "Injected crash."))
                            : Result.Success()));

                break;

            case EndOfProcess.ReceiptStoredRetirementLeft:

                StoppedHostResetTestSupport.UseFaultSeams(
                    services,
                    faultSeam: null,
                    terminalSuffixFaultSeam: static (boundary, _) => Task.FromResult(
                        boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending
                            ? Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "Injected crash."))
                            : Result.Success()));

                break;

            case EndOfProcess.JournalRetiredRecordPrepared:

                StoppedHostResetTestSupport.UseFaultSeams(
                    services,
                    faultSeam: null,
                    terminalSuffixFaultSeam: static (boundary, _) => Task.FromResult(
                        boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired
                            ? Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "Injected crash."))
                            : Result.Success()));

                break;

            default:

                StoppedHostResetTestSupport.DieBeforeActiveWrite(
                    services,
                    static (_, next) => next.Phase is InstallationResetPhase.Verified);

                break;
        }
    }

    /// <summary>What the interrupted process left in the reset record and the operation ledger.</summary>
    private async Task AssertTheRecordAfterAsync(EndOfProcess end, string described)
    {
        InstallationResetActiveStore store = new(ArcanumPaths.GrimoireDirectory, _credentials);

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(CancellationToken.None);

        Assert.True(inspected.IsSuccess, described);

        InstallationResetActivePublication publication = Assert.IsType<InstallationResetActivePublication>(
            inspected.Value.Publication);

        InstallationResetActivePayloadV3 payload = publication.Payload;

        if (end is EndOfProcess.BeforeTheClaim)
        {
            Assert.Equal(InstallationResetPhase.Prepared, payload.Phase);

            Assert.False(payload.PointOfNoReturn, described);

            Assert.Null(payload.NestedTransitionReceipt);

            Assert.Equal(0, await CountFactoryResetOperationsAsync());

            return;
        }

        if (end is EndOfProcess.AfterOfflineCleanup)
        {
            Assert.Equal(InstallationResetPhase.OfflineCleanupComplete, payload.Phase);

            Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile), described);

            return;
        }

        Assert.Equal(InstallationResetPhase.Prepared, payload.Phase);

        Assert.True(payload.PointOfNoReturn, described);

        Assert.NotNull(payload.NestedTransitionReceipt);

        if (end is EndOfProcess.ClaimWithNoOperation)
        {
            Assert.Equal(0, await CountFactoryResetOperationsAsync());
        }

        if (end is EndOfProcess.UnlaunchedOperation)
        {
            Assert.Equal(1, await CountFactoryResetOperationsAsync());

            Assert.Equal(0, await FactoryResetCheckpointVersionAsync());
        }

        if (end is EndOfProcess.LaunchWithoutJournal)
        {
            Assert.Equal(2, await FactoryResetCheckpointVersionAsync());
        }
    }

    private async Task<string> DescribeTheRecordAsync()
    {
        InstallationResetActiveStore store = new(ArcanumPaths.GrimoireDirectory, _credentials);

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(CancellationToken.None);

        if (inspected.IsFailure || inspected.Value.Publication is not { } publication)
        {
            return "none";
        }

        InstallationResetActivePayloadV3 payload = publication.Payload;

        return $"{payload.Phase}, point of no return {payload.PointOfNoReturn}, last error {payload.LastErrorCode}, "
            + $"receipt {payload.NestedTransitionReceipt?.Phase}";
    }

    private static Task<long> CountFactoryResetOperationsAsync() =>
        StoppedHostResetTestSupport.ScalarAsync(
            "SELECT COUNT(*) FROM \"LongRunningOperations\" WHERE \"Kind\" = $kind;",
            ("$kind", LongRunningOperationKinds.DataRetentionFactoryReset));

    private static Task<long> FactoryResetCheckpointVersionAsync() =>
        StoppedHostResetTestSupport.ScalarAsync(
            "SELECT \"CheckpointVersion\" FROM \"LongRunningOperations\" WHERE \"Kind\" = $kind;",
            ("$kind", LongRunningOperationKinds.DataRetentionFactoryReset));

    /// <summary>
    /// The serving host refuses to start beside this record: the same check its startup makes, over
    /// the same recovery pass, under the same lock.
    /// </summary>
    private async Task AssertTheHostRefusesToStartAsync(bool covenantFeature)
    {
        await using ServiceProvider provider = Compose(covenantFeature).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetStartupRecovery recovery = scope.ServiceProvider
            .GetRequiredService<IInstallationResetStartupRecovery>();

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        Result<InstallationResetStartupRecoveryState> state = await recovery
            .RecoverBeforeBootstrapAsync(held, CancellationToken.None);

        // Two ways the host's own startup refuses, and it takes either: a recovery pass that cannot
        // read the evidence safely stops the start with its message, and one that reads an active
        // reset the host has no business serving beside stops it with the reset named.
        if (state.IsFailure)
        {
            return;
        }

        ActiveInstallationReset active = Assert.IsType<ActiveInstallationReset>(state.Value.ActiveReset);

        Assert.False(
            InstallationResetHostStartupAdmission.AllowsRecoveryHost(active),
            "The serving host would have started beside an unfinished global reset.");
    }
}
