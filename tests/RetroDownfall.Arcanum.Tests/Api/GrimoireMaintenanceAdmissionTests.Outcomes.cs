using System.Data;

using System.Data.Common;

using System.Globalization;

using System.Net;

using System.Net.Http.Json;

using System.Security.Cryptography;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GrimoireMaintenanceAdmissionTests
{
    private const string OutcomeFailureMessage = "The Covenant reset did not reach a committed reopen disposition.";

    private static readonly string[] OutcomeTables =
    [
        "covenant_state",

        "covenant_entries",

        "covenant_versions",

        "covenant_heads",

        "artifact_sensitivity",

        "managed_file_write_intents",

        "Sessions",

        "Entries",

        "SessionAttachments",

        "session_attachment_index_state",

        "session_attachment_chunks",

        "session_attachment_embeddings",

        "BillableOperations",

        "InferenceRuns",

        "UnseenServantWatermarks",
    ];

    [SkippableTheory]
    [InlineData(GrimoireTransitionEntryPoint.DirectCovenantReset)]
    [InlineData(GrimoireTransitionEntryPoint.StandaloneFactoryReset)]
    public async Task Authenticated_transition_rolls_back_and_reopens_only_when_pre_effect_is_proven(
        GrimoireTransitionEntryPoint entryPoint)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        OneShotOutcomeFault fault = new(
            CovenantErasureFaultBoundary.BeforePhaseBegin,
            CovenantResetPhase.CanonicalApplied);

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, faultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        await SeedOutcomeDatasetAsync(harness, timeout.Token);

        OutcomeDatasetSnapshot before = await CaptureOutcomeDatasetAsync(harness, timeout.Token);

        long originalGeneration = harness.Admission.CurrentGeneration;

        DataRetentionPlan plan = await harness.PlanAsync(entryPoint);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage apply = harness.CreateApplyRequest(entryPoint, plan.PlanId, requestedOperationId);

        using HttpResponseMessage response = await harness.Client.SendAsync(apply, timeout.Token);

        Assert.True(fault.Fired, entryPoint.ToString());

        RecordedMaintenancePublication terminal = harness.Journal.Publications.Last();

        Guid operationId = terminal.Payload.Binding.OperationId;

        await AssertTransitionFailureAsync(
            response,
            ErrorCodes.Covenant.ErasureIncomplete,
            operationId,
            entryPoint,
            timeout.Token);

        GrimoireOwnerObservation grimoire = Assert.Single(harness.Admission.ClosedLeases);

        Assert.Equal([CovenantExclusiveLeaseDisposition.RollbackAndReopen], grimoire.Dispositions);

        await using AsyncServiceScope inspection = harness.Factory.Services.CreateAsyncScope();

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inspection.ServiceProvider
            .GetRequiredService<ILongRunningOperationStore>().GetAsync(operationId, timeout.Token));

        Assert.Equal(LongRunningOperationState.Failed, operation.State);

        Assert.Equal("grimoire.offline_transition_not_applied", operation.TerminalErrorCode);

        Assert.NotNull(operation.CompletedAt);

        AssertOutcomeLaunch(entryPoint, operation.CheckpointVersion, operation.CheckpointPayload!, terminal, requestedOperationId);

        Assert.Equal(originalGeneration + 1, harness.Admission.CurrentGeneration);

        Assert.Equal(originalGeneration + 1, grimoire.Generation);

        Assert.Equal(operationId, grimoire.Owner.OperationId);

        Assert.Equal(terminal.Payload.Binding.EffectDigest, grimoire.Owner.EffectDigest);

        Assert.Equal(ExpectedOperation(entryPoint), grimoire.Owner.Operation);

        Assert.Equal(grimoire.Owner, Assert.Single(harness.Admission.ClosingOwners).Owner);

        Assert.Equal(originalGeneration, Assert.Single(harness.Admission.ClosingOwners).Generation);

        Assert.Equal(1, grimoire.Disposals);

        Assert.Equal(1, Assert.Single(harness.Admission.ClosingOwners).Disposals);

        await AssertRollbackRetirementAsync(harness, operation, terminal, timeout.Token, entryPoint);

        await AssertNoParentReceiptAsync(harness, terminal, timeout.Token, entryPoint);

        OutcomeDatasetSnapshot after = await CaptureOutcomeDatasetAsync(harness, timeout.Token);

        Assert.Equal(before.Rows, after.Rows);

        AssertManagedFilesEqual(before, after, entryPoint);

        await AssertOpenAdmissionAsync(harness, originalGeneration + 1, timeout.Token, entryPoint);
    }

    [SkippableTheory]
    [InlineData(GrimoireTransitionEntryPoint.DirectCovenantReset)]
    [InlineData(GrimoireTransitionEntryPoint.StandaloneFactoryReset)]
    public async Task Authenticated_transition_keeps_both_gates_closed_when_effect_outcome_is_uncertain(
        GrimoireTransitionEntryPoint entryPoint)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        OneShotOutcomeFault fault = new(
            CovenantErasureFaultBoundary.AfterPhaseBegin,
            CovenantResetPhase.CanonicalApplied);

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, faultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        await SeedOutcomeDatasetAsync(harness, timeout.Token);

        OutcomeDatasetSnapshot before = await CaptureOutcomeDatasetAsync(harness, timeout.Token);

        string[] indexingBefore = await OrdinaryIndexingSnapshotAsync(harness, harness.IndexingAttachmentB.Id);

        Assert.Contains("session_attachment_index_state:count:0", indexingBefore);

        long originalGeneration = harness.Admission.CurrentGeneration;

        DataRetentionPlan plan = await harness.PlanAsync(entryPoint);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage apply = harness.CreateApplyRequest(entryPoint, plan.PlanId, requestedOperationId);

        using HttpResponseMessage response = await harness.Client.SendAsync(apply, timeout.Token);

        Assert.True(fault.Fired, entryPoint.ToString());

        RecordedMaintenancePublication parked = harness.Journal.Publications.Last();

        Guid operationId = parked.Payload.Binding.OperationId;

        await AssertTransitionFailureAsync(
            response,
            ErrorCodes.Covenant.ErasureIncomplete,
            operationId,
            entryPoint,
            timeout.Token);

        Assert.Equal(originalGeneration + 1, harness.Admission.CurrentGeneration);

        GrimoireOwnerObservation grimoire = Assert.Single(harness.Admission.ClosedLeases);

        Assert.Equal([CovenantExclusiveLeaseDisposition.KeepClosed], grimoire.Dispositions);

        Assert.Equal(operationId, grimoire.Owner.OperationId);

        Assert.Equal(ExpectedOperation(entryPoint), grimoire.Owner.Operation);

        Assert.Equal(parked.Payload.Binding.EffectDigest, grimoire.Owner.EffectDigest);

        Assert.Equal(originalGeneration + 1, grimoire.Generation);

        GrimoireOwnerObservation closing = Assert.Single(harness.Admission.ClosingOwners);

        Assert.Equal(grimoire.Owner, closing.Owner);

        Assert.Equal(originalGeneration, closing.Generation);

        Assert.Equal(1, grimoire.Disposals);

        Assert.Equal(1, closing.Disposals);

        Assert.Equal(GrimoireOfflineTransitionState.KeepClosed, parked.Payload.Lifecycle.State);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, parked.Payload.LastCompletedPhase);

        Assert.Equal(CovenantResetPhase.CanonicalApplied, parked.Payload.InFlightPhase);

        Assert.Null(parked.Payload.Lifecycle.ReconciliationEvidence);

        GrimoireOfflineTransitionBlocker blocker = Assert.IsType<GrimoireOfflineTransitionBlocker>(
            parked.Payload.Lifecycle.Blocker);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, blocker.ErrorCode);

        Assert.Equal(GrimoireOfflineTransitionState.Applying, blocker.ResumeState);

        Assert.True(blocker.ResolutionBindingDigest.IsValid);

        Assert.True(blocker.ExpectedStateDigest.IsValid);

        Assert.NotEqual(blocker.ResolutionBindingDigest, blocker.ExpectedStateDigest);

        Assert.True(File.Exists(parked.Raw.Location.JournalPath), entryPoint.ToString());

        IOsCredentialStore credentials = harness.Factory.Services.GetRequiredService<IOsCredentialStore>();

        var anchor = new GrimoireOfflineTransitionJournalAnchorStore(credentials).Read(parked.Raw.Location);

        Assert.True(anchor.IsSuccess, $"{entryPoint}: {anchor.Error.Message}");

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, anchor.Value!.State);

        Assert.True(CryptographicOperations.FixedTimeEquals(
            harness.Journal.InitialJournalKeyFingerprint!,
            ObservingMaintenanceJournal.JournalKeyFingerprint(credentials, parked.Raw.Location.ProfileNamespace)));

        Assert.DoesNotContain(harness.Journal.Steps, step => step.StartsWith("anchor:closed", StringComparison.Ordinal));

        Assert.DoesNotContain(harness.Journal.Steps, step => step.StartsWith("file:retiring", StringComparison.Ordinal));

        RecordedMaintenanceTransition attention = harness.Operations.Transitions.Last(transition => transition.OperationId == operationId);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, attention.State);

        Assert.Equal(ErrorCodes.Covenant.ErasureIncomplete, attention.TerminalErrorCode);

        RecordedMaintenanceCheckpoint checkpoint = harness.Operations.Checkpoints.Last(item => item.OperationId == operationId);

        Assert.NotNull(checkpoint.Payload);

        AssertOutcomeLaunch(entryPoint, checkpoint.Version, checkpoint.Payload!.Value.AsSpan(), parked, requestedOperationId);

        await AssertNoParentReceiptAsync(harness, parked, timeout.Token, entryPoint);

        await AssertParkedRefusalAndDeferralAsync(harness, originalGeneration + 1, timeout.Token, entryPoint);

        OutcomeDatasetSnapshot managedAfter = await CaptureManagedFilesAsync(harness, before.Rows);

        AssertManagedFilesEqual(before, managedAfter, entryPoint);

        await using (AsyncServiceScope covenantScope = harness.Factory.Services.CreateAsyncScope())
        {
            var covenant = await covenantScope.ServiceProvider.GetRequiredService<ICovenantOperationGate>()
                .AcquireInstallationReadAsync(timeout.Token);

            Assert.True(covenant.IsFailure, entryPoint.ToString());
        }

        GrimoireMaintenanceAdmissionObserver retainedAdmission = harness.Admission;

        await harness.DisposeAsync();

        Assert.Equal(originalGeneration + 1, retainedAdmission.CurrentGeneration);

        Assert.False(retainedAdmission.TryAcquireRequestLease(GrimoireRequestKind.Finite, out var afterHostRequest));

        Assert.Null(afterHostRequest);

        Assert.False(retainedAdmission.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out var afterHostWork));

        Assert.Null(afterHostWork);
    }

    private static async Task SeedOutcomeDatasetAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        CancellationToken cancellationToken)
    {
        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.AttachAsync(
            new DesignTimeGrimoireConnectionFactory(harness.Factory.Services.GetRequiredService<IGrimoireDbPassphraseSource>()),
            harness.Factory.Services.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
            harness.Drain,
            cancellationToken);

        await fixture.SeedAcceptanceStateAsync(cancellationToken);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", cancellationToken));

        Assert.Equal(1, await fixture.CountAsync("artifact_sensitivity", cancellationToken));
    }

    private static async Task<OutcomeDatasetSnapshot> CaptureOutcomeDatasetAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync(cancellationToken);

        List<string> rows = [];

        foreach (string table in OutcomeTables)
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1;";

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            int count = 0;

            while (await reader.ReadAsync(cancellationToken))
            {
                string[] values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => $"{reader.GetName(index)}={OutcomeValue(reader, index)}")
                    .ToArray();

                rows.Add($"{table}:{string.Join('|', values)}");

                count++;
            }

            rows.Add($"{table}:count={count}");
        }

        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        foreach (SessionAttachmentRecord attachment in new[]
        {
            harness.DownloadAttachment,

            harness.IndexingAttachmentA,

            harness.IndexingAttachmentB,
        })
        {
            string path = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, attachment.RelativePath));

            files[path] = await File.ReadAllBytesAsync(path, cancellationToken);
        }

        return new(rows.ToArray(), files);
    }

    private static async Task<OutcomeDatasetSnapshot> CaptureManagedFilesAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        string[] retainedRows)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

        foreach (SessionAttachmentRecord attachment in new[]
        {
            harness.DownloadAttachment,

            harness.IndexingAttachmentA,

            harness.IndexingAttachmentB,
        })
        {
            string path = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, attachment.RelativePath));

            files[path] = await File.ReadAllBytesAsync(path);
        }

        return new(retainedRows, files);
    }

    private static string OutcomeValue(DbDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return "null";
        }

        object value = reader.GetValue(index);

        return value is byte[] bytes
            ? $"bytes:{Convert.ToHexString(bytes)}"
            : $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}";
    }

    private static void AssertManagedFilesEqual(
        OutcomeDatasetSnapshot expected,
        OutcomeDatasetSnapshot actual,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Assert.Equal(expected.ManagedFiles.Keys.Order(StringComparer.Ordinal), actual.ManagedFiles.Keys.Order(StringComparer.Ordinal));

        foreach ((string path, byte[] bytes) in expected.ManagedFiles)
        {
            Assert.True(actual.ManagedFiles.TryGetValue(path, out byte[]? observed), $"{entryPoint}: missing managed file {path}");

            Assert.Equal(bytes, observed);
        }
    }

    private static async Task AssertTransitionFailureAsync(
        HttpResponseMessage response,
        string expectedCode,
        Guid operationId,
        GrimoireTransitionEntryPoint entryPoint,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        string raw = await response.Content.ReadAsStringAsync(cancellationToken);

        var body = JsonSerializer.Deserialize(raw, ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess, entryPoint.ToString());

        Assert.Null(body.Data);

        Assert.Equal(expectedCode, body.Error!.Value.Code);

        Assert.Equal(OutcomeFailureMessage, body.Error.Value.Message);

        string lower = raw.ToLowerInvariant();

        Assert.DoesNotContain(ArcanumPaths.GrimoireDirectory.ToLowerInvariant(), lower);

        Assert.DoesNotContain("owner", lower);

        Assert.DoesNotContain("canonicalapplied", lower);

        Assert.DoesNotContain("generation", lower);

        Assert.DoesNotContain("sqlite", lower);

        Assert.DoesNotContain("exception", lower);

        Assert.DoesNotContain(operationId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOutcomeLaunch(
        GrimoireTransitionEntryPoint entryPoint,
        int checkpointVersion,
        ReadOnlySpan<byte> checkpointPayload,
        RecordedMaintenancePublication publication,
        Guid requestedOperationId)
    {
        Assert.Equal(publication.Raw.Envelope.OperationId, publication.Payload.Binding.OperationId);

        Assert.Null(publication.Payload.Binding.ParentReceiptBindingDigest);

        if (entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset)
        {
            Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, checkpointVersion);

            var decoded = CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(checkpointPayload);

            Assert.True(decoded.IsSuccess, $"{entryPoint}: {decoded.Error.Message}");

            Assert.Equal(publication.Payload.Binding.OperationId, decoded.Value.OperationId);

            Assert.Equal(CovenantExclusiveOperation.CovenantReset, decoded.Value.Operation);

            Assert.Equal(CovenantOfflineTransitionLaunchV4.CurrentVersion, decoded.Value.Version);
        }
        else
        {
            Assert.Equal(DataRetentionFactoryTransitionLaunchV2.CurrentVersion, checkpointVersion);

            var decoded = CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(checkpointPayload);

            Assert.True(decoded.IsSuccess, $"{entryPoint}: {decoded.Error.Message}");

            Assert.Equal(publication.Payload.Binding.OperationId, decoded.Value.OperationId);

            Assert.Equal(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, decoded.Value.Operation);

            Assert.Equal(DataRetentionFactoryTransitionLaunchV2.CurrentVersion, decoded.Value.Version);

            Assert.NotEqual(requestedOperationId, decoded.Value.OperationId);
        }
    }

    private static CovenantExclusiveOperation ExpectedOperation(GrimoireTransitionEntryPoint entryPoint) =>
        entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset
            ? CovenantExclusiveOperation.CovenantReset
            : CovenantExclusiveOperation.HealthyCatalogFactoryErasure;

    private static async Task AssertRollbackRetirementAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        LongRunningOperation operation,
        RecordedMaintenancePublication terminal,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Assert.Equal(GrimoireOfflineTransitionState.RetirementPending, terminal.Payload.Lifecycle.State);

        Assert.Equal(CovenantResetPhase.InventoryPrepared, terminal.Payload.LastCompletedPhase);

        Assert.Null(terminal.Payload.InFlightPhase);

        GrimoireOfflineTransitionReconciliationEvidence evidence = Assert.IsType<GrimoireOfflineTransitionReconciliationEvidence>(
            terminal.Payload.Lifecycle.ReconciliationEvidence);

        Assert.Equal(GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified, evidence.Step);

        Assert.Equal(GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen, evidence.CovenantDispositionIntent);

        Assert.Equal(
            GrimoireOfflineTransitionDatabaseReconciler.WinnerDigest(terminal.Payload.Binding, operation),
            evidence.DatabaseTerminalWinnerDigest);

        Assert.True(evidence.ParentReceiptNotRequired);

        Assert.Null(evidence.ParentReceiptDigest);

        Assert.True(evidence.LaneClosed);

        Assert.All(harness.Journal.DispositionsAtRetirementSteps, accepted => Assert.Equal(0, accepted));

        string[] retirement = harness.Journal.Steps.Where(step => step.StartsWith("anchor:closed", StringComparison.Ordinal)
            || step.StartsWith("file:retiring", StringComparison.Ordinal)
            || step is "file:delete-parent-flushed" or "file:absence-parent-flushed" or "file:absence-proved").ToArray();

        Assert.Equal(new[]
        {
            "anchor:closed-written",

            "anchor:closed-readback",

            "file:retiring-moved",

            "file:retiring-verified",

            "file:retiring-parent-flushed",

            "file:retiring-unlinked",

            "file:retiring-zero-link-verified",

            "file:delete-parent-flushed",

            "file:absence-parent-flushed",

            "file:absence-proved",
        }, retirement);

        Assert.False(File.Exists(terminal.Raw.Location.JournalPath), entryPoint.ToString());

        IOsCredentialStore credentials = harness.Factory.Services.GetRequiredService<IOsCredentialStore>();

        var anchor = new GrimoireOfflineTransitionJournalAnchorStore(credentials).Read(terminal.Raw.Location);

        Assert.True(anchor.IsSuccess, $"{entryPoint}: {anchor.Error.Message}");

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, anchor.Value!.State);

        Assert.Equal(terminal.Raw.Envelope.SlotEpoch, anchor.Value.SlotEpoch);

        Assert.Equal(terminal.Raw.Envelope.Revision, anchor.Value.Revision);

        Assert.True(CryptographicOperations.FixedTimeEquals(
            harness.Journal.InitialJournalKeyFingerprint!,
            ObservingMaintenanceJournal.JournalKeyFingerprint(credentials, terminal.Raw.Location.ProfileNamespace)));

        await Task.CompletedTask;
    }

    private static async Task AssertNoParentReceiptAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        RecordedMaintenancePublication publication,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Assert.Null(publication.Payload.Binding.ParentReceiptBindingDigest);

        var parent = await new InstallationResetActiveStore(
            publication.Raw.Location.GuardedDirectory,
            harness.Factory.Services.GetRequiredService<IOsCredentialStore>()).InspectAsync(cancellationToken);

        Assert.True(parent.IsSuccess, $"{entryPoint}: {parent.Error.Message}");

        Assert.Equal(InstallationResetActiveRecoveryOutcome.NoActiveRecord, parent.Value.Outcome);

        Assert.Null(parent.Value.Publication);
    }

    private static async Task AssertOpenAdmissionAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        long expectedGeneration,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Assert.True(harness.Admission.TryAcquireRequestLease(GrimoireRequestKind.Finite, out var request), entryPoint.ToString());

        await using (request!)
        {
            Assert.Equal(expectedGeneration, request!.Generation);
        }

        Assert.True(harness.Admission.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out var work), entryPoint.ToString());

        await using (work!)
        {
            Assert.Equal(expectedGeneration, work!.Generation);
        }

        using SqliteConnection connection = new();

        using (IGrimoireConnectionOpenTicket ticket = harness.Admission.AcquireOrdinaryOpen(connection))
        {
            Assert.Equal(expectedGeneration, ticket.Generation);

            ticket.MarkFailed();
        }

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        var covenant = await scope.ServiceProvider.GetRequiredService<ICovenantOperationGate>()
            .AcquireInstallationReadAsync(cancellationToken);

        Assert.True(covenant.IsSuccess, entryPoint.ToString());

        await covenant.Value.DisposeAsync();

        harness.Stats.Checkpoint.Release();

        using HttpRequestMessage requestMessage = harness.CreateObservedRequest(HttpMethod.Get, "/api/grimoire/stats");

        using HttpResponseMessage response = await harness.Client.SendAsync(requestMessage, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True((await response.Content.ReadFromJsonAsync(
            ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto,
            cancellationToken))!.IsSuccess);

        Assert.Equal(expectedGeneration, harness.StatsProbe.ObservedLease!.Generation);
    }

    private static async Task AssertParkedRefusalAndDeferralAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        long closedGeneration,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        int scopesBefore = harness.WorkerScopes.Scopes.Count;

        int effectsBefore = harness.Admission.Effects.Count;

        int weaveCallsBefore = harness.Weave.Calls;

        int weaveCompletedBefore = harness.Weave.Completed;

        int chatCallsBefore = harness.Chat.Calls;

        int chatCompletedBefore = harness.Chat.Completed;

        int failuresBefore = harness.IndexingLogs.Entries.Count(entry => entry.Level >= LogLevel.Warning);

        OrdinaryMutationCounters mutationsBefore = harness.OrdinaryMutations.Snapshot;

        Assert.Empty(harness.Indexing.DeferredRequests);

        SessionAttachmentIndexRequest request = new(
            harness.IndexingAttachmentB.Id,
            harness.SessionId,
            Attempt: 11);

        harness.IndexingLogs.ExpectDeferred(request.AttachmentId);

        Assert.True(harness.Indexing.TryEnqueue(request), entryPoint.ToString());

        await harness.IndexingLogs.WaitUntilDeferredAsync();

        SessionAttachmentIndexRequest retained = Assert.Single(harness.Indexing.DeferredRequests);

        Assert.Same(request, retained);

        Assert.Equal(11, retained.Attempt);

        Assert.Equal(0, harness.Indexing.QueueReader.Count);

        int refusedWorkAttempts = harness.Admission.WorkAttempts.Count(
            attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && !attempt.Acquired);

        Assert.True(refusedWorkAttempts >= 1, entryPoint.ToString());

        Assert.True(harness.Indexing.TryEnqueue(request with { Attempt = 19 }), entryPoint.ToString());

        Assert.Same(retained, Assert.Single(harness.Indexing.DeferredRequests));

        Assert.Equal(11, Assert.Single(harness.Indexing.DeferredRequests).Attempt);

        Assert.Equal(0, harness.Indexing.QueueReader.Count);

        Assert.Equal(refusedWorkAttempts, harness.Admission.WorkAttempts.Count(
            attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && !attempt.Acquired));

        int requestAttemptsBefore = harness.Admission.RequestAttempts.Count;

        int workAttemptsBefore = harness.Admission.WorkAttempts.Count;

        int ticketsBefore = harness.Admission.Tickets.Count;

        Assert.False(harness.Admission.TryAcquireRequestLease(GrimoireRequestKind.Finite, out var requestLease));

        Assert.Null(requestLease);

        Assert.False(harness.Admission.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out var workLease));

        Assert.Null(workLease);

        using SqliteConnection unopened = new();

        Assert.Throws<GrimoireMaintenanceUnavailableException>(() => harness.Admission.AcquireOrdinaryOpen(unopened));

        Assert.Equal(requestAttemptsBefore + 1, harness.Admission.RequestAttempts.Count);

        Assert.Equal(workAttemptsBefore + 1, harness.Admission.WorkAttempts.Count);

        Assert.Equal(ticketsBefore, harness.Admission.Tickets.Count);

        const string maintenanceMessage = "The Grimoire is temporarily unavailable while maintenance owns connection admission.";

        using HttpRequestMessage stats = harness.CreateObservedRequest(HttpMethod.Get, "/api/grimoire/stats");

        using HttpResponseMessage api = await harness.Client.SendAsync(stats, cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);

        var apiBody = await api.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseString, cancellationToken);

        Assert.False(apiBody!.IsSuccess, entryPoint.ToString());

        Assert.Null(apiBody.Data);

        Assert.Equal("Grimoire.MaintenanceUnavailable", apiBody.Error!.Value.Code);

        Assert.Equal(maintenanceMessage, apiBody.Error.Value.Message);

        Assert.Equal(0, harness.StatsProbe.Invocations);

        Assert.Equal(0, harness.Stats.Callbacks);

        using HttpRequestMessage models = harness.CreateObservedRequest(HttpMethod.Get, "/v1/models");

        using HttpResponseMessage v1 = await harness.Client.SendAsync(models, cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, v1.StatusCode);

        var openAi = await v1.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.OpenAiErrorResponse, cancellationToken);

        Assert.Equal("service_unavailable", openAi!.Error.Type);

        Assert.Equal("grimoire_maintenance", openAi.Error.Code);

        Assert.Equal(maintenanceMessage, openAi.Error.Message);

        Assert.Null(openAi.Error.Param);

        Assert.Equal(0, harness.ProbeForPath("/v1/models").Invocations);

        Assert.Null(harness.ProbeForPath("/v1/models").Scope);

        Assert.Equal(closedGeneration, harness.Admission.CurrentGeneration);

        Assert.Same(retained, Assert.Single(harness.Indexing.DeferredRequests));

        Assert.Equal(11, Assert.Single(harness.Indexing.DeferredRequests).Attempt);

        Assert.Equal(scopesBefore, harness.WorkerScopes.Scopes.Count);

        Assert.Equal(effectsBefore, harness.Admission.Effects.Count);

        Assert.Equal(weaveCallsBefore, harness.Weave.Calls);

        Assert.Equal(weaveCompletedBefore, harness.Weave.Completed);

        Assert.Equal(chatCallsBefore, harness.Chat.Calls);

        Assert.Equal(chatCompletedBefore, harness.Chat.Completed);

        Assert.Equal(failuresBefore, harness.IndexingLogs.Entries.Count(entry => entry.Level >= LogLevel.Warning));

        Assert.Equal(mutationsBefore, harness.OrdinaryMutations.Snapshot);

        TestLogEntry refusal = Assert.Single(harness.IndexingLogs.Entries, entry =>
            entry.Message.Contains(request.AttachmentId.ToString(), StringComparison.OrdinalIgnoreCase)
            && entry.Message.Contains("indexing deferred", StringComparison.Ordinal));

        Assert.Equal(LogLevel.Debug, refusal.Level);

        Assert.Null(refusal.Exception);
    }

    private sealed record OutcomeDatasetSnapshot(
        string[] Rows,
        IReadOnlyDictionary<string, byte[]> ManagedFiles);

    private sealed class OneShotOutcomeFault(
        CovenantErasureFaultBoundary boundary,
        CovenantResetPhase phase)
    {
        private int _fired;

        internal bool Fired => Volatile.Read(ref _fired) != 0;

        internal Task<Result> RaiseAsync(
            CovenantErasureFaultBoundary raisedBoundary,
            CovenantResetPhase raisedPhase,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                raisedBoundary == boundary
                && raisedPhase == phase
                && Interlocked.Exchange(ref _fired, 1) == 0
                    ? Result.Failure(new Error(
                        ErrorCodes.Covenant.ErasureIncomplete,
                        $"Injected outcome fault at {boundary} of {phase}."))
                    : Result.Success());
    }
}
