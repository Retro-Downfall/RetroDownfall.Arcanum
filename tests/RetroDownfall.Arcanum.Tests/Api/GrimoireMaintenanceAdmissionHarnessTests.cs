using System.Data.Common;

using System.Net;

using System.Net.Http.Json;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Mcp;

using Microsoft.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore.Diagnostics;

using Microsoft.EntityFrameworkCore.Infrastructure;

using Microsoft.AspNetCore.Hosting;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

[Trait("Category", "Integration")]
public sealed class GrimoireMaintenanceAdmissionHarnessTests
{
    [Theory]
    [InlineData("data: extra\n\ndata: [DONE]\n\n")]
    [InlineData("data: [DONE]\n\ndata: extra\n\n")]
    [InlineData("data: [DONE]\n\ndata: [DONE]\n\n")]
    [InlineData("data: unfinished")]
    [InlineData("data: [DONE]\n\ndata: unfinished")]
    public async Task Sse_reader_rejects_extra_or_incomplete_frames_after_revocation(string wire)
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);

        await using MaintenanceSseResponse reader = new(response, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(wire)));

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRevocationTerminalToEndAsync());
    }

    [Fact]
    public async Task Sse_reader_requires_one_complete_terminal_and_observes_EOF()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);

        await using MaintenanceSseResponse reader = new(response, new MemoryStream("data: [DONE]\n\n"u8.ToArray()));

        Assert.Equal(1, await reader.ReadRevocationTerminalToEndAsync());

        Assert.Equal(["data: [DONE]"], reader.Frames);
    }

    [SkippableFact]
    public async Task Host_log_capture_records_warning_and_error_from_real_categories_and_the_worker_logger()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        AssertQuietHost(harness);

        ILogger logger = harness.Factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(CovenantErasureCoordinator).FullName!);

        logger.LogWarning("controlled-capture-warning");

        logger.LogError("controlled-capture-error");

        harness.IndexingLogs.LogWarning("controlled-worker-warning");

        harness.IndexingLogs.LogError("controlled-worker-error");

        Assert.Collection(harness.HostLogs.Unexpected,
            entry => Assert.Equal((typeof(CovenantErasureCoordinator).FullName, LogLevel.Warning, "controlled-capture-warning"), (entry.Category, entry.Level, entry.Message)),
            entry => Assert.Equal((typeof(CovenantErasureCoordinator).FullName, LogLevel.Error, "controlled-capture-error"), (entry.Category, entry.Level, entry.Message)),
            entry => Assert.Equal((typeof(SessionAttachmentIndexingService).FullName, LogLevel.Warning, "controlled-worker-warning"), (entry.Category, entry.Level, entry.Message)),
            entry => Assert.Equal((typeof(SessionAttachmentIndexingService).FullName, LogLevel.Error, "controlled-worker-error"), (entry.Category, entry.Level, entry.Message)));
    }

    private static void AssertQuietHost(GrimoireMaintenanceAdmissionHarness harness) =>
        Assert.Empty(harness.HostLogs.Unexpected);

    [SkippableFact]
    public async Task Composed_endpoint_probes_observe_models_and_refused_routes_without_executing_or_creating_scopes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        using HttpRequestMessage unauthorized = harness.CreateObservedRequest(HttpMethod.Get, "/v1/models");

        unauthorized.Headers.Add(ArcanumApiHeaders.ApiKey, "wrong-key");

        using HttpResponseMessage denied = await harness.Client.SendAsync(unauthorized, timeout.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        Assert.Empty(harness.Admission.RequestAttempts);

        Assert.All(harness.EndpointProbes, probe => Assert.Null(probe.Scope));

        using HttpRequestMessage models = harness.CreateObservedRequest(HttpMethod.Get, "/v1/models");

        using HttpResponseMessage response = await harness.Client.SendAsync(models, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        PostAdmissionEndpointProbe modelProbe = harness.ProbeForPath("/v1/models");

        Assert.Equal(1, modelProbe.Invocations);

        Assert.NotNull(modelProbe.ObservedLease);

        await modelProbe.Scope!.WaitUntilDisposedAsync();

        await Assert.Single(harness.Admission.RequestAttempts).WaitUntilDisposedAsync();

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        await using IGrimoireClosingOwner closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

        foreach (PostAdmissionEndpointProbe probe in harness.EndpointProbes)
        {
            using HttpRequestMessage refusedRequest = harness.CreateObservedRequest(
                probe.Path == "/v1/chat/completions" ? HttpMethod.Post : HttpMethod.Get, probe.Path);

            using HttpResponseMessage refused = await harness.Client.SendAsync(refusedRequest, timeout.Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

            Assert.Equal(ReferenceEquals(probe, modelProbe) ? 1 : 0, probe.Invocations);

            if (!ReferenceEquals(probe, modelProbe))
            {
                Assert.Null(probe.Scope);

                Assert.Null(probe.ObservedLease);
            }
        }

        Assert.Equal(harness.EndpointProbes.Count, harness.Admission.RequestAttempts.Count(attempt => !attempt.Acquired));

        AssertQuietHost(harness);
    }

    [SkippableFact]
    public async Task Recovery_host_preserves_original_participants_without_reseeding_rows_blobs_or_effects()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        Guid sessionId;

        Guid apprenticeId;

        Guid entryId;

        Guid[] attachmentIds;

        string[] beforeRows;

        string[] beforeBlobs;

        await using (GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness.StartAsync(profile))
        {
            sessionId = first.SessionId;

            apprenticeId = first.ApprenticeId;

            entryId = first.EntryId;

            attachmentIds = [first.DownloadAttachment.Id, first.IndexingAttachmentA.Id, first.IndexingAttachmentB.Id];

            // This ordinary-open restart baseline must not race legitimate recovery of pending
            // indexing work. Settle the original A/B through the real worker, without reseeding.
            first.Weave.Checkpoint.Release();

            Assert.True(first.Indexing.TryEnqueue(new(first.IndexingAttachmentA.Id, first.SessionId, Attempt: 0)));

            Assert.True(first.Indexing.TryEnqueue(new(first.IndexingAttachmentB.Id, first.SessionId, Attempt: 0)));

            await first.Weave.WaitUntilSecondCallAsync();

            foreach (WorkerScopeObservation observed in first.WorkerScopes.Scopes)
            {
                await observed.WaitUntilDisposedAsync();
            }

            await using (AsyncServiceScope verification = first.Factory.Services.CreateAsyncScope())
            {
                SessionAttachmentIndexRepository index = verification.ServiceProvider.GetRequiredService<SessionAttachmentIndexRepository>();

                Assert.Equal(SessionAttachmentIndexStatus.Indexed, (await index.GetStateAsync(first.IndexingAttachmentA.Id, CancellationToken.None)).Status);

                Assert.Equal(SessionAttachmentIndexStatus.Indexed, (await index.GetStateAsync(first.IndexingAttachmentB.Id, CancellationToken.None)).Status);
            }

            beforeRows = await ParticipantRowsAsync(first);

            beforeBlobs = Directory.GetFiles(ArcanumPaths.AttachmentsDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        }

        await using GrimoireMaintenanceAdmissionHarness recovered = await GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile);

        Assert.Equal(sessionId, recovered.SessionId);

        Assert.Equal(apprenticeId, recovered.ApprenticeId);

        Assert.Equal(entryId, recovered.EntryId);

        Assert.Equal(attachmentIds, new[] { recovered.DownloadAttachment.Id, recovered.IndexingAttachmentA.Id, recovered.IndexingAttachmentB.Id });

        Assert.Equal(beforeRows, await ParticipantRowsAsync(recovered));

        Assert.Equal(beforeBlobs, Directory.GetFiles(ArcanumPaths.AttachmentsDirectory, "*", SearchOption.AllDirectories).Order().ToArray());

        // Ordinary startup performs the real health probe. It is not participant seeding.
        // Retained-closed recovery scenarios must instead require no ordinary effects at all.
        Assert.Equal(GrimoireWorkKind.ProviderHealthProbe, Assert.Single(recovered.Admission.Effects).Kind);

        Assert.Equal(0, recovered.Chat.Calls);

        Assert.Equal(0, recovered.Weave.Calls);

        Assert.Empty(recovered.Operations.Checkpoints);

        Assert.Empty(recovered.Operations.Transitions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => GrimoireMaintenanceAdmissionHarness.StartAsync(profile));

        AssertQuietHost(recovered);
    }

    private static async Task<string[]> ParticipantRowsAsync(GrimoireMaintenanceAdmissionHarness harness)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await db.Database.SqlQueryRaw<string>("""
            SELECT 'session:' || Id AS Value FROM Sessions
            UNION ALL SELECT 'entry:' || Id AS Value FROM Entries
            UNION ALL SELECT 'apprentice:' || Id AS Value FROM Apprentices
            UNION ALL SELECT 'attachment:' || Id AS Value FROM SessionAttachments
            ORDER BY Value
            """).ToArrayAsync(timeout.Token);
    }

    [SkippableFact]
    public async Task Borrowed_hosts_decrypt_the_original_blob_using_the_shared_reset_visible_file_credential()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        SessionAttachmentRecord original;

        await using (GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness.StartAsync(profile))
        {
            original = first.DownloadAttachment;

            Assert.Same(profile.CredentialStore, first.Factory.Services.GetRequiredService<IOsCredentialStore>());

            Assert.Equal(OsCredentialStoreStatus.Ok, profile.CredentialStore.TryGet(
                ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.FileEncryptionKeyAccount).Status);
        }

        await using GrimoireMaintenanceAdmissionHarness second = await GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile);

        Assert.Same(profile.CredentialStore, second.Factory.Services.GetRequiredService<IOsCredentialStore>());

        Assert.Equal(OsCredentialStoreStatus.Ok, second.Factory.Services.GetRequiredService<IOsCredentialStore>().TryGet(
            ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.FileEncryptionKeyAccount).Status);

        await using AsyncServiceScope scope = second.Factory.Services.CreateAsyncScope();

        second.Blobs.Checkpoint.Release();

        ReadOnlyMemory<byte> recovered = await scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>()
            .ReadBytesAsync(original, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("controlled-download-prefix-and-complete-encrypted-payload"u8.ToArray(), recovered.ToArray());

        Assert.Equal(1, second.Blobs.WrappedReads);
    }

    [SkippableFact]
    public async Task Harness_cleanup_releases_pending_participants_after_assertion_failure_and_preserves_borrowed_profile()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync(profile);

        ControlledEventBus events = harness.Events;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Task<HttpResponseMessage> stats = harness.StartStatsAsync(timeout.Token);

        await harness.Stats.Checkpoint.WaitUntilReachedAsync();

        await harness.OpenSseAsync("/api/events/daemon");

        await events.Daemon.Checkpoint.WaitUntilReachedAsync();

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(async () =>
        {
            await using (harness)
            {
                Assert.Fail("Controlled assertion failure exercises ordinary await-using cleanup.");
            }
        });

        await events.Daemon.WaitUntilDisposedAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stats);

        await harness.StatsProbe.Scope!.WaitUntilDisposedAsync();

        await harness.DisposeAsync();

        await using GrimoireMaintenanceAdmissionHarness restarted = await GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile);

        using HttpResponseMessage response = await restarted.Client.GetAsync("/api/grimoire/stats", timeout.Token);

        var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, timeout.Token);

        Assert.True(body?.IsSuccess);

        Assert.Equal(1, body!.Data!.SessionCount);
    }

    [SkippableFact]
    public async Task Harness_stats_observation_is_bound_to_one_marked_request_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Task<HttpResponseMessage> stats = harness.StartStatsAsync(timeout.Token);

        try
        {
            await harness.Stats.Checkpoint.WaitUntilReachedAsync();

            Assert.False(stats.IsCompleted);

            Assert.Same(harness.StatsProbe.Context, harness.Stats.Context);

            Assert.Equal(System.Data.ConnectionState.Closed, harness.StatsProbe.InitialConnectionState);

            Assert.Equal(System.Data.ConnectionState.Open, harness.Stats.ConnectionStateAtPause);

            await using AsyncServiceScope optionsScope = harness.Factory.Services.CreateAsyncScope();

            var options = optionsScope.ServiceProvider.GetRequiredService<ArcanumDbContext>().GetService<IDbContextOptions>();

            IInterceptor[] interceptors = options.FindExtension<CoreOptionsExtension>()!.Interceptors!.ToArray();

            Assert.IsType<CovenantConnectionEnrolmentInterceptor>(interceptors[0]);

            Assert.Same(harness.Stats, interceptors[1]);

            Assert.Same(harness.EfOpen, interceptors[2]);

            using HttpResponseMessage unmarked = await harness.Client.GetAsync("/api/grimoire/stats", timeout.Token);

            Assert.Equal(HttpStatusCode.OK, unmarked.StatusCode);

            Assert.Equal(1, harness.Stats.Callbacks);

            Assert.Equal(1, harness.StatsProbe.Invocations);

            Assert.False(stats.IsCompleted);
        }
        finally
        {
            harness.Stats.Checkpoint.Release();
        }

        using HttpResponseMessage response = await stats.WaitAsync(timeout.Token);

        var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(body?.IsSuccess);

        Assert.Equal(1, body!.Data!.SessionCount);

        await harness.StatsProbe.Scope!.WaitUntilDisposedAsync();

        foreach (var attempt in harness.Admission.RequestAttempts)
        {
            await attempt.WaitUntilDisposedAsync();
        }

        Assert.Null(harness.StatsProbe.Admission!.Lease);

        Assert.False(harness.StatsProbe.IsExecuting);

        using HttpResponseMessage later = await harness.Client.GetAsync("/api/grimoire/stats", timeout.Token);

        Assert.Equal(HttpStatusCode.OK, later.StatusCode);

        Assert.Equal(1, harness.Stats.Callbacks);

        AssertQuietHost(harness);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Harness_plans_through_the_authenticated_route_and_builds_the_exact_apply_payload(bool factoryReset)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        GrimoireTransitionEntryPoint entryPoint = factoryReset
            ? GrimoireTransitionEntryPoint.StandaloneFactoryReset
            : GrimoireTransitionEntryPoint.DirectCovenantReset;

        DataRetentionPlan plan = await harness.PlanAsync(entryPoint);

        Assert.False(string.IsNullOrWhiteSpace(plan.PlanId));

        Assert.Empty(plan.Blockers);

        Assert.Equal(factoryReset ? DataRetentionOperation.FactoryReset : DataRetentionOperation.ResetMemory, plan.Request.Operation);

        Assert.Equal(factoryReset ? null : MemoryResetScope.Covenant, plan.Request.MemoryScope);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage request = harness.CreateApplyRequest(entryPoint, plan.PlanId, requestedOperationId);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal(factoryReset ? "/api/data/factory-reset" : "/api/data/memory/reset", request.RequestUri!.OriginalString);

        if (factoryReset)
        {
            FactoryResetRequest payload = Assert.IsType<FactoryResetRequest>(await request.Content!
                .ReadFromJsonAsync(ArcanumJsonContext.Default.FactoryResetRequest));

            Assert.Equal("factory-reset", payload.Confirmation);

            Assert.Equal(plan.PlanId, payload.ExpectedPlanId);

            Assert.Equal(requestedOperationId, payload.RequestedOperationId);

            Assert.Null(payload.InstallationResetHandoff);
        }
        else
        {
            MemoryResetRequest payload = Assert.IsType<MemoryResetRequest>(await request.Content!
                .ReadFromJsonAsync(ArcanumJsonContext.Default.MemoryResetRequest));

            Assert.Equal(MemoryResetScope.Covenant, payload.Scope);

            Assert.Equal(plan.PlanId, payload.ExpectedPlanId);

            Assert.Null(payload.CampaignId);
        }

        AssertQuietHost(harness);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_adoption_pauses_only_after_the_real_store_has_acquired_the_lease(bool acquired)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        LongRunningOperationStore store = scope.ServiceProvider.GetRequiredService<LongRunningOperationStore>();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation created = await store.CreateAsync(new(LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Adoption observer", now), timeout.Token);

        LongRunningOperation leased = (await store.TryAcquireLeaseAsync(created.Id, "previous-owner", now, now.AddMinutes(30), timeout.Token)).Operation;

        if (!acquired)
        {
            Assert.True(await store.TryTransitionAsync(leased.Id, leased.Revision, "previous-owner", LongRunningOperationState.Completed, now, cancellationToken: timeout.Token));
        }

        LongRunningOperation current = Assert.IsType<LongRunningOperation>(await store.GetAsync(created.Id, timeout.Token));

        var heldLock = harness.Factory.Services.GetRequiredService<IInstallationResetMaintenanceLockAccessor>().BorrowHeldLock(ArcanumPaths.GrimoireDirectory).Value;

        harness.Adoption.PauseAfterAcquisition = true;

        PausingMaintenanceLeaseAdoption adoption = Assert.IsType<PausingMaintenanceLeaseAdoption>(scope.ServiceProvider.GetRequiredService<ILongRunningOperationMaintenanceLeaseAdoption>());

        Assert.Same(store, adoption.Inner);

        Task<LongRunningOperationLeaseResult> adopting = adoption.AdoptUnderInstallationLockAsync(heldLock,
            ArcanumPaths.GrimoireDirectory, new(current.Id, current.Kind, current.CheckpointVersion, current.Revision),
            "recovery-owner", now, now.AddMinutes(5), timeout.Token);

        try
        {
            if (acquired)
            {
                await harness.Adoption.Checkpoint.WaitUntilReachedAsync();

                Assert.False(adopting.IsCompleted);

                await using AsyncServiceScope verification = harness.Factory.Services.CreateAsyncScope();

                LongRunningOperation durable = Assert.IsType<LongRunningOperation>(await verification.ServiceProvider
                    .GetRequiredService<ILongRunningOperationStore>().GetAsync(created.Id, timeout.Token));

                Assert.Equal("recovery-owner", durable.LeaseOwner);

                Assert.Equal(current.Revision + 1, durable.Revision);
            }
        }
        finally
        {
            harness.Adoption.Checkpoint.Release();
        }

        LongRunningOperationLeaseResult result = await adopting.WaitAsync(timeout.Token);

        Assert.Equal(acquired, result.Acquired);

        Assert.Same(harness.Adoption.Result, result);

        Assert.Equal(acquired ? 1 : 0, harness.Adoption.Pauses);

        Assert.Equal(1, harness.Adoption.Calls);
    }

    [SkippableFact]
    public async Task Recording_store_retains_only_immutable_successful_writes_while_admission_is_closed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Guid operationId;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] payload = [1, 2, 3];

        await using (AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope())
        {
            RecordingLongRunningOperationStore store = Assert.IsType<RecordingLongRunningOperationStore>(scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>());

            Assert.Same(scope.ServiceProvider.GetRequiredService<LongRunningOperationStore>(), store.Inner);

            LongRunningOperation created = await store.CreateAsync(new(LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently, "Observer checkpoint", now), timeout.Token);

            operationId = created.Id;

            Assert.True((await store.TryAcquireLeaseAsync(operationId, "observer", now, now.AddMinutes(2), timeout.Token)).Acquired);

            Assert.True(await store.SaveCheckpointAsync(operationId, "observer", 0, 1, payload, "reference", "Recorded", now, timeout.Token));

            payload[0] = 99;

            Assert.False(await store.SaveCheckpointAsync(operationId, "foreign", 1, 2, payload, null, "Refused", now, timeout.Token));

            LongRunningOperation current = Assert.IsType<LongRunningOperation>(await store.GetAsync(operationId, timeout.Token));

            Assert.Equal(new byte[] { 1, 2, 3 }, current.CheckpointPayload);

            Assert.True(await store.TryTransitionAsync(operationId, current.Revision, "observer", LongRunningOperationState.Waiting, now, cancellationToken: timeout.Token));

            Assert.False(await store.TryTransitionAsync(operationId, current.Revision, "foreign", LongRunningOperationState.Completed, now, cancellationToken: timeout.Token));
        }

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)6, 32).ToArray()));

        await using IGrimoireClosingOwner closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

        Assert.True((await harness.Admission.DrainRequestAndWorkAsync(closing, timeout.Token)).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await harness.Admission.CloseConnectionAdmissionAsync(closing, timeout.Token)).Value;

        RecordedMaintenanceCheckpoint checkpoint = Assert.Single(harness.Operations.Checkpoints, item => item.OperationId == operationId);

        Assert.Equal(new byte[] { 1, 2, 3 }, checkpoint.Payload!.Value.ToArray());

        Assert.Equal("reference", checkpoint.Reference);

        Assert.Equal("Recorded", checkpoint.PublicSummary);

        Assert.Equal(0, checkpoint.ExpectedVersion);

        Assert.Equal(1, checkpoint.Version);

        Assert.Equal(now, checkpoint.UtcNow);

        RecordedMaintenanceTransition transition = Assert.Single(harness.Operations.Transitions, item => item.OperationId == operationId);

        Assert.Equal(LongRunningOperationState.Waiting, transition.State);

        Assert.Equal("observer", transition.OwnerId);

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, timeout.Token)).IsSuccess);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Pre_native_open_observers_preserve_success_and_generation_race(bool useEf, bool closeAdmission)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.CloseConnectionAsync();

        DbConnection connection = db.Database.GetDbConnection();

        harness.EfOpen.TargetContext = db;

        using IDisposable selectedRawOpen = harness.RawOpen.SelectCurrentExecution();

        IGrimoireOrdinaryConnectionLease? rawLease = null;

        async Task<bool> OpenAsync()
        {
            if (useEf)
            {
                try
                {
                    await db.Database.OpenConnectionAsync(timeout.Token);

                    return true;
                }
                catch (GrimoireMaintenanceUnavailableException)
                {
                    return false;
                }
            }

            var result = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, timeout.Token);

            rawLease = result.IsSuccess ? result.Value : null;

            return result.IsSuccess;
        }

        int previousTickets = harness.Admission.Tickets.Count;

        Task<bool> opening = Task.Factory.StartNew(OpenAsync, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        MaintenanceCheckpoint checkpoint = useEf ? harness.EfOpen.Checkpoint : harness.RawOpen.Checkpoint;

        IGrimoireClosingOwner? closing = null;

        try
        {
            await checkpoint.WaitUntilReachedAsync();

            GrimoireTicketObservation ticket = Assert.Single(harness.Admission.Tickets.Skip(previousTickets));

            connection = ticket.Connection;

            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

            Assert.Equal(0, ticket.TerminalCount);

            Assert.False(opening.IsCompleted);

            if (useEf)
            {
                Assert.Same(db, harness.EfOpen.Context);

                Assert.Same(connection, harness.EfOpen.Connection);
            }
            else
            {
                Assert.Equal(1, harness.RawOpen.SelectedConstructions);
            }

            Task<RetroDownfall.Arcanum.Core.Primitives.Result<IGrimoireExclusiveClosedLease>>? closingConnections = null;

            if (closeAdmission)
            {
                CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest(Enumerable.Repeat((byte)9, 32).ToArray()));

                closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

                Assert.True((await harness.Admission.DrainRequestAndWorkAsync(closing, timeout.Token)).IsSuccess);

                closingConnections = harness.Admission.CloseConnectionAdmissionAsync(closing, timeout.Token).AsTask();

                await harness.Admission.StageTwo.WaitUntilEnteredAsync();

                Assert.False(closingConnections.IsCompleted);
            }

            checkpoint.Release();

            Assert.Equal(!closeAdmission, await opening.WaitAsync(timeout.Token));

            Assert.Equal(1, ticket.TerminalCount);

            Assert.Equal(closeAdmission ? "refused-after-open" : "opened", ticket.Terminal);

            if (closingConnections is not null)
            {
                await using IGrimoireExclusiveClosedLease closed = (await closingConnections.WaitAsync(timeout.Token)).Value;

                Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

                var cleared = Assert.Single(harness.Drain.PoolClears, observation => ReferenceEquals(connection, observation.Connection));

                Assert.Equal(System.Data.ConnectionState.Closed, cleared.StateBefore);

                Assert.True(cleared.Result.IsSuccess);

                if (!useEf)
                {
                    Assert.Same(connection, Assert.Single(harness.RawOpen.ClearedConnections));
                }

                Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, timeout.Token)).IsSuccess);
            }
            else
            {
                Assert.Equal(System.Data.ConnectionState.Open, connection.State);

                Assert.Contains(harness.Drain.Enrolments, observation => ReferenceEquals(connection, observation.Connection));
            }
        }
        finally
        {
            checkpoint.Release();

            await opening.WaitAsync(timeout.Token);

            if (rawLease is not null)
            {
                await rawLease.DisposeAsync();
            }

            await db.Database.CloseConnectionAsync();

            if (closing is not null)
            {
                await closing.DisposeAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Hosted_indexing_preserves_the_admitted_effect_and_exact_post_close_request_identity()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        SessionAttachmentIndexRequest requestA = new(harness.IndexingAttachmentA.Id, harness.SessionId, Attempt: 3);

        SessionAttachmentIndexRequest requestB = new(harness.IndexingAttachmentB.Id, harness.SessionId, Attempt: 7);

        Assert.True(harness.Indexing.TryEnqueue(requestA));

        await harness.Weave.Checkpoint.WaitUntilReachedAsync();

        Assert.Single(harness.WorkerScopes.Scopes, scope => scope.Disposals == 0);

        Assert.Equal(1, harness.Weave.Calls);

        Assert.Equal(0, harness.Blobs.WrappedReads);

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        await using IGrimoireClosingOwner closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

        Task<RetroDownfall.Arcanum.Core.Primitives.Result> draining = harness.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.False(draining.IsCompleted);

        harness.Weave.Checkpoint.Release();

        Assert.True((await draining.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await harness.Admission.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.All(harness.WorkerScopes.Scopes, observed => Assert.Equal(1, observed.Disposals));

        int beforeScopes = harness.WorkerScopes.Scopes.Count;

        int beforeEffects = harness.Admission.Effects.Count;

        // The gate issues a permit for this exact scoped ledger connection. Reads during closed
        // admission use the production connection initializer and tracked physical-close protocol.
        await using (AsyncServiceScope inspection = harness.Factory.Services.CreateAsyncScope())
        {
            ICovenantClosedPeriodLedgerConnection ledger = inspection.ServiceProvider.GetRequiredService<ICovenantClosedPeriodLedgerConnection>();

            await using IGrimoireMaintenanceIoLane lane = (await closed.AcquireMaintenanceIoLaneAsync(
                (actualOwner, generation, _) => ValueTask.FromResult(actualOwner == closed.Owner && generation == closed.Generation),
                CancellationToken.None)).Value;

            await using IGrimoireScopedConnectionPermit permit = closed.AcquireScopedConnectionPermit(ledger.Connection).Value;

            await using IGrimoireTrackedMaintenanceHandle handle = permit.AcquireOpen(
                ledger.Connection, closed.Owner, closed.Generation, lane).Value;

            Assert.True(handle.ReportOpenStarted().IsSuccess);

            try
            {
                await ledger.OpenAsync(CancellationToken.None);

                string[] baseline = await IndexingSnapshotAsync(ledger.Connection, requestB.AttachmentId);

                harness.IndexingLogs.ExpectDeferred(requestB.AttachmentId);

                Assert.True(harness.Indexing.TryEnqueue(requestB));

                await harness.IndexingLogs.WaitUntilDeferredAsync();

                Assert.Equal(requestB, Assert.Single(harness.Indexing.DeferredRequests));

                Assert.Equal(beforeScopes, harness.WorkerScopes.Scopes.Count);

                Assert.All(harness.WorkerScopes.Scopes, observed => Assert.Equal(1, observed.Disposals));

                Assert.Equal(beforeEffects, harness.Admission.Effects.Count);

                Assert.Equal(baseline, await IndexingSnapshotAsync(ledger.Connection, requestB.AttachmentId));

                Assert.Equal(1, harness.Weave.Calls);

                Assert.Equal(1, harness.Weave.Completed);

                Assert.Equal(0, harness.Weave.Cancellations);
            }
            finally
            {
                await ledger.Connection.CloseAsync();

                Assert.True(harness.Drain.ClearExactPoolAfterClose((Microsoft.Data.Sqlite.SqliteConnection)ledger.Connection).IsSuccess);

                Assert.True(handle.ReportPhysicallyClosed().IsSuccess);
            }
        }

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None)).IsSuccess);

        await harness.Weave.WaitUntilSecondCallAsync();

        await harness.Admission.WorkAttempts.Last(attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && attempt.Acquired).WaitUntilDisposedAsync();

        Assert.True(harness.WorkerScopes.Scopes.Count > beforeScopes);

        WorkerScopeObservation resumedScope = harness.WorkerScopes.Scopes[beforeScopes];

        await resumedScope.WaitUntilDisposedAsync();

        Assert.Equal(1, resumedScope.Disposals);

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        SessionAttachmentIndexRepository repository = scope.ServiceProvider.GetRequiredService<SessionAttachmentIndexRepository>();

        SessionAttachmentIndexState a = await repository.GetStateAsync(requestA.AttachmentId, CancellationToken.None);

        SessionAttachmentIndexState b = await repository.GetStateAsync(requestB.AttachmentId, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, a.Status);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, b.Status);

        Assert.Equal(3, a.AttemptCount);

        Assert.Equal(7, b.AttemptCount);

        Assert.Null(a.FailureReason);

        Assert.Null(b.FailureReason);

        Assert.Single(await repository.GetChunksForAttachmentAsync(requestA.AttachmentId, CancellationToken.None));

        Assert.Single(await repository.GetChunksForAttachmentAsync(requestB.AttachmentId, CancellationToken.None));

        Assert.Equal(2, harness.Weave.Calls);

        Assert.Equal(0, harness.Blobs.WrappedReads);

        AssertQuietHost(harness);
    }

    private static async Task<string[]> IndexingSnapshotAsync(DbConnection connection, Guid attachmentId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        List<string> rows = [];

        foreach (string table in new[] { "SessionAttachments", "session_attachment_index_state", "session_attachment_chunks" })
        {
            await using DbCommand command = connection.CreateCommand();

            string key = table == "SessionAttachments" ? "Id" : "AttachmentId";

            command.CommandText = $"SELECT * FROM {table} WHERE {key} = @id ORDER BY 1";

            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = "@id";

            parameter.Value = attachmentId.ToString().ToUpperInvariant();

            command.Parameters.Add(parameter);

            await using DbDataReader reader = await command.ExecuteReaderAsync(timeout.Token);

            int count = 0;

            while (await reader.ReadAsync(timeout.Token))
            {
                string[] values = Enumerable.Range(0, reader.FieldCount).Select(index =>
                {
                    if (reader.IsDBNull(index))
                    {
                        return "null";
                    }

                    string value = Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!;

                    return $"{reader.GetName(index)}:{value.Length}:{value}";
                }).ToArray();

                rows.Add($"{table}:{string.Join('|', values)}");

                count++;
            }

            rows.Add($"{table}:count:{count}");
        }

        return rows.ToArray();
    }

    [SkippableFact]
    public async Task Billable_chat_keeps_its_real_finite_request_and_provider_effect_until_completion()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        Task<MaintenanceSseResponse> opening = harness.OpenChatAsync();

        await harness.Chat.Checkpoint.WaitUntilReachedAsync();

        await using MaintenanceSseResponse response = await opening.WaitAsync(TimeSpan.FromSeconds(10));

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        await using IGrimoireClosingOwner closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

        Task<RetroDownfall.Arcanum.Core.Primitives.Result> draining = harness.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        try
        {
            Assert.False(draining.IsCompleted);

            Assert.False(Assert.Single(harness.Admission.RequestAttempts).MaintenanceRevocation.IsCancellationRequested);

            Assert.Equal(GrimoireRequestKind.Finite, harness.Admission.RequestAttempts[0].Kind);

            Assert.Equal(1, harness.Chat.Calls);

            Assert.Equal(0, harness.Chat.Completed);
        }
        finally
        {
            harness.Chat.Checkpoint.Release();
        }

        Assert.Equal(1, await response.ReadTerminalCountToEndAsync());

        Assert.Contains(response.Frames, frame => frame.Contains("controlled-billable-prefix", StringComparison.Ordinal));

        Assert.Contains(response.Frames, frame => frame.Contains("controlled-billable-complete", StringComparison.Ordinal));

        Assert.True((await draining.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await harness.Chat.WaitUntilDisposedAsync();

        await harness.ProbeForPath("/v1/chat/completions").Scope!.WaitUntilDisposedAsync();

        Assert.Equal(1, harness.Chat.Completed);

        Assert.Equal(1, harness.Chat.Disposals);

        AssertQuietHost(harness);
    }

    [SkippableFact]
    public async Task Download_reader_pauses_only_its_distinct_blob_and_finishes_the_real_payload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        Assert.Equal(3, new[] { harness.DownloadAttachment.Id, harness.IndexingAttachmentA.Id, harness.IndexingAttachmentB.Id }.Distinct().Count());

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));

        string path = $"/api/sessions/{harness.SessionId}/attachments/{harness.DownloadAttachment.Id}/content";

        using HttpRequestMessage request = harness.CreateObservedRequest(HttpMethod.Get, path);

        using HttpResponseMessage response = await harness.Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation.Token);

        byte[] prefix = new byte[8];

        try
        {
            await stream.ReadExactlyAsync(prefix, cancellation.Token);

            await harness.Blobs.Checkpoint.WaitUntilReachedAsync();

            Assert.Equal(harness.DownloadBytes[..8], prefix);

            Assert.Equal(8, harness.Blobs.BytesRead);

            await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

            var attachments = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Storage.ISessionAttachmentStore>();

            Assert.NotEmpty((await attachments.ReadBytesAsync(harness.IndexingAttachmentA, cancellation.Token)).ToArray());

            Assert.NotEmpty((await attachments.ReadBytesAsync(harness.IndexingAttachmentB, cancellation.Token)).ToArray());

            Assert.Equal(1, harness.Blobs.WrappedReads);
        }
        finally
        {
            harness.Blobs.Checkpoint.Release();
        }

        using MemoryStream remaining = new();

        await stream.CopyToAsync(remaining, cancellation.Token);

        Assert.Equal(harness.DownloadBytes, prefix.Concat(remaining.ToArray()).ToArray());

        await harness.Blobs.WaitUntilDisposedAsync();

        await harness.ProbeForPath(path).Scope!.WaitUntilDisposedAsync();

        await Assert.Single(harness.Admission.RequestAttempts).WaitUntilDisposedAsync();

        AssertQuietHost(harness);
    }

    [SkippableFact]
    public async Task Five_real_SSE_routes_finish_complete_frames_and_dispose_after_gate_revocation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        await using MaintenanceSseResponse daemon = await harness.OpenSseAsync("/api/events/daemon");

        await using MaintenanceSseResponse mcp = await harness.OpenSseAsync("/api/events/mcp");

        await using MaintenanceSseResponse logs = await harness.OpenSseAsync("/api/events/logs");

        await using MaintenanceSseResponse session = await harness.OpenSseAsync($"/api/sessions/{harness.SessionId}/stream");

        await using MaintenanceSseResponse chronicle = await harness.OpenSseAsync($"/api/apprentices/{harness.ApprenticeId}/chronicle");

        string daemonFrame = "data: " + JsonSerializer.Serialize(new DaemonEvent(DateTimeOffset.UnixEpoch,
            Guid.Parse("25700000-0000-0000-0000-000000000001"), "controlled-daemon", "controlled-spell", DaemonEventType.Started),
            ArcanumJsonContext.Default.DaemonEvent);

        string mcpFrame = "data: " + JsonSerializer.Serialize(new McpServerEvent(DateTimeOffset.UnixEpoch)
            { ServerName = "controlled-mcp", State = McpServerState.Running }, ArcanumJsonContext.Default.McpServerEvent);

        string logFrame = "data: " + JsonSerializer.Serialize(new RetroDownfall.Arcanum.Core.Logging.LogEntry(
            257, DateTimeOffset.UnixEpoch, RetroDownfall.Arcanum.Core.Logging.LogLevel.Information,
            "MaintenanceTests", "controlled-log", null, null, null, []), ArcanumJsonContext.Default.LogEntry);

        Assert.Equal(daemonFrame, await daemon.ReadDataFrameAsync());

        Assert.Equal(mcpFrame, await mcp.ReadDataFrameAsync());

        Assert.Equal(logFrame, await logs.ReadDataFrameAsync());

        string entryFrame = await session.ReadDataFrameAsync();

        var entry = JsonSerializer.Deserialize(entryFrame[6..], ArcanumJsonContext.Default.EntryDto);

        Assert.NotNull(entry);

        Assert.Equal(harness.EntryId, entry.Id);

        Assert.Equal(harness.SessionId, entry.SessionId);

        Assert.Equal("controlled-entry", entry.Content);

        Assert.Equal("user", entry.Role);

        Assert.Null(entry.ToolCallId);

        Assert.Null(entry.ToolName);

        Assert.False(entry.IsPinned);

        // Consume the real replay/live sentinel before revocation so it cannot be mistaken
        // for a newly started data frame after the gate has revoked this request.
        Assert.Equal("data: {\"type\":\"live\"}", await session.ReadDataFrameAsync());

        string chronicleFrame = await chronicle.ReadDataFrameAsync();

        using JsonDocument plan = JsonDocument.Parse(chronicleFrame[6..]);

        Assert.Equal(new[] { "type", "apprenticeId", "timestamp", "plan" }, plan.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal("planGenerated", plan.RootElement.GetProperty("type").GetString());

        Assert.Equal(harness.ApprenticeId, plan.RootElement.GetProperty("apprenticeId").GetGuid());

        Assert.True(plan.RootElement.GetProperty("timestamp").TryGetDateTimeOffset(out _));

        JsonElement stepElement = Assert.Single(plan.RootElement.GetProperty("plan").EnumerateArray());

        var step = stepElement.Deserialize(ArcanumJsonContext.Default.PlanStep);

        Assert.NotNull(step);

        Assert.Equal(1, step.Index);

        Assert.Equal("controlled-plan", step.Description);

        PostAdmissionEndpointProbe[] probes = harness.SseProbes.ToArray();

        Assert.Equal(5, probes.Length);

        Assert.All(probes, probe =>
        {
            Assert.Equal(1, probe.Invocations);

            Assert.NotNull(probe.Scope);

            Assert.NotNull(probe.ObservedLease);

            Assert.True(probe.IsExecuting);
        });

        Assert.Equal(5, probes.Select(probe => probe.Scope).Distinct().Count());

        await harness.Events.Daemon.Checkpoint.WaitUntilReachedAsync();

        await harness.Events.Mcp.Checkpoint.WaitUntilReachedAsync();

        await harness.Logs.Frames.Checkpoint.WaitUntilReachedAsync();

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        await using IGrimoireClosingOwner closing = harness.Admission.BeginOrResumeExclusive(owner).Value;

        Task<RetroDownfall.Arcanum.Core.Primitives.Result> draining = harness.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await harness.Events.Daemon.WaitUntilCancelledAsync();

        await harness.Events.Mcp.WaitUntilCancelledAsync();

        await harness.Logs.Frames.WaitUntilCancelledAsync();

        Assert.False(draining.IsCompleted);

        Assert.All(harness.Admission.RequestAttempts, attempt => Assert.True(attempt.MaintenanceRevocation.IsCancellationRequested));

        harness.ReleaseStreamProducers();

        foreach (MaintenanceSseResponse response in new[] { daemon, mcp, logs, session, chronicle })
        {
            Assert.Equal(1, await response.ReadRevocationTerminalToEndAsync());
        }

        Assert.Equal(new[] { daemonFrame, "data: [DONE]" }, daemon.Frames);

        Assert.Equal(new[] { mcpFrame, "data: [DONE]" }, mcp.Frames);

        Assert.Equal(new[] { ": connected", logFrame, "data: [DONE]" }, logs.Frames);

        Assert.Equal(new[] { entryFrame, "data: {\"type\":\"live\"}", "data: [DONE]" }, session.Frames);

        Assert.Equal(new[] { chronicleFrame, "data: [DONE]" }, chronicle.Frames);

        Assert.True((await draining.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await harness.Events.Daemon.WaitUntilDisposedAsync();

        await harness.Events.Mcp.WaitUntilDisposedAsync();

        await harness.Logs.Frames.WaitUntilDisposedAsync();

        Assert.Equal(0, harness.Factory.Services.GetRequiredService<SessionEventHub>().GetSubscriberCount(harness.SessionId));

        Assert.Equal(0, harness.Factory.Services.GetRequiredService<ChronicleHub>().TrackedApprenticeCount);

        Assert.Equal(5, harness.Admission.RequestAttempts.Count);

        Assert.All(harness.Admission.RequestAttempts, attempt => Assert.Equal(1, attempt.Disposals));

        foreach (PostAdmissionEndpointProbe probe in probes)
        {
            await probe.Scope!.WaitUntilDisposedAsync();

            Assert.False(probe.IsExecuting);
        }

        AssertQuietHost(harness);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_stats_request_reaches_the_connection_barrier_before_materializing_its_result(bool closeAdmission)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PostAdmissionEndpointProbe probe = new("/api/grimoire/stats");

        PausingStatsConnectionInterceptor interceptor = new(probe);

        await using ArcanumWebApplicationFactory factory = new()
        {
            AdditionalDbContextInterceptors = [interceptor],

            BeforeEndpoint = probe.InvokeAsync,

            ServiceOverrides = services =>
            {
                services.AddScoped<MaintenanceScopeSentinel>();

                services.RemoveAll<IGrimoireConnectionAdmissionGate>();

                services.AddSingleton<IGrimoireConnectionAdmissionGate>(sp =>
                    new GrimoireMaintenanceAdmissionObserver(sp.GetRequiredService<GrimoireConnectionAdmissionGate>()));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        client.DefaultRequestHeaders.Add(PostAdmissionEndpointProbe.Header, probe.RequestId);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));

        Task<HttpResponseMessage> request = client.GetAsync("/api/grimoire/stats", cancellation.Token);

        IGrimoireClosingOwner? closing = null;

        Task<RetroDownfall.Arcanum.Core.Primitives.Result>? draining = null;

        GrimoireMaintenanceAdmissionObserver observer = (GrimoireMaintenanceAdmissionObserver)factory.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        try
        {
            await interceptor.Checkpoint.WaitUntilReachedAsync();

            Assert.False(request.IsCompleted);

            Assert.Equal(System.Data.ConnectionState.Open, interceptor.ConnectionStateAtPause);

            Assert.Equal(System.Data.ConnectionState.Closed, probe.InitialConnectionState);

            Assert.Same(probe.Context, interceptor.Context);

            Assert.Same(probe.Context!.Database.GetDbConnection(), interceptor.Connection);

            Assert.NotNull(probe.Admission!.Lease);

            Assert.Equal(1, interceptor.Callbacks);

            if (closeAdmission)
            {
                CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

                closing = observer.BeginOrResumeExclusive(owner).Value;

                draining = observer.DrainRequestAndWorkAsync(closing, cancellation.Token).AsTask();

                await observer.StageOne.WaitUntilEnteredAsync();

                Assert.False(draining.IsCompleted);

                Assert.Equal(0, observer.StageOne.CompletedCount);
            }
        }
        finally
        {
            interceptor.Checkpoint.Release();

            using HttpResponseMessage response = await request.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, cancellation.Token);

            Assert.True(body?.IsSuccess);

            Assert.Equal(0, body!.Data!.SessionCount);

            Assert.NotNull(probe.Admission);
        }

        await probe.Scope!.WaitUntilDisposedAsync();

        await Assert.Single(observer.RequestAttempts).WaitUntilDisposedAsync();

        Assert.Null(probe.Admission!.Lease);

        if (draining is not null)
        {
            Assert.True((await draining.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal(1, observer.StageOne.CompletedCount);

            await closing!.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Endpoint_probe_executes_only_after_authentication_and_successful_admission()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PostAdmissionEndpointProbe probe = new("/v1/models");

        await using ArcanumWebApplicationFactory factory = new()
        {
            BeforeEndpoint = probe.InvokeAsync,

            ServiceOverrides = services =>
            {
                services.AddScoped<MaintenanceScopeSentinel>();

                services.RemoveAll<IGrimoireConnectionAdmissionGate>();

                services.AddSingleton<IGrimoireConnectionAdmissionGate>(sp =>
                    new GrimoireMaintenanceAdmissionObserver(sp.GetRequiredService<GrimoireConnectionAdmissionGate>()));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        client.DefaultRequestHeaders.Add(PostAdmissionEndpointProbe.Header, probe.RequestId);

        GrimoireMaintenanceAdmissionObserver observer = (GrimoireMaintenanceAdmissionObserver)factory.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        using HttpRequestMessage unauthorized = new(HttpMethod.Get, "/v1/models");

        unauthorized.Headers.Add(ArcanumApiHeaders.ApiKey, "wrong-key");

        using HttpResponseMessage denied = await client.SendAsync(unauthorized);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        Assert.Null(probe.Admission);

        Assert.Empty(observer.RequestAttempts);

        using HttpResponseMessage admitted = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);

        Assert.NotNull(probe.Admission);

        Assert.NotNull(probe.ObservedLease);

        Assert.Equal(1, probe.Invocations);

        await probe.Scope!.WaitUntilDisposedAsync();

        await Assert.Single(observer.RequestAttempts).WaitUntilDisposedAsync();

        CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()));

        await using IGrimoireClosingOwner closing = observer.BeginOrResumeExclusive(owner).Value;

        using HttpResponseMessage refused = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        Assert.Equal(2, observer.RequestAttempts.Count);

        Assert.False(observer.RequestAttempts[1].Acquired);

        Assert.Equal(1, probe.Invocations);
    }

    [SkippableFact]
    public async Task Factory_command_control_pauses_EF_after_native_open_and_releases_its_real_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PausingFactoryCommandInterceptor interceptor = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            AdditionalDbContextInterceptors = [interceptor],
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));

        Task<int> query = db.Database.SqlQueryRaw<int>(
            """
            SELECT (SELECT COUNT(*) FROM "Sessions")
                 + (SELECT COUNT(*) FROM "Entries")
                 + (SELECT COUNT(*) FROM "Campaigns") AS Value
            """).SingleAsync(cancellation.Token);

        try
        {
            await interceptor.Checkpoint.WaitUntilReachedAsync();

            Assert.False(query.IsCompleted);

            Assert.Equal(System.Data.ConnectionState.Open, interceptor.ConnectionStateAtPause);
        }
        finally
        {
            interceptor.Checkpoint.Release();

            await query.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, await query);
    }

    [SkippableFact]
    public async Task Additional_interceptors_follow_enrolment_and_observe_real_EF_execution()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CountingCommandInterceptor interceptor = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            AdditionalDbContextInterceptors = [interceptor],
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DbContextOptions<ArcanumDbContext> options = scope.ServiceProvider.GetRequiredService<DbContextOptions<ArcanumDbContext>>();

        IInterceptor[] installed = options.FindExtension<CoreOptionsExtension>()!.Interceptors!.ToArray();

        Assert.IsType<CovenantConnectionEnrolmentInterceptor>(installed[0]);

        Assert.Same(interceptor, installed[1]);

        int value = await db.Database.SqlQueryRaw<int>("SELECT 257 AS Value").SingleAsync();

        Assert.Equal(257, value);

        Assert.Equal(1, interceptor.MatchingCommands);
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _matchingCommands;

        internal int MatchingCommands => Volatile.Read(ref _matchingCommands);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT 257 AS Value", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _matchingCommands);
            }

            return ValueTask.FromResult(result);
        }
    }
}
