using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
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
    [Fact]
    public async Task Remote_cancellation_after_erasure_before_dispatch_does_not_invent_spend()
    {
        A2ADispatchMode mode = A2ADispatchMode.Blocking;

        await using Fixture f = await Fixture.CreateAsync();

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

        await using (var transaction = connection.Connection.BeginTransaction())
        {
            await using var input = connection.Connection.CreateCommand();

            input.Transaction = transaction;

            input.CommandText = "SELECT InputContentId FROM guild_remote_executions WHERE QuestId=$quest";

            input.Parameters.AddWithValue("$quest", quest.Id.ToString("D").ToUpperInvariant());

            Guid frozenInput = Guid.Parse((string)(await input.ExecuteScalarAsync())!);

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
        }

        var current = (await f.Store.GetAsync(caller, quest.Id, default)).Value.Quest;

        GuildCancelQuestRequest stop = new(Guid.NewGuid(), current.Revision, GuildCancellationReason.OperatorRequested);

        var digest = GuildQuestIntent.Compute(quest.Id, stop);

        var cancelled = await f.Store.RequestCancellationAsync(caller, new(stop.IdempotencyKey, digest.Value), quest.Id, stop, default);

        Assert.True(cancelled.IsSuccess, cancelled.Error.Message);

        var mutation = await f.Store.ReadLocalCancellationAsync(caller, quest.Id, default);

        Assert.True(mutation.IsSuccess, mutation.Error.Message);

        using NeverDispatchHttpHandler http = new();

        FakeDnsResolver dns = new();

        dns.Add("remote-port.example", IPAddress.Parse("93.184.216.34"));

        A2AClientService client = new(new RemotePortHttp(http), f.Settings, NullLogger<A2AClientService>.Instance, dns);

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(quest.OperationId, out Guid claim));

        GuildRemotePort port = new(f.Store, f.Policy, f.Authority, operations, client, f.Gate, ownership, TimeProvider.System);

        var stopped = await port.RequestCancellationOwnedAsync(caller, mutation.Value, quest.Id, quest.Id, claim, default);

        Assert.True(stopped.IsSuccess, stopped.Error.Message);

        Assert.Equal(QuestState.Canceled, stopped.Value.State);

        Assert.Equal(0, http.Calls);

        Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_dispatches"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_effects"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_result_receipts WHERE NoDispatchProven=1 AND WorkStoppedProven=1"));
    }

    private sealed class NeverDispatchHttpHandler : HttpMessageHandler
    {
        internal int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);

            return Task.FromException<HttpResponseMessage>(new InvalidOperationException("No remote request is authorized for never-dispatched cancellation."));
        }
    }
}
