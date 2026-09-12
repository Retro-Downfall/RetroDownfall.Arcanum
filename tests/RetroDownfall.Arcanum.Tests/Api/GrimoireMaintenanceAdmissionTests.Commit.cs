using System.Data;

using System.Data.Common;

using System.Net;

using System.Net.Http.Json;

using System.Text.Json;

using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GrimoireMaintenanceAdmissionTests
{
    // A reset that skips either drain, cancels admitted finite work, admits B early, accepts the
    // losing open, or reopens without its durable commit/retirement evidence must fail here.
    [SkippableFact]
    public async Task Authenticated_direct_reset_drains_the_full_host_and_reopens_only_the_next_generation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        CovenantOfflineTransitionEpochsV1 sourceEpochs;

        Guid sourceDataset;

        await using (var seed = await CovenantCanonicalErasureFixture.AttachAsync(
            new DesignTimeGrimoireConnectionFactory(harness.Factory.Services.GetRequiredService<IGrimoireDbPassphraseSource>()),
            harness.Factory.Services.GetRequiredService<ICovenantSqliteConnectionInitializer>(), harness.Drain, timeout.Token))
        {
            await seed.SeedAcceptanceStateAsync(timeout.Token);

            Assert.Equal(1, await seed.CountAsync("covenant_entries", timeout.Token));

            Assert.Equal(1, await seed.CountAsync("artifact_sensitivity", timeout.Token));

            sourceEpochs = await seed.ReadEpochsAsync(timeout.Token);

            sourceDataset = (await seed.ReadDatasetGenerationAsync(timeout.Token))!.Value;
        }

        harness.Admission.StageOne.HoldAfterCompletion = true;

        harness.Admission.StageTwo.HoldAfterCompletion = true;

        long losingGeneration = harness.Admission.CurrentGeneration;

        Task<HttpResponseMessage> stats = harness.StartStatsAsync(timeout.Token);

        await harness.Stats.Checkpoint.WaitUntilReachedAsync();

        MaintenanceScopeSentinel statsScope = Assert.IsType<MaintenanceScopeSentinel>(harness.StatsProbe.Scope);

        Assert.Equal(ConnectionState.Open, harness.Stats.ConnectionStateAtPause);

        Assert.False(stats.IsCompleted);

        string downloadPath = $"/api/sessions/{harness.SessionId}/attachments/{harness.DownloadAttachment.Id}/content";

        using HttpRequestMessage downloadRequest = harness.CreateObservedRequest(HttpMethod.Get, downloadPath);

        using HttpResponseMessage download = await harness.Client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        await using Stream downloadStream = await download.Content.ReadAsStreamAsync(timeout.Token);

        byte[] prefix = new byte[8];

        await downloadStream.ReadExactlyAsync(prefix, timeout.Token);

        await harness.Blobs.Checkpoint.WaitUntilReachedAsync();

        Assert.Equal(harness.DownloadBytes[..8], prefix);

        Task<MaintenanceSseResponse> openingChat = harness.OpenChatAsync();

        await harness.Chat.Checkpoint.WaitUntilReachedAsync();

        await using MaintenanceSseResponse chat = await openingChat.WaitAsync(timeout.Token);

        var streams = await StartCommitStreamsAsync(harness);

        SessionAttachmentIndexRequest requestA = new(harness.IndexingAttachmentA.Id, harness.SessionId, Attempt: 3);

        SessionAttachmentIndexRequest requestB = new(harness.IndexingAttachmentB.Id, harness.SessionId, Attempt: 7);

        string[] beforeB = await OrdinaryIndexingSnapshotAsync(harness, requestB.AttachmentId);

        Assert.True(harness.Indexing.TryEnqueue(requestA));

        await harness.Weave.Checkpoint.WaitUntilReachedAsync();

        WorkerScopeObservation workerScope = Assert.Single(harness.WorkerScopes.Scopes, scope => scope.Disposals == 0);

        var worker = harness.Admission.WorkAttempts.Last(attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && attempt.Acquired);

        IGrimoireWorkLease staleWork = Assert.IsAssignableFrom<IGrimoireWorkLease>(worker.Lease);

        var workerEffect = Assert.Single(harness.Admission.Effects, effect => effect.Kind == GrimoireWorkKind.SessionAttachmentIndexing && effect.Disposals == 0);

        int admittedWorkerScopes = harness.WorkerScopes.Scopes.Count;

        int admittedEffects = harness.Admission.Effects.Count;

        DataRetentionPlan plan = await harness.PlanAsync(GrimoireTransitionEntryPoint.DirectCovenantReset);

        Assert.Empty(plan.Blockers);

        Assert.Equal(0, plan.Covenant!.ManagedFiles);

        await using AsyncServiceScope openingScope = harness.Factory.Services.CreateAsyncScope();

        ArcanumDbContext openingDb = openingScope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await openingDb.Database.CloseConnectionAsync();

        harness.EfOpen.TargetContext = openingDb;

        Task opening = Task.Factory.StartNew(() => openingDb.Database.OpenConnectionAsync(timeout.Token),
            timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await harness.EfOpen.Checkpoint.WaitUntilReachedAsync();

        var losingTicket = Assert.Single(harness.Admission.Tickets, ticket => ReferenceEquals(ticket.Connection, harness.EfOpen.Connection) && ticket.TerminalCount == 0);

        Assert.Equal(losingGeneration, losingTicket.Generation);

        Assert.Equal(ConnectionState.Closed, losingTicket.Connection.State);

        using HttpRequestMessage apply = harness.CreateApplyRequest(GrimoireTransitionEntryPoint.DirectCovenantReset, plan.PlanId, Guid.Empty);

        Task<HttpResponseMessage> reset = harness.Client.SendAsync(apply, timeout.Token);

        try
        {
            await harness.Admission.StageOne.WaitUntilEnteredAsync();

            AssertStageOnePending(harness, reset);

            await AssertCommitRefusalsAsync(harness, timeout.Token);

            harness.IndexingLogs.ExpectDeferred(requestB.AttachmentId);

            Assert.True(harness.Indexing.TryEnqueue(requestB));

            await FinishCommitStreamsAsync(harness, streams, reset);

            AssertStageOnePending(harness, reset);

            Assert.Equal(0, harness.Chat.Completed);

            Assert.Equal(0, workerScope.Disposals);

            harness.Stats.Checkpoint.Release();

            using (HttpResponseMessage response = await stats.WaitAsync(timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, timeout.Token);

                Assert.True(body!.IsSuccess);
            }

            await statsScope.WaitUntilDisposedAsync();

            Assert.Equal(1, harness.Stats.Callbacks);

            Assert.Equal(1, harness.StatsProbe.Invocations);

            AssertStageOnePending(harness, reset);

            harness.Blobs.Checkpoint.Release();

            using MemoryStream remainder = new();

            await downloadStream.CopyToAsync(remainder, timeout.Token);

            Assert.Equal(harness.DownloadBytes, prefix.Concat(remainder.ToArray()).ToArray());

            Assert.Equal(HttpStatusCode.OK, download.StatusCode);

            await harness.Blobs.WaitUntilDisposedAsync();

            await harness.ProbeForPath(downloadPath).Scope!.WaitUntilDisposedAsync();

            AssertStageOnePending(harness, reset);

            harness.Chat.Checkpoint.Release();

            Assert.Equal(1, await chat.ReadTerminalCountToEndAsync());

            List<string> answer = [];

            foreach (string frame in chat.Frames.Where(frame => frame.StartsWith("data: {", StringComparison.Ordinal)))
            {
                using JsonDocument document = JsonDocument.Parse(frame[6..]);

                foreach (JsonElement choice in document.RootElement.GetProperty("choices").EnumerateArray())
                {
                    if (choice.GetProperty("delta").TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        answer.Add(content.GetString()!);
                    }
                }
            }

            Assert.Equal("controlled-billable-prefixcontrolled-billable-complete", string.Concat(answer));

            Assert.Equal("data: [DONE]", chat.Frames[^1]);

            using (JsonDocument usageFrame = JsonDocument.Parse(Assert.Single(chat.Frames,
                frame => frame.StartsWith("data: {", StringComparison.Ordinal) && frame.Contains("\"usage\":{", StringComparison.Ordinal))[6..]))
            {
                JsonElement usage = usageFrame.RootElement.GetProperty("usage");

                Assert.Equal(7, usage.GetProperty("prompt_tokens").GetInt32());

                Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());

                Assert.Equal(9, usage.GetProperty("total_tokens").GetInt32());
            }

            await harness.Chat.WaitUntilDisposedAsync();

            await harness.ProbeForPath("/v1/chat/completions").Scope!.WaitUntilDisposedAsync();

            Assert.Equal(1, harness.Chat.Completed);

            Assert.Equal(1, harness.Chat.Disposals);

            AssertStageOnePending(harness, reset);

            harness.Weave.Checkpoint.Release();

            await workerScope.WaitUntilDisposedAsync();

            await worker.WaitUntilDisposedAsync();

            await harness.Admission.StageOne.AfterCompletion.WaitUntilReachedAsync();

            await harness.IndexingLogs.WaitUntilDeferredAsync();

            int scopesAfterA = harness.WorkerScopes.Scopes.Count;

            int effectsAfterA = harness.Admission.Effects.Count;

            Assert.Equal(admittedWorkerScopes, scopesAfterA);

            Assert.Equal(admittedEffects, effectsAfterA);

            Assert.Same(requestB, Assert.Single(harness.Indexing.DeferredRequests));

            Assert.True(harness.Indexing.TryEnqueue(requestB));

            Assert.Same(requestB, Assert.Single(harness.Indexing.DeferredRequests));

            Assert.Equal(scopesAfterA, harness.WorkerScopes.Scopes.Count);

            Assert.Equal(effectsAfterA, harness.Admission.Effects.Count);

            Assert.Equal(1, workerScope.Disposals);

            Assert.Equal(1, worker.Disposals);

            Assert.Equal(1, workerEffect.TerminalCount);

            Assert.Equal(1, harness.Weave.Calls);

            Assert.Equal(1, harness.Weave.Completed);

            Assert.Equal(0, harness.Weave.Cancellations);

            Assert.Contains(harness.Admission.WorkAttempts, attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && !attempt.Acquired);

            Assert.Equal(0, harness.Admission.StageTwo.EnteredCount);

            harness.Admission.StageOne.AfterCompletion.Release();

            await harness.Admission.StageTwo.WaitUntilEnteredAsync();

            Assert.Equal(losingGeneration + 1, harness.Admission.CurrentGeneration);

            Assert.Equal(1, harness.Admission.StageOne.CompletedCount);

            Assert.Equal(0, harness.Admission.StageTwo.CompletedCount);

            Assert.Equal(0, losingTicket.TerminalCount);

            Assert.False(opening.IsCompleted);

            harness.EfOpen.Checkpoint.Release();

            await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() => opening.WaitAsync(timeout.Token));

            await harness.Admission.StageTwo.AfterCompletion.WaitUntilReachedAsync();

            Assert.Equal(ConnectionState.Closed, losingTicket.Connection.State);

            Assert.Equal("refused-after-open", losingTicket.Terminal);

            Assert.Equal(1, losingTicket.TerminalCount);

            Assert.Equal(1, losingTicket.Disposals);

            Assert.Throws<ObjectDisposedException>(() => losingTicket.Ticket!.MarkOpened());

            var clear = Assert.Single(harness.Drain.PoolClears, item => ReferenceEquals(item.Connection, losingTicket.Connection));

            Assert.Equal(ConnectionState.Closed, clear.StateBefore);

            Assert.True(clear.Result.IsSuccess);

            Assert.Equal(0, harness.Drain.Enrolments.Select(item => item.Connection)
                .Distinct().Count(connection => connection.State != ConnectionState.Closed));

            Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-wal"));

            Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-shm"));

            Assert.False(reset.IsCompleted);

            Assert.Equal(beforeB, await ClosedIndexingSnapshotAsync(harness, requestB.AttachmentId, timeout.Token));

            Assert.Equal(scopesAfterA, harness.WorkerScopes.Scopes.Count);

            Assert.Equal(effectsAfterA, harness.Admission.Effects.Count);

            harness.Admission.StageTwo.AfterCompletion.Release();

            using HttpResponseMessage completed = await reset.WaitAsync(timeout.Token);

            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

            var result = await completed.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult, timeout.Token);

            Assert.True(result!.IsSuccess);

            await AssertCommittedDirectResetAsync(harness, plan, result.Data!, losingGeneration, sourceDataset, sourceEpochs, timeout.Token);

            Assert.False(staleWork.TryBeginExternalEffectGroup(out var staleEffect));

            Assert.Null(staleEffect);

            Assert.All(streams, stream => Assert.True(stream.Probe.ObservedLease!.MaintenanceRevocation.IsCancellationRequested));

            Assert.Equal(1, losingTicket.TerminalCount);

            Assert.True(harness.HostLogs.Unexpected.Count == 0, string.Join("\n", harness.HostLogs.Unexpected));
        }
        finally
        {
            harness.Admission.StageOne.AfterCompletion.Release();

            harness.Admission.StageTwo.AfterCompletion.Release();

            harness.Stats.Checkpoint.Release();

            harness.Blobs.Checkpoint.Release();

            harness.Chat.Checkpoint.Release();

            harness.Weave.Checkpoint.Release();

            harness.ReleaseStreamProducers();

            harness.EfOpen.Checkpoint.Release();

            await Task.WhenAll(stats, opening, reset).ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // A reset that skips either drain, cancels admitted finite work, admits B early, accepts the
    // losing open, or reopens without its durable commit/retirement evidence must fail here.
    [SkippableFact]
    public async Task Authenticated_standalone_factory_erasure_drains_the_full_host_and_reopens_only_the_next_generation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using GrimoireMaintenanceAdmissionHarness harness = await GrimoireMaintenanceAdmissionHarness.StartAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        CovenantOfflineTransitionEpochsV1 sourceEpochs;

        Guid sourceDataset;

        await using (var seed = await CovenantCanonicalErasureFixture.AttachAsync(
            new DesignTimeGrimoireConnectionFactory(harness.Factory.Services.GetRequiredService<IGrimoireDbPassphraseSource>()),
            harness.Factory.Services.GetRequiredService<ICovenantSqliteConnectionInitializer>(), harness.Drain, timeout.Token))
        {
            await seed.SeedAcceptanceStateAsync(timeout.Token);

            Assert.Equal(1, await seed.CountAsync("covenant_entries", timeout.Token));

            Assert.Equal(1, await seed.CountAsync("artifact_sensitivity", timeout.Token));

            sourceEpochs = await seed.ReadEpochsAsync(timeout.Token);

            sourceDataset = (await seed.ReadDatasetGenerationAsync(timeout.Token))!.Value;
        }

        harness.Admission.StageOne.HoldAfterCompletion = true;

        harness.Admission.StageTwo.HoldAfterCompletion = true;

        long losingGeneration = harness.Admission.CurrentGeneration;

        Task<HttpResponseMessage> stats = harness.StartStatsAsync(timeout.Token);

        await harness.Stats.Checkpoint.WaitUntilReachedAsync();

        MaintenanceScopeSentinel statsScope = Assert.IsType<MaintenanceScopeSentinel>(harness.StatsProbe.Scope);

        Assert.Equal(ConnectionState.Open, harness.Stats.ConnectionStateAtPause);

        Assert.False(stats.IsCompleted);

        string downloadPath = $"/api/sessions/{harness.SessionId}/attachments/{harness.DownloadAttachment.Id}/content";

        using HttpRequestMessage downloadRequest = harness.CreateObservedRequest(HttpMethod.Get, downloadPath);

        using HttpResponseMessage download = await harness.Client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        await using Stream downloadStream = await download.Content.ReadAsStreamAsync(timeout.Token);

        byte[] prefix = new byte[8];

        await downloadStream.ReadExactlyAsync(prefix, timeout.Token);

        await harness.Blobs.Checkpoint.WaitUntilReachedAsync();

        Assert.Equal(harness.DownloadBytes[..8], prefix);

        Task<MaintenanceSseResponse> openingChat = harness.OpenChatAsync();

        await harness.Chat.Checkpoint.WaitUntilReachedAsync();

        await using MaintenanceSseResponse chat = await openingChat.WaitAsync(timeout.Token);

        var streams = await StartCommitStreamsAsync(harness);

        SessionAttachmentIndexRequest requestA = new(harness.IndexingAttachmentA.Id, harness.SessionId, Attempt: 3);

        SessionAttachmentIndexRequest requestB = new(harness.IndexingAttachmentB.Id, harness.SessionId, Attempt: 7);

        string[] beforeB = await OrdinaryIndexingSnapshotAsync(harness, requestB.AttachmentId);

        Assert.True(harness.Indexing.TryEnqueue(requestA));

        await harness.Weave.Checkpoint.WaitUntilReachedAsync();

        WorkerScopeObservation workerScope = Assert.Single(harness.WorkerScopes.Scopes, scope => scope.Disposals == 0);

        var worker = harness.Admission.WorkAttempts.Last(attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && attempt.Acquired);

        IGrimoireWorkLease staleWork = Assert.IsAssignableFrom<IGrimoireWorkLease>(worker.Lease);

        var workerEffect = Assert.Single(harness.Admission.Effects, effect => effect.Kind == GrimoireWorkKind.SessionAttachmentIndexing && effect.Disposals == 0);

        int admittedWorkerScopes = harness.WorkerScopes.Scopes.Count;

        int admittedEffects = harness.Admission.Effects.Count;

        DataRetentionPlan plan = await harness.PlanAsync(GrimoireTransitionEntryPoint.StandaloneFactoryReset);

        Assert.Empty(plan.Blockers);

        DataRetentionConflict activeInference = Assert.Single(plan.Conflicts);

        Assert.Equal("Data.InferenceRunActive", activeInference.Code);

        Assert.Equal("An active inference run protects its inputs and accounting chain.", activeInference.Message);

        Assert.True(Guid.TryParse(activeInference.ResourceId, out _));

        Assert.Equal(0, plan.Covenant!.ManagedFiles);

        await using AsyncServiceScope openingScope = harness.Factory.Services.CreateAsyncScope();

        IGrimoireOrdinaryConnectionFactory rawFactory = openingScope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<IGrimoireOrdinaryConnectionLease>> opening = Task.Factory.StartNew(async () =>
        {
            using IDisposable selected = harness.RawOpen.SelectCurrentExecution();

            return await rawFactory.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, timeout.Token);
        }, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await harness.RawOpen.Checkpoint.WaitUntilReachedAsync();

        Assert.Equal(1, harness.RawOpen.SelectedConstructions);

        var losingTicket = Assert.Single(harness.Admission.Tickets, ticket => ticket.TerminalCount == 0);

        Assert.Equal(losingGeneration, losingTicket.Generation);

        Assert.Equal(ConnectionState.Closed, losingTicket.Connection.State);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage apply = harness.CreateApplyRequest(GrimoireTransitionEntryPoint.StandaloneFactoryReset, plan.PlanId, requestedOperationId);

        FactoryResetRequest wireRequest = (await apply.Content!.ReadFromJsonAsync(ArcanumJsonContext.Default.FactoryResetRequest, timeout.Token))!;

        Assert.Null(wireRequest.InstallationResetHandoff);

        Assert.Equal(plan.PlanId, wireRequest.ExpectedPlanId);

        Assert.Equal(requestedOperationId, wireRequest.RequestedOperationId);

        Task<HttpResponseMessage> reset = harness.Client.SendAsync(apply, timeout.Token);

        try
        {
            await harness.Admission.StageOne.WaitUntilEnteredAsync();

            AssertStageOnePending(harness, reset);

            await AssertCommitRefusalsAsync(harness, timeout.Token);

            harness.IndexingLogs.ExpectDeferred(requestB.AttachmentId);

            Assert.True(harness.Indexing.TryEnqueue(requestB));

            await FinishCommitStreamsAsync(harness, streams, reset);

            AssertStageOnePending(harness, reset);

            Assert.Equal(0, harness.Chat.Completed);

            Assert.Equal(0, workerScope.Disposals);

            harness.Stats.Checkpoint.Release();

            using (HttpResponseMessage response = await stats.WaitAsync(timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var body = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, timeout.Token);

                Assert.True(body!.IsSuccess);
            }

            await statsScope.WaitUntilDisposedAsync();

            Assert.Equal(1, harness.Stats.Callbacks);

            Assert.Equal(1, harness.StatsProbe.Invocations);

            AssertStageOnePending(harness, reset);

            harness.Blobs.Checkpoint.Release();

            using MemoryStream remainder = new();

            await downloadStream.CopyToAsync(remainder, timeout.Token);

            Assert.Equal(harness.DownloadBytes, prefix.Concat(remainder.ToArray()).ToArray());

            Assert.Equal(HttpStatusCode.OK, download.StatusCode);

            await harness.Blobs.WaitUntilDisposedAsync();

            await harness.ProbeForPath(downloadPath).Scope!.WaitUntilDisposedAsync();

            AssertStageOnePending(harness, reset);

            harness.Chat.Checkpoint.Release();

            Assert.Equal(1, await chat.ReadTerminalCountToEndAsync());

            List<string> answer = [];

            foreach (string frame in chat.Frames.Where(frame => frame.StartsWith("data: {", StringComparison.Ordinal)))
            {
                using JsonDocument document = JsonDocument.Parse(frame[6..]);

                foreach (JsonElement choice in document.RootElement.GetProperty("choices").EnumerateArray())
                {
                    if (choice.GetProperty("delta").TryGetProperty("content", out JsonElement content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        answer.Add(content.GetString()!);
                    }
                }
            }

            Assert.Equal("controlled-billable-prefixcontrolled-billable-complete", string.Concat(answer));

            Assert.Equal("data: [DONE]", chat.Frames[^1]);

            using (JsonDocument usageFrame = JsonDocument.Parse(Assert.Single(chat.Frames,
                frame => frame.StartsWith("data: {", StringComparison.Ordinal) && frame.Contains("\"usage\":{", StringComparison.Ordinal))[6..]))
            {
                JsonElement usage = usageFrame.RootElement.GetProperty("usage");

                Assert.Equal(7, usage.GetProperty("prompt_tokens").GetInt32());

                Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());

                Assert.Equal(9, usage.GetProperty("total_tokens").GetInt32());
            }

            await harness.Chat.WaitUntilDisposedAsync();

            await harness.ProbeForPath("/v1/chat/completions").Scope!.WaitUntilDisposedAsync();

            Assert.Equal(1, harness.Chat.Completed);

            Assert.Equal(1, harness.Chat.Disposals);

            AssertStageOnePending(harness, reset);

            harness.Weave.Checkpoint.Release();

            await workerScope.WaitUntilDisposedAsync();

            await worker.WaitUntilDisposedAsync();

            await harness.Admission.StageOne.AfterCompletion.WaitUntilReachedAsync();

            await harness.IndexingLogs.WaitUntilDeferredAsync();

            int scopesAfterA = harness.WorkerScopes.Scopes.Count;

            int effectsAfterA = harness.Admission.Effects.Count;

            Assert.Equal(admittedWorkerScopes, scopesAfterA);

            Assert.Equal(admittedEffects, effectsAfterA);

            Assert.Same(requestB, Assert.Single(harness.Indexing.DeferredRequests));

            Assert.True(harness.Indexing.TryEnqueue(requestB));

            Assert.Same(requestB, Assert.Single(harness.Indexing.DeferredRequests));

            Assert.Equal(scopesAfterA, harness.WorkerScopes.Scopes.Count);

            Assert.Equal(effectsAfterA, harness.Admission.Effects.Count);

            Assert.Equal(1, workerScope.Disposals);

            Assert.Equal(1, worker.Disposals);

            Assert.Equal(1, workerEffect.TerminalCount);

            Assert.Equal(1, harness.Weave.Calls);

            Assert.Equal(1, harness.Weave.Completed);

            Assert.Equal(0, harness.Weave.Cancellations);

            Assert.Contains(harness.Admission.WorkAttempts, attempt => attempt.Kind == GrimoireWorkKind.SessionAttachmentIndexing && !attempt.Acquired);

            Assert.Equal(0, harness.Admission.StageTwo.EnteredCount);

            harness.Admission.StageOne.AfterCompletion.Release();

            await harness.Admission.StageTwo.WaitUntilEnteredAsync();

            Assert.Equal(losingGeneration + 1, harness.Admission.CurrentGeneration);

            Assert.Equal(1, harness.Admission.StageOne.CompletedCount);

            Assert.Equal(0, harness.Admission.StageTwo.CompletedCount);

            Assert.Equal(0, losingTicket.TerminalCount);

            Assert.False(opening.IsCompleted);

            harness.RawOpen.Checkpoint.Release();

            var refusedOpen = await opening.WaitAsync(timeout.Token);

            if (refusedOpen.IsSuccess)
            {
                await refusedOpen.Value.DisposeAsync();
            }

            Assert.True(refusedOpen.IsFailure);

            Assert.Equal(GrimoireMaintenanceUnavailableException.Code, refusedOpen.Error.Code);

            Assert.Same(losingTicket.Connection, Assert.Single(harness.RawOpen.ClearedConnections));

            await harness.Admission.StageTwo.AfterCompletion.WaitUntilReachedAsync();

            Assert.Equal(ConnectionState.Closed, losingTicket.Connection.State);

            Assert.Equal("refused-after-open", losingTicket.Terminal);

            Assert.Equal(1, losingTicket.TerminalCount);

            Assert.Equal(1, losingTicket.Disposals);

            Assert.Throws<ObjectDisposedException>(() => losingTicket.Ticket!.MarkOpened());

            var clear = Assert.Single(harness.Drain.PoolClears, item => ReferenceEquals(item.Connection, losingTicket.Connection));

            Assert.Equal(ConnectionState.Closed, clear.StateBefore);

            Assert.True(clear.Result.IsSuccess);

            Assert.Equal(0, harness.Drain.Enrolments.Select(item => item.Connection)
                .Distinct().Count(connection => connection.State != ConnectionState.Closed));

            Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-wal"));

            Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-shm"));

            Assert.False(reset.IsCompleted);

            FactoryDrainEvidence? drained = null;

            Assert.Equal(beforeB, await ClosedIndexingSnapshotAsync(harness, requestB.AttachmentId, timeout.Token,
                async connection => drained = await ReadFactoryDrainEvidenceAsync(harness, (SqliteConnection)connection, plan, activeInference, timeout.Token)));

            Assert.Equal(scopesAfterA, harness.WorkerScopes.Scopes.Count);

            Assert.Equal(effectsAfterA, harness.Admission.Effects.Count);

            harness.Admission.StageTwo.AfterCompletion.Release();

            using HttpResponseMessage completed = await reset.WaitAsync(timeout.Token);

            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

            var result = await completed.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult, timeout.Token);

            Assert.True(result!.IsSuccess);

            await AssertCommittedFactoryResetAsync(harness, plan, result.Data!, requestedOperationId, losingGeneration, sourceDataset, sourceEpochs, drained!, timeout.Token);

            Assert.False(staleWork.TryBeginExternalEffectGroup(out var staleEffect));

            Assert.Null(staleEffect);

            Assert.All(streams, stream => Assert.True(stream.Probe.ObservedLease!.MaintenanceRevocation.IsCancellationRequested));

            Assert.Equal(1, losingTicket.TerminalCount);

            Assert.True(harness.HostLogs.Unexpected.Count == 0, string.Join("\n", harness.HostLogs.Unexpected));
        }
        finally
        {
            harness.Admission.StageOne.AfterCompletion.Release();

            harness.Admission.StageTwo.AfterCompletion.Release();

            harness.Stats.Checkpoint.Release();

            harness.Blobs.Checkpoint.Release();

            harness.Chat.Checkpoint.Release();

            harness.Weave.Checkpoint.Release();

            harness.ReleaseStreamProducers();

            harness.RawOpen.Checkpoint.Release();

            await Task.WhenAll(stats, opening, reset).ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed record FactoryDrainEvidence(long Rows, long DerivedRecords, string[] Files, long AuditBytes);

    private static async Task<FactoryDrainEvidence> ReadFactoryDrainEvidenceAsync(GrimoireMaintenanceAdmissionHarness harness,
        SqliteConnection connection, DataRetentionPlan plan, DataRetentionConflict activeInference, CancellationToken token)
    {
        // These checks run at the held stage-two completion, before the coordinator receives
        // closed authority and before its pre-canonical orphan guard can execute.
        Assert.DoesNotContain("transition:verified", harness.Journal.Steps);

        Assert.All(harness.Journal.Publications, publication => Assert.Equal(CovenantResetPhase.InventoryPrepared, publication.Payload.LastCompletedPhase));

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT r.Id, r.Status, r.CompletedAt, b.Status, b.InputTokens, b.OutputTokens, b.Provider, b.Model
            FROM InferenceRuns r JOIN BillableOperations b ON b.RunId = r.Id;
            """;

        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(await reader.ReadAsync(token));

            Assert.Equal(Guid.Parse(activeInference.ResourceId), Guid.Parse(reader.GetString(0)));

            Assert.Equal((long)InferenceRunStatus.Completed, reader.GetInt64(1));

            Assert.False(reader.IsDBNull(2));

            Assert.Equal((long)BillableOperationStatus.Completed, reader.GetInt64(3));

            Assert.Equal(7, reader.GetInt64(4));

            Assert.Equal(2, reader.GetInt64(5));

            Assert.Equal("test", reader.GetString(6));

            Assert.Equal("mistral:latest", reader.GetString(7));

            Assert.False(await reader.ReadAsync(token));
        }

        command.CommandText = "SELECT COUNT(*) FROM InferenceRuns WHERE Status = 0;";

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(token))!);

        command.CommandText = "SELECT Status, AttemptCount, FailureReason FROM session_attachment_index_state WHERE AttachmentId = $id;";

        command.Parameters.AddWithValue("$id", harness.IndexingAttachmentA.Id.ToString().ToUpperInvariant());

        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            Assert.True(await reader.ReadAsync(token));

            Assert.Equal("Indexed", reader.GetString(0));

            Assert.Equal(3, reader.GetInt64(1));

            Assert.True(reader.IsDBNull(2));

            Assert.False(await reader.ReadAsync(token));
        }

        command.Parameters.Clear();

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Sessions) + (SELECT COUNT(*) FROM Entries)
                + (SELECT COUNT(*) FROM SessionAttachments) + (SELECT COUNT(*) FROM InferenceRuns)
                + (SELECT COUNT(*) FROM BillableOperations);
            """;

        long rows = (long)(await command.ExecuteScalarAsync(token))!;

        Assert.Equal(7, rows);

        Assert.Equal(6, plan.Rows);

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Entries_fts) + (SELECT COUNT(*) FROM session_attachment_index_state)
                + (SELECT COUNT(*) FROM session_attachment_chunks) + (SELECT COUNT(*) FROM session_attachment_embeddings);
            """;

        long derived = (long)(await command.ExecuteScalarAsync(token))!;

        // This host uses blob embeddings; its optional vector mirror is not installed.
        Assert.Equal(plan.DerivedRecords + 2, derived);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'session_attachment_embeddings_vec';";

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(token))!);

        string auditFile = Assert.Single(Directory.GetFiles(ArcanumPaths.GrimoireDirectory, "audit-????????.jsonl"));

        string[] files = [.. new[] { harness.DownloadAttachment, harness.IndexingAttachmentA, harness.IndexingAttachmentB }
            .Select(attachment => Path.Combine(ArcanumPaths.AttachmentsDirectory, attachment.RelativePath)), auditFile];

        Assert.All(files, file => Assert.True(File.Exists(file)));

        long auditBytes = new FileInfo(auditFile).Length;

        Assert.True(auditBytes > 0);

        Assert.Equal(plan.EstimatedBytes, files.Take(3).Sum(file => new FileInfo(file).Length));

        var audit = Assert.Single(await harness.Factory.Services.GetRequiredService<IInferenceAuditLogger>()
            .QueryAsync(null, null, "mistral:latest", null, 10, token));

        Assert.Equal((7, 2, 9), (audit.PromptTokens, audit.CompletionTokens, audit.TotalTokens));

        Assert.Equal("stop", audit.FinishReason);

        return new(rows, derived, files, auditBytes);
    }

    private static async Task AssertFactoryCatalogAsync(SqliteConnection connection,
        DataRetentionFactoryTransitionLaunchV2 launch, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
            FROM covenant_state WHERE StateKey = 1;
            """;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));

            Assert.Equal(launch.TargetDatasetGeneration, new Guid((byte[])reader.GetValue(0)));

            Assert.Equal(launch.TargetEpochs, new CovenantOfflineTransitionEpochsV1(
                (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2), (ulong)reader.GetInt64(3)));

            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        foreach (string table in new[] { "covenant_entries", "covenant_versions", "covenant_heads", "artifact_sensitivity",
            "managed_file_write_intents", "Sessions", "Entries", "SessionAttachments", "session_attachment_chunks",
            "session_attachment_index_state", "session_attachment_embeddings", "Entries_fts", "BillableOperations", "InferenceRuns" })
        {
            command.CommandText = $"SELECT COUNT(*) FROM {table};";

            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0,
                $"Factory erasure must leave {table} empty.");
        }
    }

    private static async Task AssertCommittedFactoryResetAsync(GrimoireMaintenanceAdmissionHarness harness,
        DataRetentionPlan plan, DataRetentionApplyResult result, Guid requestedOperationId, long losingGeneration, Guid sourceDataset,
        CovenantOfflineTransitionEpochsV1 sourceEpochs, FactoryDrainEvidence drained, CancellationToken cancellationToken)
    {
        Assert.Equal(plan.PlanId, result.PlanId);

        Assert.Equal(requestedOperationId, result.RequestedOperationId);

        Assert.NotEqual(requestedOperationId, result.OperationId);

        Assert.True(result.Reconciled);

        Assert.Equal(plan.Rows + 1, result.RowsDeleted);

        Assert.Equal(plan.DerivedRecords + 2, result.DerivedRecordsDeleted);

        Assert.Equal(plan.Files + 1, result.FilesDeleted);

        Assert.Equal(plan.EstimatedBytes + drained.AuditBytes, result.EstimatedBytesDeleted);

        Assert.Equal(drained.Rows, result.RowsDeleted);

        Assert.Equal(drained.DerivedRecords, result.DerivedRecordsDeleted);

        Assert.Equal(drained.Files.LongLength, result.FilesDeleted);

        Assert.All(drained.Files, file => Assert.False(File.Exists(file)));

        Assert.True((await harness.Journal.CommittedLease!.RevalidateAsync(cancellationToken)).IsFailure);

        bool staleCallback = false;

        Assert.True(harness.Journal.CommittedLease.ExecuteWhileHeld(() =>
        {
            staleCallback = true;

            return RetroDownfall.Arcanum.Core.Primitives.Result.Success();
        }).IsFailure);

        Assert.False(staleCallback);

        Assert.NotEmpty(harness.Journal.ParentResolutions);

        Assert.All(harness.Journal.ParentResolutions, resolution =>
        {
            Assert.Equal(GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure, resolution.Kind);

            Assert.Null(resolution.CommittedBinding);

            Assert.True(resolution.Succeeded);

            Assert.False(resolution.HasSink);
        });

        var parent = await new InstallationResetActiveStore(harness.Journal.Publications.First().Raw.Location.GuardedDirectory,
            harness.Factory.Services.GetRequiredService<IOsCredentialStore>()).InspectAsync(cancellationToken);

        Assert.True(parent.IsSuccess);

        Assert.Equal(InstallationResetActiveRecoveryOutcome.NoActiveRecord, parent.Value.Outcome);

        Assert.Null(parent.Value.Publication);

        Assert.Empty(result.Blockers);

        Assert.Empty(result.Conflicts);

        Assert.Equal(losingGeneration + 1, harness.Admission.CurrentGeneration);

        var disposition = Assert.Single(harness.Admission.ClosedLeases);

        Assert.Equal([CovenantExclusiveLeaseDisposition.CommitAndReopen], disposition.Dispositions);

        Assert.Equal(1, disposition.Disposals);

        Assert.Equal(1, Assert.Single(harness.Admission.ClosingOwners).Disposals);

        await using AsyncServiceScope inspection = harness.Factory.Services.CreateAsyncScope();

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inspection.ServiceProvider
            .GetRequiredService<ILongRunningOperationStore>().GetAsync(result.OperationId, cancellationToken));

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

        Assert.Equal(2, operation.CheckpointVersion);

        var decoded = CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(operation.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(result.OperationId, decoded.Value.OperationId);

        Assert.Equal(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, decoded.Value.Operation);

        Assert.Equal(2, decoded.Value.Version);

        Assert.Equal(LongRunningOperationKinds.DataRetentionFactoryReset, operation.Kind);

        var inventory = plan.Covenant!;

        var expectedEffect = new CovenantErasureEffectDigestCalculator().Compute(new(
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure, plan.PlanId, sourceDataset,
            inventory.Rows, inventory.ManagedFiles, inventory.LocalArtifacts, inventory.AffectedSessions,
            inventory.PossibleDisclosures, inventory.DisclosureCountKind));

        Assert.True(expectedEffect.IsSuccess);

        Assert.Equal(Convert.ToHexStringLower(expectedEffect.Value.Bytes), decoded.Value.EffectDigest);

        Assert.All(harness.Journal.Publications, publication =>
            Assert.Equal(expectedEffect.Value, publication.Payload.Binding.EffectDigest));

        Assert.Null(operation.ParentOperationId);

        Assert.Null(operation.RootOperationId);

        Assert.Equal(operation.Kind, decoded.Value.OperationKind);

        Assert.Equal(sourceDataset, decoded.Value.SourceDatasetGeneration);

        Assert.Equal(sourceEpochs, decoded.Value.SourceEpochs);

        Assert.NotEqual(sourceDataset, decoded.Value.TargetDatasetGeneration);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(sourceEpochs.AcceleratorEpoch + 1,
            sourceEpochs.KeyReclamationEpoch + 1, sourceEpochs.EnvelopeKeyEpoch + 1), decoded.Value.TargetEpochs);

        Assert.Null(operation.TerminalErrorCode);

        Assert.NotNull(operation.CompletedAt);

        await AssertCommittedRetirementAsync(harness, operation, decoded.Value.TargetDatasetGeneration, cancellationToken);

        using HttpRequestMessage request = harness.CreateObservedRequest(HttpMethod.Get, "/api/grimoire/stats");

        using HttpResponseMessage response = await harness.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True((await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, cancellationToken))!.IsSuccess);

        Assert.Equal(losingGeneration + 1, harness.StatsProbe.ObservedLease!.Generation);

        Assert.True(harness.Admission.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out var work));

        await using (work!)
        {
            Assert.Equal(losingGeneration + 1, work!.Generation);
        }

        var fresh = await inspection.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken);

        Assert.True(fresh.IsSuccess);

        await using (fresh.Value)
        {
            Assert.Equal(ConnectionState.Open, fresh.Value.Connection.State);

            Assert.Equal(losingGeneration + 1, harness.Admission.Tickets.Last().Generation);

            await AssertFactoryCatalogAsync(fresh.Value.Connection, decoded.Value, cancellationToken);
        }

        var audit = await inspection.ServiceProvider.GetRequiredService<IInferenceAuditLogger>()
            .QueryAsync(null, null, "mistral:latest", null, 10, cancellationToken);

        Assert.Empty(audit);

        var covenant = await inspection.ServiceProvider.GetRequiredService<ICovenantOperationGate>()
            .AcquireInstallationReadAsync(cancellationToken);

        Assert.True(covenant.IsSuccess);

        await covenant.Value.DisposeAsync();

        using Microsoft.Data.Sqlite.SqliteConnection unused = new();

        Assert.True(harness.Admission.LastClosedLease!.AcquireScopedConnectionPermit(unused).IsFailure);

        Assert.True((await harness.Admission.LastClosedLease.AcquireMaintenanceIoLaneAsync(
            (_, _, _) => ValueTask.FromResult(true), cancellationToken)).IsFailure);
    }

    private static async Task AssertCommittedCatalogAsync(SqliteConnection connection,
        CovenantOfflineTransitionLaunchV4 launch, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch,
                (SELECT COUNT(*) FROM covenant_entries),
                (SELECT COUNT(*) FROM covenant_versions),
                (SELECT COUNT(*) FROM covenant_heads),
                (SELECT COUNT(*) FROM artifact_sensitivity),
                (SELECT COUNT(*) FROM managed_file_write_intents)
            FROM covenant_state WHERE StateKey = 1;
            """;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));

            Assert.Equal(launch.TargetDatasetGeneration, new Guid((byte[])reader.GetValue(0)));

            Assert.Equal(launch.TargetEpochs, new CovenantOfflineTransitionEpochsV1(
                (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2), (ulong)reader.GetInt64(3)));

            for (int column = 4; column < 9; column++)
            {
                Assert.True(reader.GetInt64(column) == 0, $"Expected empty {reader.GetName(column)}; observed {reader.GetInt64(column)}.");
            }

            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        command.CommandText = """
            SELECT b.InputTokens, b.OutputTokens, b.Status, r.Status, r.CompletedAt,
                b.Provider, b.Model, (SELECT COUNT(*) FROM BillableOperations),
                (SELECT COUNT(*) FROM InferenceRuns)
            FROM BillableOperations b JOIN InferenceRuns r ON r.Id = b.RunId
            WHERE b.InputTokens = 7 AND b.OutputTokens = 2;
            """;

        await using var billing = await command.ExecuteReaderAsync(cancellationToken);

        Assert.True(await billing.ReadAsync(cancellationToken));

        Assert.Equal(7, billing.GetInt64(0));

        Assert.Equal(2, billing.GetInt64(1));

        Assert.Equal((long)BillableOperationStatus.Completed, billing.GetInt64(2));

        Assert.Equal((long)InferenceRunStatus.Completed, billing.GetInt64(3));

        Assert.False(billing.IsDBNull(4));

        Assert.Equal("test", billing.GetString(5));

        Assert.Equal("mistral:latest", billing.GetString(6));

        Assert.Equal(1, billing.GetInt64(7));

        Assert.Equal(1, billing.GetInt64(8));

        Assert.False(await billing.ReadAsync(cancellationToken));
    }

    // The authenticated terminal protocol is shared; launch decoding and cleanup inventories
    // remain specific to each independently executed host test.
    private static async Task AssertCommittedRetirementAsync(GrimoireMaintenanceAdmissionHarness harness,
        LongRunningOperation operation, Guid targetDataset, CancellationToken cancellationToken)
    {
        RecordedMaintenancePublication reconciliation = harness.Journal.Publications.First(publication =>
            publication.Payload.Lifecycle.State == GrimoireOfflineTransitionState.DatabaseReconciliationPending);

        Assert.Equal(0, reconciliation.AcceptedGrimoireDispositions);

        Assert.True(harness.Journal.PublishedExactCandidate);

        Assert.Equal(targetDataset, harness.Journal.VerifiedCandidate!.Dataset.DatasetGeneration);

        Assert.True(Array.IndexOf(harness.Journal.Steps.ToArray(), "transition:verified")
            < Array.IndexOf(harness.Journal.Steps.ToArray(), "transition:published"));

        Assert.All(harness.Journal.Publications, publication => Assert.Equal(0, publication.AcceptedGrimoireDispositions));

        var suffix = harness.Journal.Publications.Where(publication => publication.Payload.Lifecycle.ReconciliationEvidence is not null).ToArray();

        Assert.Equal(Enum.GetValues<GrimoireOfflineTransitionReconciliationStep>(), suffix
            .Select(publication => publication.Payload.Lifecycle.ReconciliationEvidence!.Step).Distinct());

        Assert.All(suffix.Where(publication => publication.Payload.Lifecycle.ReconciliationEvidence!.Step
            >= GrimoireOfflineTransitionReconciliationStep.LaneClosed), publication => Assert.True(publication.MaintenanceLaneClosed));

        var terminal = harness.Journal.Publications.Last();

        Assert.Equal(GrimoireOfflineTransitionState.RetirementPending, terminal.Payload.Lifecycle.State);

        // Immutable reopen proof belongs to the typed verification suffix, not a synthetic
        // completion of the older erasure ladder's final phase.
        Assert.Equal(CovenantResetPhase.SidecarsVerified, terminal.Payload.LastCompletedPhase);

        Assert.True(terminal.Payload.Lifecycle.VerificationEvidence!.CandidateVerified);

        Assert.True(terminal.Payload.Lifecycle.VerificationEvidence.RuntimeCovenantAuthorityVerified);

        Assert.Null(terminal.Payload.InFlightPhase);

        Assert.Null(terminal.Payload.Binding.ParentReceiptBindingDigest);

        var evidence = terminal.Payload.Lifecycle.ReconciliationEvidence!;

        Assert.Equal(GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified, evidence.Step);

        Assert.True(evidence.ParentReceiptNotRequired);

        Assert.Null(evidence.ParentReceiptDigest);

        Assert.True(evidence.LaneClosed);

        Assert.Equal(GrimoireOfflineTransitionTerminalIntent.CommitAndReopen, evidence.CovenantDispositionIntent);

        Assert.Equal(GrimoireOfflineTransitionDatabaseReconciler.WinnerDigest(terminal.Payload.Binding, operation), evidence.DatabaseTerminalWinnerDigest);

        Assert.All(harness.Journal.DispositionsAtRetirementSteps, accepted => Assert.Equal(0, accepted));

        string[] retirement = harness.Journal.Steps.Where(step => step.StartsWith("anchor:closed", StringComparison.Ordinal)
            || step.StartsWith("file:retiring", StringComparison.Ordinal) || step is "file:delete-parent-flushed"
                or "file:absence-parent-flushed" or "file:absence-proved").ToArray();

        Assert.Equal(new[] { "anchor:closed-written", "anchor:closed-readback", "file:retiring-moved", "file:retiring-verified",
            "file:retiring-parent-flushed", "file:retiring-unlinked", "file:retiring-zero-link-verified", "file:delete-parent-flushed",
            "file:absence-parent-flushed", "file:absence-proved" }, retirement);

        Assert.False(File.Exists(terminal.Raw.Location.JournalPath));

        IOsCredentialStore credentials = harness.Factory.Services.GetRequiredService<IOsCredentialStore>();

        var anchor = new GrimoireOfflineTransitionJournalAnchorStore(credentials).Read(terminal.Raw.Location);

        Assert.True(anchor.IsSuccess);

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, anchor.Value!.State);

        Assert.Equal(terminal.Raw.Envelope.SlotEpoch, anchor.Value.SlotEpoch);

        Assert.Equal(terminal.Raw.Envelope.Revision, anchor.Value.Revision);

        Assert.True(CryptographicOperations.FixedTimeEquals(harness.Journal.InitialJournalKeyFingerprint!,
            ObservingMaintenanceJournal.JournalKeyFingerprint(credentials, terminal.Raw.Location.ProfileNamespace)));

        var heldLock = harness.Factory.Services.GetRequiredService<IInstallationResetMaintenanceLockAccessor>()
            .BorrowHeldLock(ArcanumPaths.GrimoireDirectory);

        Assert.True(heldLock.IsSuccess);

        var recovered = await harness.Factory.Services.GetRequiredService<IGrimoireOfflineTransitionJournalStore>()
            .RecoverAsync(heldLock.Value, ArcanumPaths.GrimoireDirectory, cancellationToken);

        Assert.True(recovered.IsSuccess);

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal, recovered.Value.Outcome);

        Assert.Null(recovered.Value.Publication);

        Assert.Equal(targetDataset, reconciliation.PublishedDatasetGeneration);

        Assert.Equal(targetDataset, harness.Factory.Services
            .GetRequiredService<CovenantRuntimeGenerationProvider>().Current.Availability.DatasetGeneration);

    }

    private static void AssertStageOnePending(GrimoireMaintenanceAdmissionHarness harness, Task<HttpResponseMessage> reset)
    {
        Assert.Equal(1, harness.Admission.StageOne.EnteredCount);

        Assert.Equal(0, harness.Admission.StageOne.CompletedCount);

        Assert.Equal(0, harness.Admission.StageTwo.EnteredCount);

        Assert.False(reset.IsCompleted);
    }

    private static async Task AssertCommittedDirectResetAsync(GrimoireMaintenanceAdmissionHarness harness,
        DataRetentionPlan plan, DataRetentionApplyResult result, long losingGeneration, Guid sourceDataset,
        CovenantOfflineTransitionEpochsV1 sourceEpochs, CancellationToken cancellationToken)
    {
        Assert.Equal(plan.PlanId, result.PlanId);

        Assert.Empty(result.Blockers);

        Assert.Empty(result.Conflicts);

        Assert.Equal(losingGeneration + 1, harness.Admission.CurrentGeneration);

        var disposition = Assert.Single(harness.Admission.ClosedLeases);

        Assert.Equal([CovenantExclusiveLeaseDisposition.CommitAndReopen], disposition.Dispositions);

        Assert.Equal(1, disposition.Disposals);

        Assert.Equal(1, Assert.Single(harness.Admission.ClosingOwners).Disposals);

        await using AsyncServiceScope inspection = harness.Factory.Services.CreateAsyncScope();

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inspection.ServiceProvider
            .GetRequiredService<ILongRunningOperationStore>().GetAsync(result.OperationId, cancellationToken));

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

        Assert.Equal(4, operation.CheckpointVersion);

        var decoded = CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(operation.CheckpointPayload!);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(result.OperationId, decoded.Value.OperationId);

        Assert.Equal(CovenantExclusiveOperation.CovenantReset, decoded.Value.Operation);

        Assert.Equal(4, decoded.Value.Version);

        Assert.Equal(operation.Kind, decoded.Value.OperationKind);

        Assert.Equal(sourceDataset, decoded.Value.SourceDatasetGeneration);

        Assert.Equal(sourceEpochs, decoded.Value.SourceEpochs);

        Assert.NotEqual(sourceDataset, decoded.Value.TargetDatasetGeneration);

        Assert.Equal(new CovenantOfflineTransitionEpochsV1(sourceEpochs.AcceleratorEpoch + 1,
            sourceEpochs.KeyReclamationEpoch + 1, sourceEpochs.EnvelopeKeyEpoch + 1), decoded.Value.TargetEpochs);

        Assert.Null(operation.TerminalErrorCode);

        Assert.NotNull(operation.CompletedAt);

        Assert.Equal(0, result.FilesDeleted);

        await AssertCommittedRetirementAsync(harness, operation, decoded.Value.TargetDatasetGeneration, cancellationToken);

        using HttpRequestMessage request = harness.CreateObservedRequest(HttpMethod.Get, "/api/grimoire/stats");

        using HttpResponseMessage response = await harness.Client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True((await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseGrimoireStatsDto, cancellationToken))!.IsSuccess);

        Assert.Equal(losingGeneration + 1, harness.StatsProbe.ObservedLease!.Generation);

        Assert.True(harness.Admission.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out var work));

        await using (work!)
        {
            Assert.Equal(losingGeneration + 1, work!.Generation);
        }

        var fresh = await inspection.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken);

        Assert.True(fresh.IsSuccess);

        await using (fresh.Value)
        {
            Assert.Equal(ConnectionState.Open, fresh.Value.Connection.State);

            Assert.Equal(losingGeneration + 1, harness.Admission.Tickets.Last().Generation);

            await AssertCommittedCatalogAsync(fresh.Value.Connection, decoded.Value, cancellationToken);
        }

        var audit = await inspection.ServiceProvider.GetRequiredService<IInferenceAuditLogger>()
            .QueryAsync(null, null, "mistral:latest", null, 10, cancellationToken);

        var completedChat = Assert.Single(audit);

        Assert.Equal((7, 2, 9), (completedChat.PromptTokens, completedChat.CompletionTokens, completedChat.TotalTokens));

        Assert.Equal("test", completedChat.Provider);

        Assert.Equal(0, completedChat.ToolCalls);

        Assert.Equal("stop", completedChat.FinishReason);

        var indexing = inspection.ServiceProvider.GetRequiredService<SessionAttachmentIndexRepository>();

        var indexed = await indexing.GetStateAsync(harness.IndexingAttachmentA.Id, cancellationToken);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, indexed.Status);

        Assert.Equal(3, indexed.AttemptCount);

        Assert.Null(indexed.FailureReason);

        Assert.Single(await indexing.GetChunksForAttachmentAsync(harness.IndexingAttachmentA.Id, cancellationToken));

        var covenant = await inspection.ServiceProvider.GetRequiredService<ICovenantOperationGate>()
            .AcquireInstallationReadAsync(cancellationToken);

        Assert.True(covenant.IsSuccess);

        await covenant.Value.DisposeAsync();

        using Microsoft.Data.Sqlite.SqliteConnection unused = new();

        Assert.True(harness.Admission.LastClosedLease!.AcquireScopedConnectionPermit(unused).IsFailure);

        Assert.True((await harness.Admission.LastClosedLease.AcquireMaintenanceIoLaneAsync(
            (_, _, _) => ValueTask.FromResult(true), cancellationToken)).IsFailure);
    }
}
