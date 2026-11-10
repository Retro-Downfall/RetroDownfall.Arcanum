using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_transport_read_after_cancel_requires_fresh_admission(bool lostAcknowledgement)
    {
        using var server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("cancel follow-up"));

        GuildSendingLifecycle observation = new();

        using CancelSettlementHandler handler = new(server.CreateHandler(),
            () => observation.RefuseAuxiliary = true, lostAcknowledgement);

        var settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        var client = CreateClient(handler, settings);

        GuildSendingLifecycle initial = new();

        var sent = await client.SendGuildAsync(new(DiscoveryUrl, "Compute", "cancel-settlement",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), initial, default);

        Assert.True(sent.IsSuccess, sent.Error.Message);

        A2AGuildTaskObservation accepted = Assert.IsType<A2AGuildTaskObservation>(initial.Observation);

        Assert.NotNull(accepted.TaskId);

        GuildRemoteTransportDescriptor transport = new("cancel-peer", $"http://{FakeAgentHost}/agent", "1.0", ["JSONRPC"], [], null, null, null);

        var result = await client.ObserveGuildTaskAsync(DiscoveryUrl, accepted.TaskId!, transport,
            observation, cancellationRequested: true, default, accepted.ContextId);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(1, handler.Cancellations);

        Assert.Equal(0, handler.ReadsAfterCancellation);

        Assert.Null(observation.Observation);

        Assert.Equal(0, observation.AuxiliaryDepth);
    }

    private sealed class CancelSettlementHandler(HttpMessageHandler inner, Action afterCancellation,
        bool lostAcknowledgement) : DelegatingHandler(inner)
    {
        internal int Cancellations;

        internal int ReadsAfterCancellation;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            if (Cancellations > 0 && body.Contains("\"GetTask\"", StringComparison.Ordinal)) ReadsAfterCancellation++;

            HttpResponseMessage response = await base.SendAsync(request, ct);

            if (body.Contains("\"CancelTask\"", StringComparison.Ordinal))
            {
                Cancellations++;

                afterCancellation();

                if (lostAcknowledgement)
                {
                    response.Dispose();

                    throw new HttpRequestException("The cancellation acknowledgement was lost.");
                }
            }

            return response;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_transport_closes_stream_and_admission_before_authoritative_settlement_query(bool revokeAfterStream)
    {
        using GateAgentHandler agent = new(1);

        using var server = await CreateFakeRemoteAgentServerAsync(agent);

        GuildSendingLifecycle lifecycle = new();

        using StreamSettlementHandler handler = new(server.CreateHandler(), agent,
            () => lifecycle.RefuseAuxiliary = revokeAfterStream);

        var settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "stream-settlement",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(!revokeAfterStream, result.IsSuccess);

        Assert.Equal(1, handler.Subscriptions);

        if (revokeAfterStream)
        {
            Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

            Assert.Equal(0, handler.SettlementQueries);

            Assert.Null(lifecycle.Observation);
        }
        else Assert.True(handler.SettlementQueries > 0);

        Assert.True(handler.QueriesAfterStreamDisposal);

        Assert.Equal(1, lifecycle.MaximumAuxiliaryDepth);

        Assert.Equal(0, lifecycle.AuxiliaryDepth);
    }

    private sealed class StreamSettlementHandler(HttpMessageHandler inner, GateAgentHandler agent,
        Action afterStreamDisposed) : DelegatingHandler(inner)
    {
        internal int Subscriptions;

        internal int SettlementQueries;

        internal bool QueriesAfterStreamDisposal = true;

        private bool _streamDisposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

            if (body.Contains("\"GetTask\"", StringComparison.Ordinal))
            {
                SettlementQueries++;

                QueriesAfterStreamDisposal &= _streamDisposed;
            }

            HttpResponseMessage response = await base.SendAsync(request, ct);

            if (body.Contains("\"SubscribeToTask\"", StringComparison.Ordinal))
            {
                Subscriptions++;

                response.Content = new StreamSettlementContent(response.Content, () =>
                {
                    _streamDisposed = true;

                    afterStreamDisposed();
                });

                agent.Release("stream settlement");
            }

            return response;
        }
    }

    private sealed class StreamSettlementContent : HttpContent
    {
        private readonly HttpContent _inner;

        private readonly Action _disposed;

        internal StreamSettlementContent(HttpContent inner, Action disposed)
        {
            _inner = inner;

            _disposed = disposed;

            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => _inner.ReadAsStreamAsync(ct);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed();

                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_transport_rejects_a_polled_task_or_context_switch_before_observing_result(bool contextMismatch)
    {
        AgentCard card = BuildFakeCard();

        card.Capabilities = new();

        using var server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("a different task must never settle this Quest"), card);

        using GuildTaskMutationHandler handler = new(server.CreateHandler(), contextMismatch);

        var settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        GuildSendingLifecycle lifecycle = new();

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "identity-message",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default);

        Assert.True(result.IsFailure);

        Assert.Null(lifecycle.Observation);

        Assert.True(handler.Posts == 2, $"Expected send then poll, observed {handler.Posts}; {result.Error.Code}: {result.Error.Message}");
    }

    [Theory]
    [InlineData("client")]
    [InlineData("target")]
    public async Task Guild_transport_rechecks_client_and_configured_target_at_physical_send(string change)
    {
        using var server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("must not execute"));

        var settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        using GuildCardMutationHandler handler = new(server.CreateHandler(), () =>
        {
            if (change == "client") settings.Features.A2AClient = false;
            else settings.Integrations!.A2A!.AllowedRemoteAgents = ["http://different.example/"];
        });

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "changed-message",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), new GuildSendingLifecycle(), default);

        Assert.True(result.IsFailure);

        Assert.Equal(0, handler.Posts);
    }

    private sealed class GuildTaskMutationHandler(HttpMessageHandler inner, bool contextMismatch) : DelegatingHandler(inner)
    {
        internal int Posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);

            if (request.Method != HttpMethod.Post) return response;

            int call = ++Posts;

            JsonNode payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;

            JsonObject? task = FindTask(payload);

            Assert.NotNull(task);

            if (call == 1)
            {
                string state = task["status"]!["state"]!.GetValue<string>();

                task["status"]!["state"] = state.StartsWith("TASK_STATE_", StringComparison.Ordinal) ? "TASK_STATE_WORKING" : "working";
            }
            else task[contextMismatch ? "contextId" : "id"] = "different-identity";

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

    private sealed class GuildCardMutationHandler(HttpMessageHandler inner, Action mutation) : DelegatingHandler(inner)
    {
        internal int Posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posts++;

            var result = await base.SendAsync(request, ct);

            if (request.Method == HttpMethod.Get) mutation();

            return result;
        }
    }
}
