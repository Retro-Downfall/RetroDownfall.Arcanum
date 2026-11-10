using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(A2ADispatchMode.Blocking, "complete")]
    [InlineData(A2ADispatchMode.Continuable, "complete")]
    [InlineData(A2ADispatchMode.Callback, "complete")]
    [InlineData(A2ADispatchMode.Callback, "push")]
    [InlineData(A2ADispatchMode.Continuable, "continuation-push")]
    [InlineData(A2ADispatchMode.Continuable, "wait")]
    [InlineData(A2ADispatchMode.Continuable, "restart-wait")]
    [InlineData(A2ADispatchMode.Continuable, "observe-wait")]
    [InlineData(A2ADispatchMode.Continuable, "modality")]
    [InlineData(A2ADispatchMode.Continuable, "revoked")]
    [InlineData(A2ADispatchMode.Continuable, "revoked-erased")]
    [InlineData(A2ADispatchMode.Continuable, "revoked-cancel-disconnect")]
    [InlineData(A2ADispatchMode.Blocking, "disconnect")]
    [InlineData(A2ADispatchMode.Continuable, "continuation-disconnect")]
    [InlineData(A2ADispatchMode.Continuable, "continuation-cancel")]
    [InlineData(A2ADispatchMode.Continuable, "slow-cancel")]
    [InlineData(A2ADispatchMode.Continuable, "maintenance-stall")]
    [InlineData(A2ADispatchMode.Blocking, "task-switch")]
    [InlineData(A2ADispatchMode.Blocking, "context-switch")]
    [InlineData(A2ADispatchMode.Blocking, "rejected")]
    [InlineData(A2ADispatchMode.Blocking, "unsupported-wait")]
    [InlineData(A2ADispatchMode.Callback, "unsupported-wait")]
    [InlineData(A2ADispatchMode.Blocking, "scheduler")]
    public async Task Remote_port_owns_one_Quest_and_retains_unknown_cost_without_a_second_lifecycle(A2ADispatchMode mode, string scenario)
    {
        await using Fixture f = await Fixture.CreateAsync(gateTimeProvider: new ManualTimerTimeProvider());

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.A2AClient = true;

        f.Settings.CurrentValue.Integrations ??= new();

        f.Settings.CurrentValue.Integrations.A2A ??= new();

        f.Settings.CurrentValue.Integrations.A2A.AllowedRemoteAgents = ["http://remote-port.example/"];

        GuildRemoteTransportDescriptor target = new("remote-port", "http://remote-port.example/agent", "1.0", ["JSONRPC"], [], null, null, null);

        GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Remote", "Remote peer", "1", [], ["text/plain"], ["text/plain"],
            new(false, false, false), new(GuildAgentSourceKind.Remote, null, target.PeerId, true, null));

        GuildStore roster = new(f.Connections, f.Gate, TimeProvider.System);

        var stored = await roster.UpsertCardAsync(new("remote-port", null, null, null,
            new(card, "", [], target, "http://remote-port.example/"), new string('A', 64)), default);

        Assert.True(stored.IsSuccess, stored.Error.Message);

        var caller = await f.OperatorAsync();

        var root = f.Request with { AgentId = stored.Value.Payload.Card.AgentId,
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Remote] },
            RemoteOptions = new((GuildRemoteDispatchMode)mode) };

        var issued = await f.Store.IssueAsync(caller,
            new(f.Generation, null, caller.InvocationId, caller.InvocationId, root.IdempotencyKey), root, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        LongRunningOperation operation = (await operations.GetAsync(quest.OperationId))!;

        RemotePortAgent agent = new(scenario);

        CompletedRemoteTaskStore peerTasks = new();

        FakeDnsResolver peerDns = new();

        peerDns.Add("callback.example", IPAddress.Parse("93.184.216.35"));

        ArcanumSettings peerSettings = new()
        {
            Features = new() { Conclave = true, A2AServer = true },
            Integrations = new() { A2A = new() { PushNotifications = true, AllowedRemoteAgents = ["https://callback.example/"] } },
        };

        if (scenario is "push" or "continuation-push")
        {
            f.Settings.CurrentValue.Integrations.A2A.PushNotifications = true;

            f.Settings.CurrentValue.Integrations.A2A.PushCallbackBaseUrl = "https://callback.example/";
        }

        using IHost host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();

            web.ConfigureServices(services => services.AddRouting().AddAdHocHttpJson());

            web.Configure(app =>
            {
                app.UseRouting();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWellKnownAgentCard(new AgentCard { Name = "Remote", Description = "Peer", Version = "1",
                        SupportedInterfaces = [new() { Url = target.Endpoint, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
                        Capabilities = new() { PushNotifications = scenario is "push" or "continuation-push" }, DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"] });

                    endpoints.MapA2A(new ArcanumA2AServer(agent, peerTasks, new ChannelEventNotifier(),
                        new A2APushNotificationRegistry(new TestOptionsMonitor<ArcanumSettings>(peerSettings), peerDns,
                            NullLogger<A2APushNotificationRegistry>.Instance), NullLogger<A2AServer>.Instance, new A2AServerOptions()), "/agent");
                });
            });
        }).StartAsync();

        using HttpMessageHandler http = host.GetTestServer().CreateHandler();

        FakeDnsResolver dns = new();

        dns.Add("remote-port.example", IPAddress.Parse("93.184.216.34"));

        using PushRemoteCapture capture = new(http, scenario is "slow-cancel" or "maintenance-stall", scenario == "maintenance-stall");

        using HttpMessageHandler transport = scenario is "disconnect" or "continuation-disconnect" or "continuation-cancel"
            ? new DropRemoteAcknowledgement(capture, scenario == "disconnect" ? 1 : 2)
            : scenario is "task-switch" or "context-switch" ? new RemoteTaskIdentityMutation(capture, scenario == "context-switch")
            : scenario == "revoked-cancel-disconnect" ? new DropRemoteCancelAcknowledgement(capture) : capture;

        A2ASendingCallbackRegistry callbacks = new(NullLogger<A2ASendingCallbackRegistry>.Instance);

        A2AClientService client = new(new RemotePortHttp(transport), f.Settings, NullLogger<A2AClientService>.Instance, dns, callbacks: callbacks);

        ManualTimerTimeProvider cancellationClock = new();

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, new(),
            scenario == "slow-cancel" ? cancellationClock : TimeProvider.System);

        GuildDispatchRequest request = new(new(root.IdempotencyKey, issued.Value.IntentDigest), quest.Id, quest.OperationId,
            quest.RunId, quest.Id, operation.Revision, quest.GoalRef.Digest);

        async Task DrainScheduledRemoteAsync()
        {
            LongRunningOperationOwnership ownership = new();

            SchedulerEngine localEngine = new();

            GuildLocalQuestScheduler? scheduler = null;

            ServiceCollection registrations = new();

            registrations.AddSingleton(f.Store);
            registrations.AddSingleton(f.Policy);
            registrations.AddSingleton(f.Authority);
            registrations.AddSingleton<ILongRunningOperationStore>(operations);
            registrations.AddSingleton<IGuildLocalExecutionEngine>(localEngine);
            registrations.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());
            registrations.AddSingleton<IGuildLocalQuestScheduler>(_ => scheduler!);
            registrations.AddSingleton<IGrimoireConnectionAdmissionGate>(f.Gate);
            registrations.AddSingleton(client);
            registrations.AddSingleton(ownership);
            registrations.AddSingleton(TimeProvider.System);
            registrations.AddScoped<GuildLocalPort>();
            registrations.AddScoped<GuildRemotePort>();

            await using ServiceProvider services = registrations.BuildServiceProvider();

            using var scheduled = new GuildLocalQuestScheduler(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
                f.Connections, ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance,
                new SchedulerReadiness(false), f.Gate);

            scheduler = scheduled;

            await scheduler.StartAsync(default);

            try
            {
                scheduler.Schedule((await f.Store.GetAsync(caller, quest.Id, default)).Value.Quest);

                while ((await f.Store.GetAsync(caller, quest.Id, CancellationToken.None)).Value.Quest.ResultReceiptId is null)
                    await Task.Delay(25, CancellationToken.None);
            }
            finally { await scheduler.StopAsync(default); }

        }

        async Task<Result<GuildDispatchObservation>> CompletePushAsync(Func<CancellationToken, Task<Result<GuildDispatchObservation>>> start)
        {
            using CancellationTokenSource lifetime = new();

            Task<Result<GuildDispatchObservation>> dispatch = start(lifetime.Token);
            try
            {
                Assert.Same(capture.InitialRead.Task, await Task.WhenAny(capture.InitialRead.Task, dispatch));

                await capture.InitialRead.Task;

                Assert.False(dispatch.IsCompleted);

                Assert.NotNull(capture.ConfigId);

                var binding = await f.Store.FindRemoteCallbackAsync(capture.ConfigId!, capture.Token, CancellationToken.None);

                Assert.True(binding.IsSuccess, binding.Error.Message);

                Assert.NotNull(binding.Value);

                agent.Release();

                Assert.Same(peerTasks.Completed.Task, await Task.WhenAny(peerTasks.Completed.Task, dispatch));

                await peerTasks.Completed.Task;

                var accepted = await f.Store.AcceptRemoteCallbackAsync(caller, capture.ConfigId!, capture.Token, binding.Value!, CancellationToken.None);

                Assert.True(accepted.IsSuccess, accepted.Error.Message);

                Assert.Equal(A2ACallbackOutcome.Delivered, callbacks.TrySignal(capture.ConfigId!, capture.Token));

                var completed = await dispatch;

                Assert.Equal(1, capture.Registrations);

                Assert.True(capture.TaskReads >= 2);

                return completed;
            }
            finally
            {
                await lifetime.CancelAsync();

                agent.Release();

                if (capture.ConfigId is { } config) callbacks.TrySignal(config, capture.Token);

                try { _ = await dispatch; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }

        GuildExecutionHandle currentExecution = execution.Value;

        RetroDownfall.Arcanum.Core.Primitives.Result<GuildDispatchObservation> result;

        if (scenario == "scheduler")
        {
            LongRunningOperationOwnership ownership = new();

            SchedulerEngine localEngine = new();

            GuildLocalQuestScheduler? scheduler = null;

            ServiceCollection registrations = new();

            registrations.AddSingleton(f.Store);
            registrations.AddSingleton(f.Policy);
            registrations.AddSingleton(f.Authority);
            registrations.AddSingleton<ILongRunningOperationStore>(operations);
            registrations.AddSingleton<IGuildLocalExecutionEngine>(localEngine);
            registrations.AddSingleton<IGuildLocalInferenceRuntime>(new SchedulerInference());
            registrations.AddSingleton<IGuildLocalQuestScheduler>(_ => scheduler!);
            registrations.AddSingleton<IGrimoireConnectionAdmissionGate>(f.Gate);
            registrations.AddSingleton(client);
            registrations.AddSingleton(ownership);
            registrations.AddSingleton(TimeProvider.System);
            registrations.AddScoped<GuildLocalPort>();
            registrations.AddScoped<GuildRemotePort>();

            await using ServiceProvider services = registrations.BuildServiceProvider();

            using var scheduled = new GuildLocalQuestScheduler(services.GetRequiredService<IServiceScopeFactory>(), f.Settings,
                f.Connections, ownership, TimeProvider.System, NullLogger<GuildLocalQuestScheduler>.Instance,
                new SchedulerReadiness(false), f.Gate);

            scheduler = scheduled;

            await scheduler.StartAsync(default);

            try
            {
                Assert.True(scheduler.IsAvailable, "Configured remote execution must not require a local model.");

                scheduler.Schedule(quest);

                while ((await f.Store.GetAsync(caller, quest.Id, CancellationToken.None)).Value.Quest.ResultReceiptId is null)
                    await Task.Delay(25, CancellationToken.None);

                Assert.Equal(0, localEngine.Calls);
            }
            finally { await scheduler.StopAsync(default); }

            var completed = (await f.Store.GetAsync(caller, quest.Id, default)).Value.Quest;

            var rebound = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, completed.SignetRevision, default);

            currentExecution = (await f.Authority.RebindAsync(rebound.Value, default)).Value;

            result = await port.ObserveAsync(currentExecution, quest.Id, quest.Id, default);
        }
        else if (scenario == "push") result = await CompletePushAsync(ct => port.DispatchAsync(currentExecution, request, ct));
        else result = await port.DispatchAsync(currentExecution, request, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        GuildQuestStore currentStore = f.Store;

        GuildLocalHostPolicy? restartedPolicy = scenario == "restart-wait" ? f.NewPolicy() : null;

        using SignetAuthority? restartedAuthority = restartedPolicy is null ? null
            : new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

        if (scenario == "observe-wait")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var firstQuestion = await currentStore.ReadLocalQuestionAsync(caller, quest.Id, default);

            Assert.True(firstQuestion.IsSuccess, firstQuestion.Error.Message);

            result = await port.DispatchAsync(currentExecution, request, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var sameQuestion = await currentStore.ReadLocalQuestionAsync(caller, quest.Id, default);

            Assert.True(sameQuestion.IsSuccess, sameQuestion.Error.Message);

            Assert.Equal(firstQuestion.Value.AwaitingInput, sameQuestion.Value.AwaitingInput);

            Assert.Equal(firstQuestion.Value.Revision, sameQuestion.Value.Revision);

            Assert.Equal(1L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_remote_questions"));
        }

        if (scenario == "maintenance-stall")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var current = (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest;

            GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Revision, GuildCancellationReason.OperatorRequested);

            var digest = GuildQuestIntent.Compute(quest.Id, stop);

            Assert.True((await currentStore.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, default)).IsSuccess);

            var mutation = await currentStore.ReadLocalCancellationAsync(caller, quest.Id, default);

            Assert.True(mutation.IsSuccess, mutation.Error.Message);

            Task<Result<GuildDispatchObservation>> stopping = port.RequestCancellationAsync(currentExecution, mutation.Value,
                quest.Id, quest.Id, CancellationToken.None);

            IGrimoireClosingOwner? closing = null;

            Task<Result>? drain = null;

            int startedRequests = 0;

            try
            {
                await capture.CancelEntered.Task.WaitAsync(CancellationToken.None);

                startedRequests = capture.Requests;

                CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                    new CovenantDigest(new byte[32]));

                var begun = f.Gate.BeginOrResumeExclusive(owner);

                Assert.True(begun.IsSuccess, begun.Error.Message);

                closing = begun.Value;

                drain = f.Gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

                using CancellationTokenRegistration registration = capture.CancelToken.Register(() => revoked.TrySetResult());

                await revoked.Task;

                Assert.True(capture.CancelToken.IsCancellationRequested,
                    "Closing must revoke coordination while the admitted physical send remains owned.");

                Assert.False(stopping.IsCompleted, "A started physical send must be joined even when it ignores cancellation.");

                Assert.False(drain.IsCompleted, "Maintenance must wait for the admitted physical send to settle.");
            }
            finally
            {
                capture.CancelRelease.TrySetResult();

                try
                {
                    _ = await stopping.WaitAsync(CancellationToken.None);
                }
                catch (OperationCanceledException) when (capture.CancelToken.IsCancellationRequested)
                {
                    // Revoked coordination may cancel the owned call; the physical task is already joined.
                }
                finally
                {
                    try
                    {
                        if (drain is not null) Assert.True((await drain).IsSuccess);
                    }
                    finally
                    {
                        if (closing is not null) await closing.DisposeAsync();
                    }
                }
            }

            Assert.Equal(startedRequests, capture.Requests);

            Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_result_receipts"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_remote_effects WHERE SettledAtUtc IS NULL"));

            return;
        }

        if (scenario == "slow-cancel")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            var current = (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest;

            GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Revision, GuildCancellationReason.OperatorRequested);

            var digest = GuildQuestIntent.Compute(quest.Id, stop);

            var cancelled = await currentStore.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, default);

            Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

            var mutation = await currentStore.ReadLocalCancellationAsync(caller, quest.Id, default);

            Assert.True(mutation.IsSuccess, mutation.Error.Message);

            var stopping = port.RequestCancellationAsync(currentExecution, mutation.Value, quest.Id, quest.Id, CancellationToken.None);

            Result<GuildDispatchObservation> stopped;

            try
            {
                await capture.CancelEntered.Task.WaitAsync(CancellationToken.None);

                var beforeStop = (await operations.GetAsync(quest.OperationId, CancellationToken.None))!;

                int timersBeforeRenewal = cancellationClock.TimersCreated;

                cancellationClock.Advance(TimeSpan.FromSeconds(11));

                // The next pulse timer is armed only after the previous renewal and read finish.
                await cancellationClock.WaitForTimerCountAsync(timersBeforeRenewal + 1, CancellationToken.None);

                LongRunningOperation renewed = (await operations.GetAsync(quest.OperationId, CancellationToken.None))!;

                Assert.True(renewed.Revision > beforeStop.Revision, "The cancelling owner must renew its lease while the exact remote stop is pending.");

                Assert.True(renewed.LeaseExpiresAt > beforeStop.LeaseExpiresAt);

                Assert.False(stopping.IsCompleted, "Cancellation intent must keep observing the peer while the owned lease is renewed.");
            }
            finally
            {
                capture.CancelRelease.TrySetResult();

                stopped = await stopping.WaitAsync(CancellationToken.None);
            }

            Assert.True(stopped.IsSuccess, stopped.Error.Message);

            Assert.Equal(QuestState.Canceled, stopped.Value.State);

            Assert.Equal(1, agent.Cancellations);

            return;
        }

        if (scenario is "revoked" or "revoked-erased" or "revoked-cancel-disconnect")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            await RevokeDiscoveryRootAsync(f, caller, (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest);

            if (scenario == "revoked-erased")
            {
                await using var transaction = connection.Connection.BeginTransaction();

                await using var inputId = connection.Connection.CreateCommand();

                inputId.Transaction = transaction;

                inputId.CommandText = "SELECT InputContentId FROM guild_remote_executions WHERE QuestId=$quest";

                inputId.Parameters.AddWithValue("$quest", quest.Id.ToString("D").ToUpperInvariant());

                Guid frozenInput = Guid.Parse((string)(await inputId.ExecuteScalarAsync())!);

                foreach (Guid erased in new[] { frozenInput, quest.GoalRef.ContentId })
                {
                    await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection.Connection, transaction, erased.ToString("N"), default);

                    await using var purge = connection.Connection.CreateCommand();

                    purge.Transaction = transaction;

                    purge.CommandText = "DELETE FROM guild_content WHERE ContentId=$id";

                    purge.Parameters.AddWithValue("$id", erased.ToString("D").ToUpperInvariant());

                    Assert.Equal(1, await purge.ExecuteNonQueryAsync());
                }

                await transaction.CommitAsync();

                var current = (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest;

                GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Revision, GuildCancellationReason.OperatorRequested);

                var digest = GuildQuestIntent.Compute(quest.Id, stop);

                var cancelled = await currentStore.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, default);

                Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

                var cleanup = await currentStore.CreateRemoteCleanupDriverAsync(caller, quest.Id, default);

                Assert.True(cleanup.IsSuccess, cleanup.Error.Message);

                Assert.True((await currentStore.ReadRemoteBodyAsync(cleanup.Value, quest.Id, default)).IsFailure);

                Assert.True((await currentStore.PrepareDispatchAsync(cleanup.Value, Guid.NewGuid(), quest.GoalRef.Digest,
                    quest.RunId, false, default)).IsFailure);
            }

            await DrainScheduledRemoteAsync();

            Assert.Equal(QuestState.Canceled, (await f.Store.GetAsync(caller, quest.Id, default)).Value.Quest.State);

            Assert.Equal(1, agent.Cancellations);

            Assert.Equal(1, agent.Calls);

            return;
        }

        if (scenario is "wait" or "restart-wait" or "observe-wait" or "modality" or "continuation-push" or "continuation-disconnect" or "continuation-cancel")
        {
            Assert.Equal(QuestState.InputRequired, result.Value.State);

            GuildCallerContext currentCaller = caller;

            if (restartedPolicy is not null)
            {
                currentCaller = (await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), default)).Value;

                currentStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority!, restartedPolicy,
                    new GuildBudgetLedger(), TimeProvider.System, readiness: f.Readiness);

                var currentQuest = (await currentStore.GetAsync(currentCaller, quest.Id, default)).Value.Quest;

                var context = await restartedPolicy.CreateRecoveryAsync(currentCaller, quest.Id, quest.OperationId, currentQuest.SignetRevision, default);

                currentExecution = (await restartedAuthority!.RebindAsync(context.Value, default)).Value;

                port = new(currentStore, restartedPolicy, restartedAuthority, operations, client, f.Gate, new(), TimeProvider.System);
            }

            var question = await currentStore.ReadLocalQuestionAsync(currentCaller, quest.Id, default);

            Assert.True(question.IsSuccess, question.Error.Message);

            Assert.Equal("Which answer?", question.Value.Question);

            GuildContinueRequest answer = new(Guid.NewGuid(), question.Value.Revision, question.Value.AwaitingInput.ContinuationId,
                "text/plain", "The first answer", scenario == "modality" ? new(GuildRemoteDispatchMode.Blocking, ["application/json"])
                    : scenario == "continuation-push" ? new(GuildRemoteDispatchMode.Callback) : null);

            var accepted = await currentStore.AcceptContinuationAsync(currentCaller, quest.Id, answer, default);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            operation = (await operations.GetAsync(quest.OperationId))!;

            var continuation = await currentStore.GetContinuationAsync(currentCaller, quest.Id, answer.ContinuationId, default);

            Assert.True(continuation.IsSuccess, continuation.Error.Message);

            var transportCaller = await (restartedPolicy ?? f.Policy).CreateRunCallerAsync(restartedAuthority ?? f.Authority,
                currentExecution, answer.ContinuationId, default);

            Assert.True(transportCaller.IsSuccess, transportCaller.Error.Message);

            var acceptedDispatch = await currentStore.ReadLocalContinuationAsync(new(currentExecution, "observe", 0, transportCaller.Value),
                answer.ContinuationId, default, false);

            Assert.True(acceptedDispatch.IsSuccess, acceptedDispatch.Error.Message);

            request = new(acceptedDispatch.Value!.Mutation, quest.Id,
                quest.OperationId, quest.RunId, answer.ContinuationId, operation.Revision, continuation.Value.ContentRef.Digest);

            result = scenario == "continuation-push"
                ? await CompletePushAsync(ct => port.ContinueAsync(currentExecution, request, answer, ct))
                : await port.ContinueAsync(currentExecution, request, answer, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            if (scenario == "continuation-cancel")
            {
                var current = (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest;

                GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Revision, GuildCancellationReason.OperatorRequested);

                var digest = GuildQuestIntent.Compute(quest.Id, stop);

                var cancelled = await currentStore.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, default);

                Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

                var cleanup = await currentStore.CreateRemoteCleanupDriverAsync(caller, quest.Id, default);

                Assert.True(cleanup.IsSuccess, cleanup.Error.Message);

                var hiddenAnswer = await currentStore.ReadLocalContinuationAsync(cleanup.Value, answer.ContinuationId, default);

                Assert.True(hiddenAnswer.IsFailure, "A stop-only cleanup context must not disclose the accepted answer body.");

                Assert.Equal(ErrorCodes.Guild.Forbidden, hiddenAnswer.Error.Code);

                await DrainScheduledRemoteAsync();

                Assert.Equal(QuestState.Canceled, (await currentStore.GetAsync(caller, quest.Id, default)).Value.Quest.State);

                Assert.Equal(2, agent.Calls);

                Assert.Equal(1L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_effects WHERE StateCode=2"));

                return;
            }

            if (scenario == "modality")
            {
                Assert.True(result.Value.ReconciliationRequired);

                Assert.Equal(1, agent.Calls);

                return;
            }
        }

        bool uncertain = scenario is "disconnect" or "continuation-disconnect" or "continuation-cancel" or "task-switch" or "context-switch";

        Assert.Equal(uncertain ? QuestState.Working : scenario is "rejected" or "unsupported-wait" ? QuestState.Failed : QuestState.Completed, result.Value.State);

        Assert.Equal(uncertain, result.Value.ReconciliationRequired);

        if (scenario == "continuation-disconnect")
        {
            var pending = await currentStore.ReadLocalPendingAsync(caller, quest.Id, default);

            Assert.True(pending.IsSuccess, pending.Error.Message);

            Assert.False(pending.Value.RequiresAttention);

            var resumed = await currentStore.PrepareLocalScheduleAsync(caller, currentExecution, pending.Value.Operation.Revision, default);

            Assert.True(resumed.IsSuccess, resumed.Error.Message);

            request = resumed.Value;
        }

        var replay = await port.DispatchAsync(currentExecution, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        if (scenario == "continuation-disconnect") Assert.Equal(QuestState.Completed, replay.Value.State);

        var retained = (await f.Store.GetAsync(caller, quest.Id, default)).Value.Quest;

        if (!uncertain || scenario == "continuation-disconnect") Assert.Equal(GuildAccountingState.Uncertain, retained.AccountingState);

        await using var count = connection.Connection.CreateCommand();

        count.CommandText = "SELECT COUNT(*) FROM LongRunningOperations";

        Assert.Equal(1L, await count.ExecuteScalarAsync());

        count.CommandText = "SELECT COUNT(*) FROM guild_dispatches";

        Assert.Equal(scenario is "wait" or "restart-wait" or "observe-wait" or "continuation-push" or "continuation-disconnect" ? 2L : 1L, await count.ExecuteScalarAsync());

        Assert.Equal(scenario is "wait" or "restart-wait" or "observe-wait" or "continuation-push" or "continuation-disconnect" ? 2 : 1, agent.Calls);

        Assert.Single(agent.TaskIds.Distinct());

        if (scenario == "unsupported-wait") Assert.Equal(1, agent.Cancellations);

        if (scenario is "task-switch" or "context-switch")
        {
            Assert.True(Assert.IsType<RemoteTaskIdentityMutation>(transport).Substitutions > 0);

            Assert.Null(retained.ResultReceiptId);
        }

        count.CommandText = "SELECT COUNT(*) FROM guild_budget_effects WHERE UnknownUsd=1 AND UnknownTokens=1 AND StateCode="
            + (uncertain && scenario != "continuation-disconnect" ? "1" : "2");

        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    private sealed class RemoteTaskIdentityMutation(HttpMessageHandler inner, bool context) : DelegatingHandler(inner)
    {
        private int _posts;

        internal int Substitutions;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);

            if (request.Method != HttpMethod.Post) return response;

            JsonNode payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;

            JsonObject task = FindTask(payload)!;

            if (++_posts == 1)
            {
                string state = task["status"]!["state"]!.GetValue<string>();

                task["status"]!["state"] = state.StartsWith("TASK_STATE_", StringComparison.Ordinal) ? "TASK_STATE_WORKING" : "working";
            }
            else
            {
                task[context ? "contextId" : "id"] = "different-task-binding";

                Substitutions++;
            }

            response.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            return response;
        }

        private static JsonObject? FindTask(JsonNode? node)
        {
            if (node is not JsonObject value) return null;

            if (value.ContainsKey("status") && value.ContainsKey("id")) return value;

            foreach (var child in value)
                if (FindTask(child.Value) is { } task) return task;

            return null;
        }
    }

    private sealed class PushRemoteCapture(HttpMessageHandler inner, bool pauseCancel = false, bool retainCanceledSend = false) : DelegatingHandler(inner)
    {
        internal readonly TaskCompletionSource InitialRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource CancelEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal readonly TaskCompletionSource CancelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal string? ConfigId;

        internal string? Token;

        internal int Registrations;

        internal int TaskReads;

        internal int Requests;

        internal CancellationToken CancelToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);

            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            if (pauseCancel && body.Contains("\"CancelTask\"", StringComparison.Ordinal))
            {
                CancelToken = ct;

                CancelEntered.TrySetResult();

                if (retainCanceledSend) await CancelRelease.Task;
                else await CancelRelease.Task.WaitAsync(ct);
            }

            var response = await base.SendAsync(request, ct);

            if (body.Contains("\"CreateTaskPushNotificationConfig\"", StringComparison.Ordinal))
            {
                using JsonDocument document = JsonDocument.Parse(body);

                var config = document.RootElement.GetProperty("params").GetProperty("config");

                ConfigId = config.GetProperty("id").GetString();

                Token = config.GetProperty("token").GetString();

                Registrations++;
            }

            if (body.Contains("\"GetTask\"", StringComparison.Ordinal))
            {
                TaskReads++;

                if (ConfigId is not null) InitialRead.TrySetResult();
            }

            return response;
        }
    }

    private sealed class DropRemoteCancelAcknowledgement(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            var response = await base.SendAsync(request, ct);

            if (body.Contains("\"CancelTask\"", StringComparison.Ordinal))
            {
                response.Dispose();

                throw new HttpRequestException("Cancellation acknowledgement was lost after the peer stopped work.");
            }

            return response;
        }
    }

    private sealed class DropRemoteAcknowledgement(HttpMessageHandler inner, int dropOn = 1) : DelegatingHandler(inner)
    {
        private int _sends;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            HttpResponseMessage response = await base.SendAsync(request, ct);

            if ((body.Contains("\"SendMessage\"", StringComparison.Ordinal) || body.Contains("message/send", StringComparison.Ordinal))
                && ++_sends == dropOn)
            {
                response.Dispose();

                throw new HttpRequestException("The acknowledgement was lost after the peer accepted work.");
            }

            return response;
        }
    }

    private sealed class RemotePortHttp(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class CompletedRemoteTaskStore : ITaskStore
    {
        private readonly InMemoryTaskStore _inner = new();

        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken ct = default)
        {
            await _inner.SaveTaskAsync(taskId, task, ct);

            if (task.Status.State == TaskState.Completed) Completed.TrySetResult();
        }

        public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default) => _inner.GetTaskAsync(taskId, ct);

        public Task DeleteTaskAsync(string taskId, CancellationToken ct = default) => _inner.DeleteTaskAsync(taskId, ct);

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken ct = default) => _inner.ListTasksAsync(request, ct);
    }

    private sealed class RemotePortAgent(string scenario) : IAgentHandler
    {
        internal int Calls;

        internal int Cancellations;

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        internal List<string> TaskIds { get; } = [];

        internal List<IReadOnlyList<string>> DelegationChains { get; } = [];

        public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref Calls);

            TaskIds.Add(context.TaskId);

            DelegationChains.Add(ConclaveDelegationChain.Read(context.Message?.Metadata));

            TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

            await updater.SubmitAsync(ct);

            await updater.StartWorkAsync(cancellationToken: ct);

            if (call == 1 && scenario == "auth-wait")
            {
                await updater.RequireAuthAsync(new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"),
                    Parts = [Part.FromText("Which credential?")] }, ct);

                return;
            }

            if (call == 1 && scenario is "wait" or "restart-wait" or "observe-wait" or "modality" or "revoked" or "revoked-erased" or "continuation-push" or "continuation-disconnect" or "unsupported-wait" or "revoked-cancel-disconnect" or "continuation-cancel" or "slow-cancel" or "maintenance-stall")
            {
                await updater.RequireInputAsync(new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N"),
                    Parts = [Part.FromText("Which answer?")] }, ct);

                return;
            }

            if (scenario == "rejected")
            {
                await updater.RejectAsync(cancellationToken: ct);

                return;
            }

            if (scenario is "push" or "continuation-push") await _release.Task.WaitAsync(ct);

            await updater.AddArtifactAsync([Part.FromText("Remote answer")], cancellationToken: ct);

            await updater.CompleteAsync(cancellationToken: ct);
        }

        public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken ct)
        {
            Interlocked.Increment(ref Cancellations);

            await new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken: ct);
        }
    }
}
