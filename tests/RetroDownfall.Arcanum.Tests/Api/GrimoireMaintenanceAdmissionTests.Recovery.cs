using System.Data;

using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GrimoireMaintenanceAdmissionTests
{
    [SkippableTheory]
    [InlineData(GrimoireTransitionEntryPoint.DirectCovenantReset)]
    [InlineData(GrimoireTransitionEntryPoint.StandaloneFactoryReset)]
    public async Task Parked_authenticated_transition_recovers_before_second_host_readiness(
        GrimoireTransitionEntryPoint entryPoint)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(entryPoint, () => AssertTwoHostRecoveryAsync(entryPoint));
    }

    [SkippableFact]
    public async Task Authenticated_writer_restore_failure_keeps_terminal_evidence_but_refuses_readiness()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            () => AssertTwoHostRecoveryAsync(
                GrimoireTransitionEntryPoint.DirectCovenantReset,
                failDisclosureWriterRestore: true));
    }

    private static async Task AssertTwoHostRecoveryAsync(
        GrimoireTransitionEntryPoint entryPoint,
        bool failDisclosureWriterRestore = false)
    {
        OneShotOutcomeFault fault = new(
            CovenantErasureFaultBoundary.AfterPhaseBegin,
            CovenantResetPhase.CanonicalApplied);

        await using RestartableArcanumProfileFixture profile = new();

        GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, faultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        await SeedOutcomeDatasetAsync(first, timeout.Token);

        OutcomeDatasetSnapshot before = await CaptureOutcomeDatasetAsync(first, timeout.Token);

        long firstOpenGeneration = first.Admission.CurrentGeneration;

        DataRetentionPlan plan = await first.PlanAsync(entryPoint);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage apply = first.CreateApplyRequest(entryPoint, plan.PlanId, requestedOperationId);

        using HttpResponseMessage response = await first.Client.SendAsync(apply, timeout.Token);

        Assert.True(fault.Fired);

        RecordedMaintenancePublication parked = first.Journal.Publications.Last();

        Guid operationId = parked.Payload.Binding.OperationId;

        await AssertTransitionFailureAsync(response, ErrorCodes.Covenant.ErasureIncomplete,
            operationId, entryPoint, timeout.Token);

        RecordedMaintenanceTransition attention = first.Operations.Transitions.Last(transition =>
            transition.OperationId == operationId);

        RecordedMaintenanceCheckpoint checkpoint = first.Operations.Checkpoints.Last(item =>
            item.OperationId == operationId);

        RecordedMaintenanceOperationRead bindingOperation = AssertInitialBindingOperation(first, operationId);

        AssertOutcomeLaunch(entryPoint, checkpoint.Version, checkpoint.Payload!.Value.AsSpan(), parked,
            requestedOperationId, bindingOperation);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, attention.State);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, attention.TerminalErrorCode);

        Assert.Equal(GrimoireOfflineTransitionState.KeepClosed, parked.Payload.Lifecycle.State);

        Assert.Null(parked.Payload.Binding.ParentReceiptBindingDigest);

        Assert.True(File.Exists(parked.Raw.Location.JournalPath));

        byte[] journalKeyFingerprint = ObservingMaintenanceJournal.JournalKeyFingerprint(
            profile.CredentialStore, parked.Raw.Location.ProfileNamespace);

        string masterApiKey = Assert.IsType<string>(await first.Factory.Services
            .GetRequiredService<ISecretStore>().GetApiKeyAsync());

        Guid installationId = parked.Raw.Envelope.InstallationId;

        OutcomeDatasetSnapshot parkedFiles = await CaptureManagedFilesAsync(first, before.Rows);

        AssertManagedFilesEqual(before, parkedFiles, entryPoint);

        await AssertParkedRefusalAndDeferralAsync(first, firstOpenGeneration + 1, timeout.Token, entryPoint);

        GrimoireMaintenanceAdmissionObserver firstAdmission = first.Admission;

        Task<long> firstOpenWaiter = firstAdmission.WaitForNextOpenGenerationAsync(
            firstAdmission.CurrentGeneration, timeout.Token);

        await first.DisposeAsync();

        Assert.False(firstOpenWaiter.IsCompleted);

        Assert.True(File.Exists(parked.Raw.Location.JournalPath));

        MaintenanceAdoptionObservation adoption = new() { PauseAfterAcquisition = true };

        RecoveryHostStartupObservation startup = new()
        {
            FailDisclosureWriterRestore = failDisclosureWriterRestore,
        };

        System.Collections.Concurrent.ConcurrentQueue<string> prematureReadiness = new();

        startup.Journal.AfterStep = step =>
        {
            if (startup.Readiness.IsReady || startup.FinalHostedServiceStarted.IsCompleted)
            {
                prematureReadiness.Enqueue(step);
            }
        };

        Task<GrimoireMaintenanceAdmissionHarness> starting = Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile, adoption, startup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        bool startupTransferred = false;

        try
        {
            Task adoptionReached = adoption.Checkpoint.WaitUntilReachedAsync();

            Task firstStartupBoundary = await Task.WhenAny(adoptionReached, starting).WaitAsync(timeout.Token);

            if (ReferenceEquals(firstStartupBoundary, starting))
            {
                try
                {
                    await starting;
                }
                catch (Exception failure)
                {
                    string load = startup.AuthorityLoad is { } loaded
                        ? $"{loaded.IsSuccess}:{loaded.Error.Code}:{loaded.Error.Message}"
                        : "not-called";

                    string consume = startup.AuthorityConsume is { } consumed
                        ? $"{consumed.IsSuccess}:{consumed.Error.Code}:{consumed.Error.Message}"
                        : "not-called";

                    GrimoireMaintenanceAdmissionObserver? failedAdmission = startup.AdmissionOrNull;

                    string grimoire = failedAdmission is null
                        ? "not-resolved"
                        : $"generation={failedAdmission.CurrentGeneration},"
                            + $"closing={failedAdmission.ClosingOwners.Count},"
                            + $"closed={failedAdmission.ClosedLeases.Count}";

                    throw new Xunit.Sdk.XunitException(
                        $"Recovery failed before adoption. CovenantPermitted={startup.CovenantPermitted}; "
                        + $"Load={load}; Consume={consume}; AdoptionCalls={adoption.Calls}; "
                        + $"Grimoire={grimoire}; {failure}");
                }

                throw new Xunit.Sdk.XunitException(
                    "The recovery host completed before maintenance adoption paused.");
            }

            await adoptionReached;

            try
            {
                Assert.False(starting.IsCompleted);

                Assert.False(startup.Readiness.IsReady);

                Assert.False(startup.FinalHostedServiceStarted.IsCompleted);

                IHostProcessToolsRuntimePolicy actualHostTools = Assert.IsAssignableFrom<
                    IHostProcessToolsRuntimePolicy>(startup.ActualHostToolsPolicy);

                Assert.False(actualHostTools.IsPublished);

                Assert.False(actualHostTools.CovenantPermitted);

                LongRunningOperationLeaseResult adopted =
                    Assert.IsType<LongRunningOperationLeaseResult>(adoption.Result);

                Assert.True(adopted.Acquired);

                Assert.Equal(operationId, adopted.Operation!.Id);

                Assert.Equal(checkpoint.Version, adopted.Operation.CheckpointVersion);

                Assert.Equal(checkpoint.Payload.Value.ToArray(), adopted.Operation.CheckpointPayload);

                Assert.True(adopted.Operation.Revision > attention.ExpectedRevision);

                Assert.NotEqual(attention.OwnerId, adopted.Operation.LeaseOwner);

                GrimoireMaintenanceAdmissionObserver secondAdmission = startup.Admission;

                Assert.Equal(startup.InitialOpenGeneration + 1, secondAdmission.CurrentGeneration);

                Assert.False(secondAdmission.TryAcquireRequestLease(GrimoireRequestKind.Finite, out _));

                Assert.False(secondAdmission.TryAcquireWorkLease(
                    GrimoireWorkKind.SessionAttachmentIndexing,
                    out _));

                using SqliteConnection refused = new();

                Assert.Throws<GrimoireMaintenanceUnavailableException>(
                    () => secondAdmission.AcquireOrdinaryOpen(refused));

                var covenant = await startup.Covenant.AcquireInstallationReadAsync(timeout.Token);

                Assert.True(covenant.IsFailure);

                Assert.Equal(0, startup.Chat?.Calls ?? 0);

                Assert.Equal(0, startup.Weave?.Calls ?? 0);

                Assert.Empty(startup.Workers?.Scopes ?? []);

                Assert.Empty(secondAdmission.Effects);

                Assert.Equal(new OrdinaryMutationCounters(), startup.OrdinaryMutations.Snapshot);

                Assert.DoesNotContain(startup.Journal.Steps, step =>
                    step.StartsWith("anchor:closed", StringComparison.Ordinal)
                    || step.StartsWith("file:retiring", StringComparison.Ordinal));

                Assert.True(File.Exists(parked.Raw.Location.JournalPath));
            }
            finally
            {
                adoption.Checkpoint.Release();
            }

            GrimoireMaintenanceAdmissionHarness second;

            try
            {
                second = await starting.WaitAsync(timeout.Token);

                startupTransferred = true;
            }
            catch (Exception failure)
            {
                if (failDisclosureWriterRestore)
                {
                    Assert.IsType<GrimoireDatabaseUnavailableException>(failure);

                    Assert.False(startup.Readiness.IsReady);

                    Assert.False(startup.FinalHostedServiceStarted.IsCompleted);

                    RecordedMaintenanceTransition terminal = startup.Operations.Transitions.Last(item =>
                        item.OperationId == operationId
                        && item.State == LongRunningOperationState.Completed);

                    Assert.Null(terminal.TerminalErrorCode);

                    Assert.False(File.Exists(parked.Raw.Location.JournalPath));

                    Assert.Contains(startup.Journal.Steps, static step =>
                        step.StartsWith("file:delete", StringComparison.Ordinal));

                    Assert.Equal(
                        [CovenantExclusiveLeaseDisposition.CommitAndReopen],
                        Assert.Single(startup.Admission.ClosedLeases).Dispositions);

                    Assert.False(firstOpenWaiter.IsCompleted);

                    return;
                }

                string publications = string.Join(",", startup.Journal.Publications.Select(item =>
                    $"{item.Payload.Lifecycle.State}/{item.Payload.LastCompletedPhase}/{item.Payload.InFlightPhase}"));

                string transitions = string.Join(",", startup.Operations.Transitions.Select(item =>
                    $"{item.State}/{item.TerminalErrorCode}/{item.ExpectedRevision}"));

                string logs = string.Join(" | ", startup.HostLogs.Entries.Select(item =>
                    $"{item.Level}:{item.Category}:{item.Message}"));

                string publish = startup.Journal.PublishResult is { } publishResult
                    ? publishResult.IsSuccess
                        ? "success"
                        : $"{publishResult.Error.Code}/{publishResult.Error.Message}"
                    : "not-called";

                CovenantRuntimeGenerationState? runtime = startup.Journal.RuntimeBeforePublish;

                CovenantVerifiedCandidateState? candidate = startup.Journal.VerifiedCandidate;

                string projection = runtime is null || candidate is null
                    ? "unavailable"
                    : $"availability={runtime.Availability.Generation}/{runtime.Availability.DatasetGeneration}/"
                        + $"{runtime.Availability.Canonical}/{runtime.Availability.Accelerator}; "
                        + $"authority={candidate.Authority.InstallationIdentity}/{candidate.Authority.AuthorityEpoch}/"
                        + $"{candidate.Authority.CurrentMasterKeyVersion}/{candidate.Authority.RecoveryEnvelopeEpoch}/"
                        + $"{candidate.Authority.HostToolsState}/{candidate.Authority.TransitionId ?? "null"}; "
                        + $"dataset={candidate.Dataset.DatasetGeneration}/{candidate.Dataset.CanonicalSearchSequence}/"
                        + $"{candidate.Dataset.AppliedDatasetGeneration}/{candidate.Dataset.AppliedSearchSequence}/"
                        + $"{candidate.Dataset.AppliedCampaignDeletionSequence}/{candidate.Dataset.EnvelopeKeyEpoch}";

                throw new Xunit.Sdk.XunitException(
                    $"Recovery failed after adoption. Publish={publish}; Projection={projection}; "
                    + $"Publications={publications}; Transitions={transitions}; "
                    + $"Steps={string.Join(',', startup.Journal.Steps)}; Logs={logs}; {failure}");
            }

            await using (second)
            {
                Assert.True(startup.Readiness.IsReady, "host-2 readiness was not published");

                Assert.True(startup.FinalHostedServiceStarted.IsCompletedSuccessfully,
                    "the final hosted-service sentinel did not start");

                Assert.Empty(prematureReadiness);

                Assert.False(firstOpenWaiter.IsCompleted);

                Assert.Equal(firstOpenGeneration + 1, firstAdmission.CurrentGeneration);

                await using AsyncServiceScope inspection = second.Factory.Services.CreateAsyncScope();

                LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
                    await inspection.ServiceProvider
                        .GetRequiredService<ILongRunningOperationStore>()
                        .GetAsync(operationId, timeout.Token));

                Assert.Equal(LongRunningOperationState.Completed, operation.State);

                Assert.Equal(checkpoint.Version, operation.CheckpointVersion);

                Assert.Equal(checkpoint.Payload.Value.ToArray(), operation.CheckpointPayload);

                Assert.Null(operation.TerminalErrorCode);

                Assert.NotNull(operation.CompletedAt);

                Assert.Equal(
                    [CovenantExclusiveLeaseDisposition.CommitAndReopen],
                    Assert.Single(second.Admission.ClosedLeases).Dispositions);

                Assert.Equal(startup.InitialOpenGeneration + 1, second.Admission.CurrentGeneration);

                Assert.True(CryptographicOperations.FixedTimeEquals(
                    journalKeyFingerprint,
                    ObservingMaintenanceJournal.JournalKeyFingerprint(
                        profile.CredentialStore,
                        parked.Raw.Location.ProfileNamespace)),
                    "the journal-key fingerprint changed");

                Assert.Equal(masterApiKey, await second.Factory.Services
                    .GetRequiredService<ISecretStore>().GetApiKeyAsync());

                await AssertRecoveredCatalogAsync(
                    second,
                    before,
                    operation,
                    installationId,
                    timeout.Token,
                    entryPoint);

                await AssertOpenAdmissionAsync(
                    second,
                    startup.InitialOpenGeneration + 1,
                    timeout.Token,
                    entryPoint);
            }
        }
        finally
        {
            // Idempotent even when the assertion block already released it. This ownership begins
            // immediately after the LongRunning task is created, so a timeout before the first
            // WhenAny cannot strand a later adoption on the test's artificial barrier.
            adoption.Checkpoint.Release();

            if (!startupTransferred)
            {
                try
                {
                    Task ownerBoundary = await Task.WhenAny(startup.HarnessCreated, starting);

                    if (ReferenceEquals(ownerBoundary, startup.HarnessCreated))
                    {
                        GrimoireMaintenanceAdmissionHarness owned = await startup.HarnessCreated;

                        await owned.DisposeAsync();
                    }
                }
                catch
                {
                    // StartHostAsync also owns the same idempotent failure cleanup.
                }
                finally
                {
                    try
                    {
                        _ = await starting;
                    }
                    catch
                    {
                        // The owned factory is disposed; awaiting observes the startup failure.
                    }
                }
            }
        }
    }

    private static async Task AssertRecoveredCatalogAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        OutcomeDatasetSnapshot before,
        LongRunningOperation operation,
        Guid installationId,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Result<GrimoireOfflineTransitionLaunchBinding> projected = GrimoireOfflineTransitionLaunch
            .FromCommittedCheckpoint(operation.CheckpointVersion, operation.CheckpointPayload!);

        Assert.True(projected.IsSuccess, $"checkpoint projection: {projected.Error.Message}");

        await AssertCommittedRetirementAsync(harness, operation, projected.Value.TargetDatasetGeneration,
            cancellationToken);

        var fresh = await harness.Factory.Services.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken);

        Assert.True(fresh.IsSuccess, $"fresh catalog open: {fresh.Error.Message}");

        await using (fresh.Value)
        {
            Assert.Equal(ConnectionState.Open, fresh.Value.Connection.State);

            if (entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset)
            {
                Result<CovenantOfflineTransitionLaunchV4> decoded = CovenantRecoveryCheckpointCodec
                    .DecodeCovenantOfflineTransitionLaunch(operation.CheckpointPayload!);

                Assert.True(decoded.IsSuccess, decoded.Error.Message);

                await AssertRecoveredCovenantCatalogAsync(
                    fresh.Value.Connection,
                    decoded.Value,
                    installationId,
                    cancellationToken);
            }
            else
            {
                Result<DataRetentionFactoryTransitionLaunchV2> decoded = CovenantRecoveryCheckpointCodec
                    .DecodeDataRetentionFactoryTransitionLaunch(operation.CheckpointPayload!);

                Assert.True(decoded.IsSuccess, decoded.Error.Message);

                await AssertFactoryCatalogAsync(fresh.Value.Connection, decoded.Value, cancellationToken);
            }
        }

        if (entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset)
        {
            OutcomeDatasetSnapshot after = await CaptureOutcomeDatasetAsync(harness, cancellationToken);

            AssertManagedFilesEqual(before, after, entryPoint);

            string[] erasedTables =
            [
                "covenant_state",
                "covenant_entries",
                "covenant_versions",
                "covenant_heads",
                "artifact_sensitivity",
                "managed_file_write_intents",
                // Hosted indexing legitimately starts after readiness and may populate these
                // projections before the post-start snapshot. The barrier above, not this later
                // catalog read, proves recovery itself produced no ordinary effects.
                "session_attachment_index_state",
                "session_attachment_chunks",
                "session_attachment_embeddings",
            ];

            foreach (string retained in OutcomeTables.Except(erasedTables, StringComparer.Ordinal))
            {
                Assert.Equal(
                    before.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)),
                    after.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)));
            }
        }
        else
        {
            Assert.All(before.ManagedFiles.Keys, path => Assert.False(File.Exists(path), path));
        }
    }

    private static async Task AssertRecoveredCovenantCatalogAsync(
        SqliteConnection connection,
        CovenantOfflineTransitionLaunchV4 launch,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT st.DatasetGeneration, st.AcceleratorEpoch, st.KeyReclamationEpoch, st.EnvelopeKeyEpoch,
                auth.InstallationIdentity,
                (SELECT COUNT(*) FROM covenant_entries),
                (SELECT COUNT(*) FROM covenant_versions),
                (SELECT COUNT(*) FROM covenant_heads),
                (SELECT COUNT(*) FROM artifact_sensitivity),
                (SELECT COUNT(*) FROM managed_file_write_intents)
            FROM covenant_state st CROSS JOIN covenant_authority_state auth
            WHERE st.StateKey = 1 AND auth.StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken));

        Assert.Equal(launch.TargetDatasetGeneration, new Guid((byte[])reader.GetValue(0)));

        Assert.Equal(launch.TargetEpochs, new CovenantOfflineTransitionEpochsV1(
            (ulong)reader.GetInt64(1),
            (ulong)reader.GetInt64(2),
            (ulong)reader.GetInt64(3)));

        Assert.Equal(installationId.ToString("D").ToUpperInvariant(), reader.GetString(4));

        for (int column = 5; column < 10; column++)
        {
            Assert.Equal(0L, reader.GetInt64(column));
        }

        Assert.False(await reader.ReadAsync(cancellationToken));
    }
}
