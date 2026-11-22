extern alias GuildProcessFixture;

using System.Collections.Concurrent;
using OperatingProcess = System.Diagnostics.Process;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using GuildProcessFixtureMarker = GuildProcessFixture::RetroDownfall.Arcanum.GuildProcessFixture.GuildProcessFixtureMarker;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    private const string MixedOriginGoal = "Coordinate the independent-host mixed cohort.";

    [Fact]
    public async Task Financial_off_mixed_cohort_uses_two_running_Arcanum_hosts_and_replays_after_restart()
    {
        TimeSpan phaseTimeout = TimeSpan.FromMinutes(4);

        using CancellationTokenSource deadline = new(phaseTimeout);

        CancellationToken ct = deadline.Token;

        string phase = "starting the hosts";

        string? lastParent = null;

        void BeginPhase(string name)
        {
            ct.ThrowIfCancellationRequested();

            phase = name;

            deadline.CancelAfter(phaseTimeout);
        }

        await using MixedPeer peer = await MixedPeer.StartAsync(ct, prepareProcess: child =>
        {
            child.ArgumentList[0] = typeof(GuildProcessFixtureMarker).Assembly.Location;

            child.Environment[GuildProcessFixtureMarker.AcceptPeerVariable] = "1";
        });

        int peerPort = peer.Port;

        string remote = $"http://{GuildProcessFixtureMarker.PeerHost}:{peerPort}/";

        TaskCompletionSource finalProviderRound = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource streamReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<long> closeRevision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseLocalResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using MixedParentTransport parentTransport = new(remote)
        {
            FullParent = true,
            BeforeFirstRound = streamReady.Task,
            CloseRevision = closeRevision,
            FinalProviderRound = finalProviderRound,
        };

        using FinancialOffMemberTransport localTransport = new();

        ConcurrentQueue<string> providerGets = new();

        using IHost provider = await StartMixedOriginProviderAsync(parentTransport, localTransport, providerGets, releaseLocalResult.Task, ct);

        string modelUrl = provider.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        string originHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"mixed-origin-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path.Combine(originHome, ".config", "arcanum"));

        OperatingProcess? originProcess = null;

        try
        {
            Task<(OperatingProcess Process, int Port, string Key, int Attempts)> StartOriginAsync(int? port = null, string? existingKey = null) =>
                MixedPeer.StartChildAsync(originHome, modelUrl, ct, port, existingKey,
                    config => ConfigureMixedOrigin(config, remote), child =>
                    {
                        child.ArgumentList[0] = typeof(GuildProcessFixtureMarker).Assembly.Location;

                        child.Environment[GuildProcessFixtureMarker.ConnectPeerVariable] = peerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

                        child.Environment["ARCANUM_GUILD_FIXTURE_OUTBOUND_PEER"] = Fixture.PeerCredential;
                    });

            (originProcess, int originPort, string masterKey, _) = await StartOriginAsync();

            using HttpClient origin = NewMixedHostClient(originPort, masterKey);

            using HttpClient peerAdministrator = peer.CreateAdministrator();

            Assert.Equal(originProcess.Id, await ReadMixedHostProcessAsync(origin, ct));

            Assert.Equal(peer.ProcessId, await ReadMixedHostProcessAsync(peerAdministrator, ct));

            Assert.NotEqual(originProcess.Id, peer.ProcessId);

            Assert.NotEqual(global::System.Environment.ProcessId, originProcess.Id);

            Assert.NotEqual(global::System.Environment.ProcessId, peer.ProcessId);

            await MixedPeer.VerifyEffectiveConfigurationAsync(originPort, masterKey, ct);

            BeginPhase("admitting the two members");

            using HttpClient anonymous = NewMixedHostClient(originPort);

            const string discovery = "/api/guild/agents/remote/resolve";

            using StringContent discoverRequest = MixedJson(new JsonObject { ["agent"] = remote }.ToJsonString());

            using var denied = await anonymous.PostAsync(discovery, MixedJson("{}"), ct);

            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var resolved = await origin.PostAsync(discovery, discoverRequest, ct);

            GuildAgentCard card = await ReadMixedDataAsync(resolved, GuildJsonContext.Default.GuildAgentCard, ct);

            Assert.Equal(GuildAgentSourceKind.Remote, card.Source.Kind);

            GuildGrant grant = new([], [.. CohortTools(), new(GuildToolSourceKind.InternalMcp, "arcanum", "get_agent_task")], [],
                new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
                [GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort], null);

            GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Mixed financial-off cohort", MixedOriginGoal, grant);

            string issuance = JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest);

            using var created = await origin.PostAsync("/api/guild/quests", MixedJson(issuance), ct);

            GuildQuestDto root = await ReadMixedDataAsync(created, GuildJsonContext.Default.GuildQuestDto, ct);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            string rootPath = $"/api/guild/quests/{root.Id:D}";

            using HttpRequestMessage watch = new(HttpMethod.Get, rootPath + "/events");

            using HttpResponseMessage live = await origin.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, ct);

            live.EnsureSuccessStatusCode();

            await using Stream eventStream = await live.Content.ReadAsStreamAsync(ct);

            using StreamReader events = new(eventStream);

            streamReady.TrySetResult();

            await WaitMixedAsync(async token =>
            {
                GuildQuestDto state = await ReadMixedDataAsync(origin, rootPath, GuildJsonContext.Default.GuildQuestDto, token);

                Assert.False(state.ReconciliationRequired || state.State is QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected,
                    $"Parent stopped before admitting members: {state.State} {state.ReasonCode}.");

                return parentTransport.AdmissionsComplete.Task.IsCompletedSuccessfully;
            }, ready => ready, ct);

            string cohortPath = $"/api/guild/cohorts/{parentTransport.CohortId:D}";

            CohortMemberPageDto members = await ReadMixedDataAsync(origin, cohortPath + "/members",
                GuildJsonContext.Default.CohortMemberPageDto, ct);

            Assert.Equal(2, members.Items.Length);

            BeginPhase("receiving both members' results and notices");

            releaseLocalResult.TrySetResult();

            Dictionary<string, Guid> answers = new(StringComparer.Ordinal);

            foreach (CohortMemberDto member in members.Items)
            {
                string memberPath = $"/api/guild/quests/{member.PrimaryVotingQuestId:D}";

                GuildQuestDto completedMember = await WaitMixedAsync(
                    token => ReadMixedDataAsync(origin, memberPath, GuildJsonContext.Default.GuildQuestDto, token),
                    value => value.ReconciliationRequired || value.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected or QuestState.Canceled,
                    ct);

                Assert.True(completedMember.State == QuestState.Completed,
                    $"Member stopped with {completedMember.State} ({completedMember.ReasonCode}). Provider calls origin={parentTransport.Bodies.Count}, local={localTransport.Posts}, peer={peer.ProviderPosts}.");

                Assert.Equal(GuildBudgetMode.Unlimited, completedMember.ResolvedGrant.Budget.Usd.Mode);

                Assert.Equal(GuildBudgetMode.Unlimited, completedMember.ResolvedGrant.Budget.Tokens.Mode);

                answers.Add(await ReadMixedContentAsync(origin, Assert.IsType<GuildDataRef>(completedMember.ResultRef), ct), completedMember.Id);
            }

            parentTransport.MemberId = answers["actual cohort member answer"];

            Guid remoteQuest = answers["remote actual answer"];

            await ReadMixedChildNoticesAsync(events, answers.Values, ct);

            CohortDto open = await ReadMixedDataAsync(origin, cohortPath, GuildJsonContext.Default.CohortDto, ct);

            Assert.Equal(GuildCohortState.Open, open.State);

            BeginPhase("closing the cohort and delivering both answers to the parent");

            closeRevision.TrySetResult(open.Revision);

            parentTransport.Release.TrySetResult();

            await WaitMixedAsync(async token =>
            {
                GuildQuestDto state = await ReadMixedDataAsync(origin, rootPath, GuildJsonContext.Default.GuildQuestDto, token);

                if (finalProviderRound.Task.IsCompletedSuccessfully) return true;

                Assert.False(state.ReconciliationRequired || state.State is QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected,
                    $"Parent stopped before receiving both results: {state.State} {state.ReasonCode}.");

                return false;
            }, ready => ready, ct);

            Assert.True(parentTransport.SawBothAnswers,
                "Both members committed their exact result, but the real parent provider continuation did not receive both results.");

            CohortDto closing = await ReadMixedDataAsync(origin, cohortPath, GuildJsonContext.Default.CohortDto, ct);

            Assert.True(closing.State is GuildCohortState.Closed or GuildCohortState.Draining or GuildCohortState.Completed,
                $"The model's close request left the Cohort in {closing.State} at revision {closing.Revision}.");

            CohortDto terminal = await WaitMixedAsync(
                token => ReadMixedDataAsync(origin, cohortPath, GuildJsonContext.Default.CohortDto, token),
                value => value.State is GuildCohortState.Completed or GuildCohortState.Failed or GuildCohortState.Canceled, ct);

            Assert.Equal(GuildCohortState.Completed, terminal.State);

            CohortConvergenceDto cohort = (await WaitMixedAsync(token => ReadMixedConvergenceAsync(origin, cohortPath, token),
                value => value?.State is GuildCohortState.Completed or GuildCohortState.Failed or GuildCohortState.Canceled, ct))!;

            Assert.True(cohort.State == GuildCohortState.Completed, cohort.ReasonCode);

            Assert.Equal(GuildConvergenceDecision.Success, cohort.Decision);

            Assert.Equal(2, cohort.FrozenMemberCount);

            Assert.Equal(0, cohort.PendingDescendantCount);

            Assert.True(cohort.Spend.UnknownUsd);

            Assert.True(cohort.Spend.UnknownTokens);

            CohortContributionPageDto contributions = await ReadMixedDataAsync(origin, cohortPath + "/contributions",
                GuildJsonContext.Default.CohortContributionPageDto, ct);

            Assert.Equal(answers.Values.Order(), contributions.Items.Select(item => item.PrimaryVotingQuestId).Order());

            BeginPhase("committing the parent result");

            GuildQuestDto completed = await WaitMixedAsync(
                async token =>
                {
                    GuildQuestDto value = await ReadMixedDataAsync(origin, rootPath, GuildJsonContext.Default.GuildQuestDto, token);

                    lastParent = $"{value.State}, revision {value.Revision}, wait {value.WaitReason}, reason {value.ReasonCode}";

                    return value;
                },
                value => value.ReconciliationRequired || value.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected
                    or QuestState.Canceled or QuestState.InputRequired, ct);

            Assert.True(completed.State == QuestState.Completed, completed.ReasonCode);

            Assert.Equal("both answers received", await ReadMixedContentAsync(origin, Assert.IsType<GuildDataRef>(completed.ResultRef), ct));

            int parentPosts = parentTransport.Bodies.Count;

            int localPosts = localTransport.Posts;

            int peerPosts = peer.ProviderPosts;

            Assert.True(parentPosts > 0 && localPosts > 0 && peerPosts > 0);

            BeginPhase("restarting both hosts and replaying the original issuance");

            OperatingProcess stoppedOrigin = originProcess;

            originProcess = null;

            await MixedPeer.StopProcessAsync(stoppedOrigin);

            await peer.StopAsync();

            MixedOriginSnapshot durable = await ReadMixedOriginSnapshotAsync(originHome, remoteQuest, ct);

            Assert.Equal(0, durable.Reservations);

            Assert.Equal(3, durable.ResultCount);

            Assert.Equal(2, durable.ChildApplications);

            Assert.Equal(3, durable.SettlementCount);

            Assert.Equal(2, durable.DeliveredChildSettlements);

            PeerPersistedSnapshot inbound = await peer.ReadStoppedSnapshotAsync(durable.TaskId, ct);

            Assert.Equal(durable.ContextId, inbound.ContextId);

            Assert.Equal("alpha", inbound.PeerId);

            Assert.Equal(0, inbound.Reservations);

            Assert.Equal(1, inbound.Results);

            Assert.True(inbound.UnknownEffects > 0);

            await peer.RestartAsync(ct, peerPort);

            Assert.Equal(peerPort, peer.Port);

            (originProcess, int restartedPort, _, _) = await StartOriginAsync(originPort, masterKey);

            using HttpClient restarted = NewMixedHostClient(restartedPort, masterKey);

            using var replayed = await restarted.PostAsync("/api/guild/quests", MixedJson(issuance), ct);

            GuildQuestDto replay = await ReadMixedDataAsync(replayed, GuildJsonContext.Default.GuildQuestDto, ct);

            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

            Assert.Equal(root.Id, replay.Id);

            Assert.True(replay.Receipt?.Replayed);

            GuildQuestDto retained = await ReadMixedDataAsync(restarted, rootPath, GuildJsonContext.Default.GuildQuestDto, ct);

            Assert.Equal(completed.ResultReceiptId, retained.ResultReceiptId);

            Assert.Equal("both answers received", await ReadMixedContentAsync(restarted, Assert.IsType<GuildDataRef>(retained.ResultRef), ct));

            CohortConvergenceDto recovered = (await WaitMixedAsync(token => ReadMixedConvergenceAsync(restarted, cohortPath, token),
                value => value is not null, ct))!;

            Assert.Equal(cohort.State, recovered.State);

            Assert.Equal(cohort.Decision, recovered.Decision);

            Assert.Equal(cohort.Spend.UnknownUsd, recovered.Spend.UnknownUsd);

            Assert.Equal(cohort.Spend.UnknownTokens, recovered.Spend.UnknownTokens);

            using HttpClient authenticated = NewMixedHostClient(peerPort, Fixture.PeerCredential);

            A2AClient sdk = new(new Uri($"http://localhost:{peerPort}/api/conclave/a2a"), authenticated);

            AgentTask task = await sdk.GetTaskAsync(new GetTaskRequest { Id = durable.TaskId }, ct);

            Assert.Equal(TaskState.Completed, task.Status.State);

            Assert.Equal(durable.ContextId, task.ContextId);

            string artifact = JsonSerializer.Serialize(Assert.Single(task.Artifacts!),
                (JsonTypeInfo<Artifact>)A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Artifact)));

            Assert.Contains("remote actual answer", artifact, StringComparison.Ordinal);

            Assert.Equal(parentPosts, parentTransport.Bodies.Count);

            Assert.Equal(localPosts, localTransport.Posts);

            Assert.Equal(peerPosts, peer.ProviderPosts);

            Assert.All(providerGets, path => Assert.Equal("/v1/models", path));

            Assert.All(peer.GetPaths, path => Assert.Equal("/v1/models", path));

            stoppedOrigin = originProcess;

            originProcess = null;

            await MixedPeer.StopProcessAsync(stoppedOrigin);

            await peer.StopAsync();

            Assert.Equal(durable, await ReadMixedOriginSnapshotAsync(originHome, remoteQuest, ct));

            Assert.Equal(inbound, await peer.ReadStoppedSnapshotAsync(durable.TaskId, ct));

            await peer.AssertCleanShutdownAsync();
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Two-host Guild scenario timed out while {phase}. Last observed parent: {lastParent ?? "unobserved"}.", error);
        }
        finally
        {
            streamReady.TrySetResult();

            closeRevision.TrySetCanceled();

            releaseLocalResult.TrySetResult();

            parentTransport.Release.TrySetResult();

            try
            {
                if (originProcess is not null) await MixedPeer.StopProcessAsync(originProcess);
            }
            finally
            {
                await MixedPeer.DeleteHomeAfterTeardownAsync(OperatingSystem.IsWindows(), () => Directory.Delete(originHome, true),
                    () => Task.Delay(TimeSpan.FromMilliseconds(250)));

                await provider.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static string ConfigureMixedOrigin(string config, string remote)
    {
        JsonNode document = JsonNode.Parse(config)!;

        JsonNode arcanum = document["Arcanum"]!;

        arcanum["Features"]!["A2AClient"] = true;

        arcanum["Integrations"]!["A2A"]!["AllowedRemoteAgents"] = new JsonArray(JsonValue.Create(remote));

        arcanum["Integrations"]!["A2A"]!["OutboundCredentialEnvironmentVariable"] = "ARCANUM_GUILD_FIXTURE_OUTBOUND_PEER";

        return document.ToJsonString();
    }

    private static async Task<IHost> StartMixedOriginProviderAsync(MixedParentTransport parentTransport,
        FinancialOffMemberTransport localTransport, ConcurrentQueue<string> gets, Task releaseLocalResult, CancellationToken ct)
    {
        return await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseKestrel().UseUrls("http://127.0.0.1:0");

            web.Configure(app => app.Run(async context =>
            {
                string path = context.Request.Path.ToString();

                if (HttpMethods.IsGet(context.Request.Method))
                {
                    gets.Enqueue(path);

                    if (path == "/v1/models")
                    {
                        context.Response.ContentType = "application/json";

                        await context.Response.WriteAsync("""{"object":"list","data":[{"id":"accepted-model","object":"model"}]}""");
                    }
                    else context.Response.StatusCode = 500;

                    return;
                }

                if (!HttpMethods.IsPost(context.Request.Method) || path != "/v1/chat/completions")
                {
                    context.Response.StatusCode = 500;

                    return;
                }

                using StreamReader body = new(context.Request.Body);

                string json = await body.ReadToEndAsync(context.RequestAborted);

                bool parent = json.Contains(MixedOriginGoal, StringComparison.Ordinal);

                using HttpMessageInvoker scripted = new(parent ? parentTransport : localTransport, false);

                using HttpRequestMessage request = new(HttpMethod.Post, "http://mixed-provider.test" + path) { Content = MixedJson(json) };

                using HttpResponseMessage result = await scripted.SendAsync(request, context.RequestAborted);

                if (!parent && localTransport.Posts > 1) await releaseLocalResult.WaitAsync(context.RequestAborted);

                context.Response.StatusCode = (int)result.StatusCode;

                context.Response.ContentType = "application/json";

                await result.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }));
        }).StartAsync(ct);
    }

    private static HttpClient NewMixedHostClient(int port, string? key = null)
    {
        HttpClient client = new() { BaseAddress = new Uri($"http://localhost:{port}") };

        if (key is not null) client.DefaultRequestHeaders.Add("X-Arcanum-Key", key);

        return client;
    }

    private static StringContent MixedJson(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<int> ReadMixedHostProcessAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/api/meta", ct);

        response.EnsureSuccessStatusCode();

        using JsonDocument metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return metadata.RootElement.GetProperty("data").GetProperty("processId").GetInt32();
    }

    private static async Task<T> ReadMixedDataAsync<T>(HttpClient client, string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);

        return await ReadMixedDataAsync(response, type, ct);
    }

    private static async Task<CohortConvergenceDto?> ReadMixedConvergenceAsync(HttpClient client, string cohortPath, CancellationToken ct)
    {
        using var response = await client.GetAsync(cohortPath + "/convergence", ct);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (payload.RootElement.GetProperty("error").GetProperty("code").GetString() == ErrorCodes.Guild.MaintenanceDeferred)
                return null;
        }

        return await ReadMixedDataAsync(response, GuildJsonContext.Default.CohortConvergenceDto, ct);
    }

    private static async Task<T> ReadMixedDataAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri?.AbsolutePath}: {payload.RootElement.GetRawText()}");

        return payload.RootElement.GetProperty("data").Deserialize(type)!;
    }

    private static async Task<string> ReadMixedContentAsync(HttpClient client, GuildDataRef reference, CancellationToken ct)
    {
        var content = await ReadMixedDataAsync(client, $"/api/guild/content/{reference.ContentId:D}?revision={reference.Revision}",
            GuildJsonContext.Default.GuildContentDto, ct);

        Assert.Equal(reference.Digest, content.Reference.Digest);

        return content.Body;
    }

    private static async Task ReadMixedChildNoticesAsync(StreamReader events, IEnumerable<Guid> children, CancellationToken ct)
    {
        Dictionary<Guid, HashSet<Guid>> notices = children.ToDictionary(child => child, _ => new HashSet<Guid>());

        while (notices.Values.Any(seen => seen.Count < 3))
        {
            string? line = await events.ReadLineAsync(ct);

            Assert.NotNull(line);

            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            using JsonDocument frame = JsonDocument.Parse(line[6..]);

            if (!frame.RootElement.TryGetProperty("eventKind", out _)) continue;

            GuildChangeEventDto change = frame.RootElement.Deserialize(GuildJsonContext.Default.GuildChangeEventDto)!;

            // An accepted member has an issue, result, and settlement notice from this source.
            if (change.EventKind == GuildEventKind.MessageCommitted && change.QuestId is { } child
                && notices.TryGetValue(child, out HashSet<Guid>? seen)) seen.Add(change.ObjectId);
        }
    }

    private static async Task<T> WaitMixedAsync<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> ready, CancellationToken ct)
    {
        while (true)
        {
            T value = await read(ct);

            if (ready(value)) return value;

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    private sealed record MixedOriginSnapshot(string TaskId, string ContextId, long Reservations, long ResultCount,
        long ChildApplications, long SettlementCount, long DeliveredChildSettlements, string DurableResults, string DurableSettlements);

    private static async Task<MixedOriginSnapshot> ReadMixedOriginSnapshotAsync(string home, Guid remoteQuest, CancellationToken ct)
    {
        await using var connection = await OpenStoppedMixedProfileAsync(home, ct);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT d.RemoteTaskId,d.RemoteContextId,
                (SELECT count(*) FROM BudgetReservations),(SELECT count(*) FROM guild_result_receipts),
                (SELECT count(*) FROM guild_result_applications),
                (SELECT count(*) FROM guild_portal_outbox WHERE SourceKindCode=2),
                (SELECT count(*) FROM guild_portal_outbox o JOIN guild_inbox i ON i.EnvelopeId=o.EnvelopeId
                    WHERE o.SourceKindCode=2 AND o.DeliveryStateCode=2 AND i.DeliveryStateCode=2),
                (SELECT group_concat(value,'|') FROM (SELECT QuestId || ':' || ResultId AS value FROM guild_result_receipts ORDER BY QuestId)),
                (SELECT group_concat(value,'|') FROM (SELECT QuestId || ':' || ReceiptId AS value FROM guild_portal_outbox WHERE SourceKindCode=2 ORDER BY QuestId))
            FROM guild_dispatches d WHERE d.QuestId=$quest COLLATE NOCASE AND d.RemoteTaskId IS NOT NULL
            """;

        command.Parameters.AddWithValue("$quest", remoteQuest.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct);

        Assert.True(await reader.ReadAsync(ct));

        MixedOriginSnapshot snapshot = new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8));

        Assert.False(await reader.ReadAsync(ct));

        return snapshot;
    }
}
