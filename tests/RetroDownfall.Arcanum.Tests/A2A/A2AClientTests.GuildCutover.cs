using System.Net;
using A2A;
using Microsoft.AspNetCore.TestHost;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_cutover_refuses_legacy_remote_issue_and_continuation_before_network(bool continuation)
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        A2AClientService client = CreateClient(handler, settings);

        Result<A2ADispatchResult> result = continuation
            ? await client.ContinueSendingAsync(DiscoveryUrl, "existing-task", "Continue")
            : await client.DispatchSendingAsync("Compute a result", null, DiscoveryUrl);

        Assert.Equal(ErrorCodes.Guild.LegacyRunUnavailable, result.Error.Code);

        Assert.Empty(handler.Requests);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Guild_transport_refuses_unsupported_finite_axes_before_discovery(bool finiteUsd, bool finiteTokens)
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        GuildBudgetGrant grant = new(new(finiteUsd ? GuildBudgetMode.Finite : GuildBudgetMode.Unlimited, finiteUsd ? 1m : null),
            new(finiteTokens ? GuildBudgetMode.Finite : GuildBudgetMode.Unlimited, finiteTokens ? 100L : null));

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", Guid.NewGuid().ToString("N"),
            grant), new GuildSendingLifecycle(), default);

        Assert.Equal(ErrorCodes.Guild.BudgetCapabilityUnavailable, result.Error.Code);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Guild_transport_requires_durable_admission_before_discovery()
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        GuildSendingLifecycle lifecycle = new() { RefusePreflight = true };

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", Guid.NewGuid().ToString("N"),
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, result.Error.Code);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Guild_transport_commits_acceptance_before_following_and_observes_unknown_cost()
    {
        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("Guild answer"));

        using HttpMessageHandler handler = server.CreateHandler();

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        GuildSendingLifecycle lifecycle = new();

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "stable-message",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(new[] { "preflight", "send", "accepted", "completed" }, lifecycle.Events);

        Assert.Equal("Guild answer", lifecycle.Observation!.ResponseText);

        Assert.False(lifecycle.Observation.Cost.IsKnown);

        Assert.NotNull(lifecycle.Observation.TaskId);
    }

    [Fact]
    public async Task Guild_transport_admission_refusal_does_not_create_a_remote_task()
    {
        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("Must not run"));

        using HttpMessageHandler handler = server.CreateHandler();

        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        GuildSendingLifecycle lifecycle = new() { RefuseSend = true };

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "stable-message",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited))), lifecycle, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(new[] { "preflight", "send" }, lifecycle.Events);

        Assert.Null(lifecycle.Observation);
    }

    [Fact]
    public async Task Guild_transport_preserves_the_frozen_multi_interface_card_and_uses_the_supported_binding()
    {
        AgentCard card = BuildFakeCard();

        card.SupportedInterfaces = [.. card.SupportedInterfaces!, new AgentInterface { Url = $"http://{FakeAgentHost}/agent-http", ProtocolBinding = "HTTP+JSON", ProtocolVersion = "1.0" }];

        using TestServer server = await CreateFakeRemoteAgentServerAsync(new EchoingAgentHandler("multi-interface answer"), card);

        using HttpMessageHandler handler = server.CreateHandler();

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        GuildRemoteTransportDescriptor descriptor = new("multi", $"http://{FakeAgentHost}/agent", "1.0", ["JSONRPC", "HTTP+JSON"], [], null, null, null,
            [new($"http://{FakeAgentHost}/agent", "JSONRPC", "1.0"), new($"http://{FakeAgentHost}/agent-http", "HTTP+JSON", "1.0")]);

        var result = await CreateClient(handler, settings).SendGuildAsync(new(DiscoveryUrl, "Compute", "multi-message",
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), Transport: descriptor), new GuildSendingLifecycle(), default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("multi-interface answer", result.Value.ResponseText);
    }

    private sealed class GuildSendingLifecycle : IA2AGuildSendingLifecycle
    {
        internal bool RefusePreflight { get; init; }

        internal bool RefuseSend { get; init; }

        internal bool RefuseAuxiliary { get; set; }

        internal List<string> Events { get; } = [];

        internal A2AGuildTaskObservation? Observation { get; private set; }

        internal int AuxiliaryDepth { get; private set; }

        internal int MaximumAuxiliaryDepth { get; private set; }

        public Task<Result> PreflightAsync(CancellationToken ct)
        {
            Events.Add("preflight");

            return Task.FromResult(RefusePreflight
                ? Result.Failure(new Error(ErrorCodes.Guild.AuthorityMismatch, "No committed dispatch")) : Result.Success());
        }

        public async Task<Result<SendMessageResponse>> AdmitAndSendAsync(Func<CancellationToken, Task<SendMessageResponse>> send, CancellationToken ct)
        {
            Events.Add("send");

            return RefuseSend ? new Error(ErrorCodes.Guild.AuthorityRevoked, "The Signet was revoked") : await send(ct);
        }

        public Task<Result> AcceptedAsync(string? taskId, string? contextId, CancellationToken ct)
        {
            Events.Add("accepted");

            return Task.FromResult(Result.Success());
        }

        public async Task<Result<T>> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken ct)
        {
            if (RefuseAuxiliary)
                return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Signet was revoked");

            AuxiliaryDepth++;

            MaximumAuxiliaryDepth = Math.Max(MaximumAuxiliaryDepth, AuxiliaryDepth);

            try { return await action(ct); }
            finally { AuxiliaryDepth--; }
        }

        public Task<Result> CallbackAsync(string configId, string tokenHash, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> ObservedAsync(A2AGuildTaskObservation observation, CancellationToken ct)
        {
            Events.Add(observation.State);

            Observation = observation;

            return Task.FromResult(Result.Success());
        }
    }
}
