using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class BatchProcessingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_PrelaunchFailure_CompletesExactTaskAndReleasesSlot(bool cancellation)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource stopBudget = new();

        harness.Service.AfterBatchRegisteredTestSeam = async () =>
        {
            registered.TrySetResult();

            await release.Task;

            if (cancellation) { throw new OperationCanceledException("prelaunch"); }

            throw new InvalidOperationException("prelaunch");
        };

        Task tick = harness.Service.TickAsync(CancellationToken.None);

        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stopping = harness.Service.StopAsync(stopBudget.Token);

        try
        {
            Assert.False(stopping.IsCompleted);

            release.TrySetResult();

            if (cancellation) { await Assert.ThrowsAsync<OperationCanceledException>(() => tick); }
            else { await Assert.ThrowsAsync<InvalidOperationException>(() => tick); }

            Assert.False(harness.Service.IsBatchInFlight(batch.Id));

            await stopping.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, harness.Provider.ExecutePromptCallCount);

            Assert.Equal(BatchStatuses.Validating, (await _batches!.GetByIdAsync(batch.Id))!.Status);
        }
        finally
        {
            release.TrySetResult();

            stopBudget.Cancel();

            await stopping;
        }
    }

    [Fact]
    public async Task Admission_ZeroObservedGeneration_WaitsAtZeroUntilHostCancellation()
    {
        BatchAdmissionGate gate = new() { ObservedGeneration = 0 };

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        IGrimoireClosingOwner closing = gate.Close();

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, Assert.Single(gate.WaitGenerations));

            Assert.False(processing.IsCompleted);

            stopping.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

            Assert.Equal(0, harness.Scopes.Active);
        }
        finally
        {
            stopping.Cancel();

            try { await processing; } catch (OperationCanceledException) { } catch (ArgumentOutOfRangeException) { }

            await gate.ReopenAsync(closing);
        }
    }

    [Theory]
    [InlineData(BatchStatuses.Completed)]
    [InlineData(BatchStatuses.Failed)]
    [InlineData(BatchStatuses.Validating)]
    [InlineData("missing")]
    [InlineData("replaced-input")]
    public async Task Admission_ClaimChangedDuringDeferral_DoesNotDispatchOrFinalizeAgain(string replacementStatus)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(65);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt => { if (attempt == 2) { closing = gate.Close(); } };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));

            if (replacementStatus == "missing")
            {
                Assert.Equal(1, await _db!.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Batches WHERE Id = {batch.Id.ToString("N")}"));
            }
            else if (replacementStatus == "replaced-input")
            {
                Guid changedInput = await SeedInputFileAsync(BatchLine("replacement"));

                Assert.Equal(1, await _db!.Database.ExecuteSqlInterpolatedAsync($"UPDATE Batches SET InputFileId = {changedInput.ToString()} WHERE Id = {batch.Id.ToString("N")}"));
            }
            else
            {
                await _batches!.UpdateStatusAsync(batch.Id, replacementStatus, DateTimeOffset.UtcNow, null, null);
            }

            IGrimoireClosingOwner toReopen = closing!;

            closing = null;

            await gate.ReopenAsync(toReopen);

            await processing.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(64, harness.Provider.ExecutePromptCallCount);

            Assert.Equal(0, harness.Blobs.CreatedWriters);

            BatchRecord? current = await _batches!.GetByIdAsync(batch.Id);

            if (replacementStatus == "missing") { Assert.Null(current); }
            else { Assert.Equal(replacementStatus == "replaced-input" ? BatchStatuses.InProgress : replacementStatus, current!.Status); }
        }
        finally
        {
            stopping.Cancel();

            if (closing is not null) { await gate.ReopenAsync(closing); }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_OperatorCancellationSealsInsideWonPageAndSurvivesPublicationDeferral()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource providerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Provider.ExecuteEntered = entered;

        harness.Provider.ExecuteGate = providerRelease;

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IGrimoireClosingOwner? closing = gate.Close();

        try
        {
            await _batches!.UpdateStatusAsync(batch.Id, BatchStatuses.Cancelled, DateTimeOffset.UtcNow, null, null);

            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(processing.IsCompleted);

            BatchLineCheckpoint sealedLine = Assert.Single(await _batches.ListLineCheckpointsAsync(batch.Id, 1, 1));

            Assert.Equal(BatchLineCheckpointState.Completed, sealedLine.State);

            Assert.Contains("batch_interrupted_after_dispatch", sealedLine.JsonLine, StringComparison.Ordinal);

            Assert.Equal(0, harness.Scopes.Active);

            IGrimoireClosingOwner toReopen = closing;

            closing = null;

            await gate.ReopenAsync(toReopen);

            await processing.WaitAsync(TimeSpan.FromSeconds(10));

            BatchRecord finished = Assert.IsType<BatchRecord>(await _batches.GetByIdAsync(batch.Id));

            Assert.Equal(BatchStatuses.Cancelled, finished.Status);

            Assert.Equal(1, finished.FailedRequestCount);

            Assert.NotNull(finished.OutputFileId);

            Assert.Equal(1, harness.Provider.ExecutePromptCallCount);

            Assert.Empty(await _batches.ListLineCheckpointsAsync(batch.Id, 1, 1));
        }
        finally
        {
            stopping.Cancel();

            providerRelease.TrySetResult();

            if (closing is not null) { await gate.ReopenAsync(closing); }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_LostParseOnlyPage_PreservesPhysicalLineAndCountersUntilReopen()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync("\n \t\r\nnot-json\n");

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt => { if (attempt == 1) { closing = gate.Close(); } };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, (await _batches!.GetByIdAsync(batch.Id))!.TotalRequestCount);

            Assert.Empty(await _batches.ListLineCheckpointsAsync(batch.Id, 1, 3));

            IGrimoireClosingOwner toReopen = closing!;

            closing = null;

            await gate.ReopenAsync(toReopen);

            await processing.WaitAsync(TimeSpan.FromSeconds(10));

            BatchRecord finished = Assert.IsType<BatchRecord>(await _batches.GetByIdAsync(batch.Id));

            Assert.Equal(1, finished.TotalRequestCount);

            Assert.Equal(0, harness.Provider.ExecutePromptCallCount);

            string errors = await ReadArtifactTextAsync(UploadedFileStorage.ResolvePath(finished.ErrorFileId!.Value));

            Assert.Contains("\"line\":3", errors, StringComparison.Ordinal);
        }
        finally
        {
            stopping.Cancel();

            if (closing is not null) { await gate.ReopenAsync(closing); }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_PublicationReadFailure_DoesNotDeleteReplacedCiphertext()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        string? replacement = null;

        harness.Blobs.BeforeArtifactRead = path =>
        {
            File.Move(path, path + ".original");

            File.WriteAllText(path, "replacement-not-owned-by-this-publication");

            replacement = path;

            throw new IOException("Injected read failure after namespace replacement.");
        };

        await Assert.ThrowsAsync<IOException>(() => harness.Service.ProcessBatchAsync(batch, CancellationToken.None));

        Assert.NotNull(replacement);

        Assert.True(File.Exists(replacement));

        Assert.Equal("replacement-not-owned-by-this-publication", await File.ReadAllTextAsync(replacement));
    }

    [Fact]
    public async Task Admission_KeepClosed_RetainsOnlyExactInFlightBatchAndConfiguredSlot()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord first = await SeedAdmissionBatchAsync(65);

        BatchRecord second = await SeedAdmissionBatchAsync(1);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt => { if (attempt == 2) { closing = gate.Close(); } };

        using CancellationTokenSource stopping = new();

        await harness.Service.TickAsync(stopping.Token);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(harness.Service.IsBatchInFlight(first.Id));

            Assert.False(harness.Service.IsBatchInFlight(second.Id));

            Assert.Equal(0, harness.Scopes.Active);

            Assert.Equal(0, gate.ActiveLeases);

            Assert.True((await gate.DrainRequestAndWorkAsync(closing!, CancellationToken.None)).IsSuccess);

            IGrimoireExclusiveClosedLease closed = (await gate.CloseConnectionAdmissionAsync(closing!, CancellationToken.None)).Value;

            CovenantExclusiveRecoveryOwner owner = closing!.Owner;

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            await closing.DisposeAsync();

            closing = null;

            int scopesBefore = harness.Scopes.CreationLeaseCounts.Count;

            for (int tick = 0; tick < 10; tick++) { await harness.Service.TickAsync(stopping.Token); }

            Assert.Equal(scopesBefore, harness.Scopes.CreationLeaseCounts.Count);

            Assert.Equal(1, gate.Events.Count(entry => entry == "wait"));

            Assert.Equal(64, harness.Provider.ExecutePromptCallCount);

            closing = gate.BeginOrResumeExclusive(owner).Value;

            closed = (await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

            await closed.DisposeAsync();

            await closing.DisposeAsync();

            closing = null;

            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Equal(65, harness.Provider.ExecutePromptCallCount);

            Assert.Equal(BatchStatuses.Validating, (await _batches!.GetByIdAsync(second.Id))!.Status);

            Assert.False(harness.Service.IsBatchInFlight(first.Id));
        }
        finally
        {
            stopping.Cancel();

            await harness.Service.StopAsync(CancellationToken.None);

            if (closing is not null) { await closing.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task Admission_RecloseBeforeReacquisition_KeepsOneIdentityWithoutEffects()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        IGrimoireClosingOwner? closing = gate.Close();

        TaskCompletionSource secondWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.BeforeLease = attempt => { if (attempt == 2) { closing = gate.Close(); } };

        gate.OnWait = wait => { if (wait == 2) { secondWait.TrySetResult(); } };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

            IGrimoireClosingOwner firstClosing = closing;

            closing = null;

            await gate.ReopenAsync(firstClosing);

            await secondWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, harness.Provider.ExecutePromptCallCount);

            Assert.Empty(harness.Scopes.CreationLeaseCounts);

            Assert.Equal(BatchStatuses.Validating, (await _batches!.GetByIdAsync(batch.Id))!.Status);

            IGrimoireClosingOwner secondClosing = closing!;

            closing = null;

            await gate.ReopenAsync(secondClosing);

            await processing.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Provider.ExecutePromptCallCount);
        }
        finally
        {
            stopping.Cancel();

            if (closing is not null) { await gate.ReopenAsync(closing); }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_HostStopOwnsAndJoinsBatchWaitingForReopen()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt => { if (attempt == 1) { closing = gate.Close(); } };

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(harness.Service.IsBatchInFlight(batch.Id));

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(harness.Service.IsBatchInFlight(batch.Id));

            Assert.Equal(BatchStatuses.InProgress, (await _batches!.GetByIdAsync(batch.Id))!.Status);

            Assert.Equal(0, harness.Provider.ExecutePromptCallCount);

            Assert.Equal(0, harness.Scopes.Active);

            Assert.Equal(0, gate.ActiveLeases);
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);

            if (closing is not null) { await gate.ReopenAsync(closing); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_FileRegistrationFault_CleansOwnedBytesAndAnyCommittedRow(bool afterCommit)
    {
        BatchAdmissionGate gate = new();

        Task FailAsync(UploadedFileRecord record) => throw new IOException("Injected file registration fault.");

        await using AdmissionService harness = CreateAdmissionService(gate, configure: services =>
            services.AddScoped<IUploadedFileRepository>(sp => new BatchAdmissionFiles(
                new UploadedFileRepository(sp.GetRequiredService<ArcanumDbContext>()),
                afterCommit ? FailAsync : static _ => Task.CompletedTask,
                afterCommit ? null : FailAsync)));

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        await Assert.ThrowsAsync<IOException>(() => harness.Service.ProcessBatchAsync(batch, CancellationToken.None));

        Assert.Single(await _files!.ListAsync(null));

        Assert.Single(Directory.GetFiles(ArcanumPaths.FilesDirectory));

        Assert.Single(await _batches!.ListLineCheckpointsAsync(batch.Id, 1, 1));
    }

    [Fact]
    public async Task Admission_StopWhileTickPreflightBlocked_DoesNotLaunchAfterStopSnapshot()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        using ManualResetEventSlim release = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Scopes.BeforeCreation = () =>
        {
            entered.TrySetResult();

            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };

        Task tick = Task.Factory.StartNew(() => harness.Service.TickAsync(CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await harness.Service.StopAsync(CancellationToken.None);

            release.Set();

            await tick.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(harness.Service.IsBatchInFlight(batch.Id));

            Assert.Equal(0, harness.Provider.ExecutePromptCallCount);
        }
        finally
        {
            release.Set();

            await tick;

            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Admission_BudgetDecisionSurvivesPublicationDeferralWithoutAnotherReservation()
    {
        BatchAdmissionGate gate = new();

        BatchAdmissionAccounting accounting = new(gate) { RejectReservation = 2 };

        await using AdmissionService harness = CreateAdmissionService(gate, accounting, accounting);

        BatchRecord batch = await SeedAdmissionBatchAsync(65);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt => { if (attempt == 3) { closing = gate.Close(); } };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await Task.WhenAny(processing, gate.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(processing.IsCompleted);

            Assert.Equal(2, accounting.Reservations);

            Assert.Equal(64, harness.Provider.ExecutePromptCallCount);

            await gate.ReopenAsync(closing!);

            closing = null;

            await processing.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(2, accounting.Runs);

            Assert.Equal(2, accounting.Reservations);

            BatchRecord finished = Assert.IsType<BatchRecord>(await _batches!.GetByIdAsync(batch.Id));

            Assert.Equal(BatchStatuses.Failed, finished.Status);

            Assert.Equal(64, finished.CompletedRequestCount);

            Assert.Equal(1, finished.FailedRequestCount);
        }
        finally
        {
            stopping.Cancel();

            if (closing is not null) { await gate.ReopenAsync(closing); }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_WinningPageHoldsDrainThroughAccountingGroupScopeAndLeaseDisposal()
    {
        BatchAdmissionGate gate = new();

        BatchAdmissionAccounting accounting = new(gate);

        await using AdmissionService harness = CreateAdmissionService(gate, accounting, accounting);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        TaskCompletionSource accountingEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseAccounting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource scopeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseScope = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource leaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseLease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        accounting.BeforeReconcile = async () => { accountingEntered.TrySetResult(); await releaseAccounting.Task; };

        harness.Scopes.BeforeDisposal = async () =>
        {
            if (gate.ActiveGroups == 0) { scopeEntered.TrySetResult(); await releaseScope.Task; }
        };

        gate.BeforeLeaseDisposal = async () => { leaseEntered.TrySetResult(); await releaseLease.Task; };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        await accountingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IGrimoireClosingOwner closing = gate.Close();

        Task drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        try
        {
            Assert.False(drain.IsCompleted);

            Assert.Equal(1, gate.ActiveGroups);

            releaseAccounting.TrySetResult();

            await scopeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(drain.IsCompleted);

            Assert.Equal(0, gate.ActiveGroups);

            Assert.Equal(1, gate.ActiveLeases);

            releaseScope.TrySetResult();

            await leaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(drain.IsCompleted);

            Assert.Equal(0, harness.Scopes.Active);

            releaseLease.TrySetResult();

            await drain.WaitAsync(TimeSpan.FromSeconds(5));

            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, (await _batches!.GetByIdAsync(batch.Id))!.CompletedRequestCount);
        }
        finally
        {
            releaseAccounting.TrySetResult();

            releaseScope.TrySetResult();

            releaseLease.TrySetResult();

            stopping.Cancel();

            await gate.ReopenAsync(closing);

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Admission_ArtifactWinnerFinishesBothFileRowsAndBatchLinkageDuringClosing()
    {
        BatchAdmissionGate gate = new();

        IGrimoireClosingOwner? closing = null;

        int filesCreated = 0;

        await using AdmissionService harness = CreateAdmissionService(gate, configure: services =>
            services.AddScoped<IUploadedFileRepository>(sp => new BatchAdmissionFiles(
                new UploadedFileRepository(sp.GetRequiredService<ArcanumDbContext>()), record =>
                {
                    Assert.Equal(1, gate.ActiveGroups);

                    if (Interlocked.Increment(ref filesCreated) == 1) { closing = gate.Close(); }

                    return Task.CompletedTask;
                })));

        BatchRecord batch = await SeedAdmissionBatchAsync(BatchLine("good") + "not-json\n");

        try
        {
            await harness.Service.ProcessBatchAsync(batch, CancellationToken.None);

            Assert.Equal(2, filesCreated);

            Assert.Equal(2, gate.GroupAttempts);

            Assert.False(gate.Waiting.Task.IsCompleted);

            BatchRecord finished = Assert.IsType<BatchRecord>(await _batches!.GetByIdAsync(batch.Id));

            Assert.Equal(BatchStatuses.Completed, finished.Status);

            Assert.NotNull(finished.OutputFileId);

            Assert.NotNull(finished.ErrorFileId);

            Assert.Empty(await _batches.ListLineCheckpointsAsync(batch.Id, 1, 2));
        }
        finally
        {
            if (closing is not null) { await gate.ReopenAsync(closing); }
        }
    }

    [Fact]
    public async Task Admission_StopDuringRegistration_JoinsTheAlreadyVisibleTask()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Service.AfterBatchRegisteredTestSeam = async () =>
        {
            registered.TrySetResult();

            await release.Task;
        };

        Task tick = harness.Service.TickAsync(CancellationToken.None);

        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task stopping = harness.Service.StopAsync(CancellationToken.None);

        try
        {
            Assert.True(harness.Service.IsBatchInFlight(batch.Id));

            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            release.TrySetResult();

            await tick.WaitAsync(TimeSpan.FromSeconds(10));

            await harness.Service.StopAsync(CancellationToken.None);

            await stopping;
        }

        Assert.False(harness.Service.IsBatchInFlight(batch.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_LaterPublicationFailure_CompensatesEveryNewFileAndRetainsCheckpoints(bool finalStatusFailure)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(BatchLine("valid") + "not-json\n");

        string trigger = finalStatusFailure
            ? "CREATE TRIGGER Task5Failure BEFORE UPDATE OF Status ON Batches WHEN NEW.Status = 'completed' BEGIN SELECT RAISE(FAIL, 'task5-final'); END;"
            : "CREATE TRIGGER Task5Failure BEFORE INSERT ON UploadedFiles WHEN NEW.Purpose = 'error' BEGIN SELECT RAISE(FAIL, 'task5-second-file'); END;";

        await _db!.Database.ExecuteSqlRawAsync(trigger);

        await Assert.ThrowsAsync<SqliteException>(() => harness.Service.ProcessBatchAsync(batch, CancellationToken.None));

        Assert.Single(await _files!.ListAsync(null));

        Assert.Single(Directory.GetFiles(ArcanumPaths.FilesDirectory));

        Assert.Equal(2, (await _batches!.ListLineCheckpointsAsync(batch.Id, 1, 2)).Count);

        Assert.Equal(BatchStatuses.InProgress, (await _batches.GetByIdAsync(batch.Id))!.Status);

        Assert.Equal(0, gate.ActiveGroups);

        Assert.Equal(0, gate.ActiveLeases);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(128, true)]
    public async Task Admission_ExactlyOneGroupPerExistingAccountingPageAndPublication(int records, bool blanks)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(records, blanks);

        await harness.Service.ProcessBatchAsync(batch, CancellationToken.None);

        Assert.Equal(records == 0 ? 0 : (records + 63) / 64 + 1, gate.GroupAttempts);

        Assert.Equal(records == 0 ? 0 : 2, harness.Blobs.CreatedWriters);

        Assert.All(harness.Scopes.CreationLeaseCounts, held => Assert.True(held > 0));

        Assert.DoesNotContain("scope-sync-close", gate.Events);

        Assert.Equal(0, gate.ActiveLeases);

        Assert.Equal(0, gate.ActiveGroups);

        BatchRecord finished = Assert.IsType<BatchRecord>(await _batches!.GetByIdAsync(batch.Id));

        Assert.Equal(BatchStatuses.Completed, finished.Status);

        Assert.Equal(records, finished.CompletedRequestCount);

        if (records == 0)
        {
            Assert.Null(finished.OutputFileId);

            Assert.Null(finished.ErrorFileId);

            Assert.Single(await _files!.ListAsync(null));

            Assert.Single(Directory.GetFiles(ArcanumPaths.FilesDirectory));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_RecordMaterializationAndOversizedSpillRequirePageFrontier(bool valid)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        harness.Blobs.RequireReadFrontier = true;

        string large = new('x', BatchJsonlRecordReader.InMemoryByteLimit + 1);

        string input = valid ? BatchLine("large", large) : large + "\n";

        BatchRecord batch = await SeedAdmissionBatchAsync(input + "\n" + BatchLine("next"));

        await harness.Service.ProcessBatchAsync(batch, CancellationToken.None);

        Assert.Equal(2, gate.GroupAttempts);

        Assert.Equal(valid ? 2 : 1, harness.Provider.ExecutePromptCallCount);

        BatchRecord finished = Assert.IsType<BatchRecord>(await _batches!.GetByIdAsync(batch.Id));

        Assert.Equal(2, finished.TotalRequestCount);
    }

    [Fact]
    public async Task Admission_TickDenied_DoesNotCreateScopeOrWaiterOrClaim()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        IGrimoireClosingOwner closing = gate.Close();

        try
        {
            await harness.Service.TickAsync(CancellationToken.None);

            Assert.Empty(harness.Scopes.CreationLeaseCounts);

            Assert.False(harness.Service.IsBatchInFlight(batch.Id));

            Assert.False(gate.Waiting.Task.IsCompleted);

            Assert.Equal(BatchStatuses.Validating, (await _batches!.GetByIdAsync(batch.Id))!.Status);
        }
        finally
        {
            await gate.ReopenAsync(closing);

            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Admission_InitialBatchLeaseDenied_WaitsWithoutClaimAndHonorsHostCancellation()
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(1);

        using CancellationTokenSource stopping = new();

        IGrimoireClosingOwner closing = gate.Close();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await Task.WhenAny(processing, gate.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(processing.IsCompleted);

            Assert.Empty(harness.Scopes.CreationLeaseCounts);

            Assert.Equal(BatchStatuses.Validating, (await _batches!.GetByIdAsync(batch.Id))!.Status);

            stopping.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }
        finally
        {
            stopping.Cancel();

            await gate.ReopenAsync(closing);

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 64)]
    [InlineData(3, 65)]
    public async Task Admission_LostPageOrPublication_RetainsExactBatchAndDoesNotReplay(int deniedGroup, int completed)
    {
        BatchAdmissionGate gate = new();

        await using AdmissionService harness = CreateAdmissionService(gate);

        BatchRecord batch = await SeedAdmissionBatchAsync(65, blanks: true);

        IGrimoireClosingOwner? closing = null;

        gate.BeforeGroup = attempt =>
        {
            if (attempt == deniedGroup)
            {
                closing = gate.Close();
            }
        };

        using CancellationTokenSource stopping = new();

        Task processing = harness.Service.ProcessBatchAsync(batch, stopping.Token);

        try
        {
            await Task.WhenAny(processing, gate.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(processing.IsCompleted);

            Assert.NotNull(closing);

            Assert.Equal(completed, harness.Provider.ExecutePromptCallCount);

            Assert.Equal(0, harness.Blobs.ActiveInputs);

            Assert.Equal(0, harness.Scopes.Active);

            Assert.Equal(0, harness.Blobs.CreatedWriters);

            BatchRecord retained = Assert.IsType<BatchRecord>(await _batches!.GetByIdAsync(batch.Id));

            Assert.Equal(BatchStatuses.InProgress, retained.Status);

            Assert.Equal(completed, retained.CompletedRequestCount);

            Assert.Null(retained.CompletedAt);

            Assert.Null(retained.OutputFileId);

            await gate.ReopenAsync(closing!);

            closing = null;

            await processing.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(65, harness.Provider.ExecutePromptCallCount);

            BatchRecord finished = Assert.IsType<BatchRecord>(await _batches.GetByIdAsync(batch.Id));

            Assert.Equal(BatchStatuses.Completed, finished.Status);

            Assert.Equal(65, finished.TotalRequestCount);

            string output = await ReadArtifactTextAsync(UploadedFileStorage.ResolvePath(finished.OutputFileId!.Value));

            Assert.Contains("req-65", output, StringComparison.Ordinal);
        }
        finally
        {
            stopping.Cancel();

            if (closing is not null)
            {
                await gate.ReopenAsync(closing);
            }

            try { await processing; } catch (OperationCanceledException) { }
        }
    }

    private AdmissionService CreateAdmissionService(BatchAdmissionGate gate,
        ITurnRunWriter? writer = null, IBudgetReservationService? reservations = null,
        Action<ServiceCollection>? configure = null)
    {
        ServiceCollection services = new();

        services.AddScoped(_ => _fixture.CreateContext(_dbPath));

        services.AddScoped<IBatchRepository, BatchRepository>();

        services.AddScoped<IUploadedFileRepository, UploadedFileRepository>();

        BatchAdmissionBlobs blobs = new(_blobStore, gate);

        services.AddSingleton<IEncryptedBlobStore>(blobs);

        FakeIntelligenceProvider provider = new() { NextText = "ok", NextFinishReason = "stop" };

        services.AddSingleton<IArcanumIntelligenceProvider>(provider);

        services.AddSingleton<IBatchRecoveryService>(new NoOpBatchRecoveryService());

        if (writer is not null) { services.AddSingleton(writer); }

        if (reservations is not null) { services.AddSingleton(reservations); }

        configure?.Invoke(services);

        ServiceProvider root = services.BuildServiceProvider();

        BatchAdmissionScopes scopes = new(root.GetRequiredService<IServiceScopeFactory>(), gate);

        ArcanumSettings settings = new()
        {
            Execution = new ExecutionSettings { MaxConcurrentBatches = 1, MaxConcurrentRequestsPerBatch = 2 },
            Providers = [new ProviderSettings
            {
                Name = "test", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost",
                Models = [new ModelEntry("m")],
            }],
        };

        BatchProcessingService service = new(scopes, new TestOptionsMonitor<ArcanumSettings>(settings),
            root, gate, NullLogger<BatchProcessingService>.Instance);

        return new AdmissionService(root, service, scopes, blobs, provider);
    }

    private async Task<BatchRecord> SeedAdmissionBatchAsync(int records, bool blanks = false) =>
        await SeedAdmissionBatchAsync((blanks ? new string(' ', BatchJsonlRecordReader.InMemoryByteLimit + 1) + "\n" : "")
            + string.Concat(Enumerable.Range(1, records).Select(index => (blanks ? "\n \t\r\n" : "") + BatchLine($"req-{index}"))));

    private async Task<BatchRecord> SeedAdmissionBatchAsync(string input)
    {
        Guid inputFile = await SeedInputFileAsync(input);

        BatchRecord batch = new(Guid.NewGuid(), inputFile, "/v1/chat/completions", BatchStatuses.Validating,
            DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch);

        return batch;
    }

    private static string BatchLine(string id, string content = "hi") =>
        $"{{\"custom_id\":\"{id}\",\"method\":\"POST\",\"url\":\"/v1/chat/completions\",\"body\":{{\"model\":\"m\",\"messages\":[{{\"role\":\"user\",\"content\":\"{content}\"}}]}}}}\n";

    private sealed record AdmissionService(ServiceProvider Root, BatchProcessingService Service,
        BatchAdmissionScopes Scopes, BatchAdmissionBlobs Blobs, FakeIntelligenceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Service.Dispose();

            await Root.DisposeAsync();
        }
    }
}
