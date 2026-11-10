using A2A;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// The A2A SDK server with Arcanum's push-notification surface layered on (issue #67).
/// </summary>
/// <remarks>
/// The SDK's own <see cref="A2AServer"/> answers every push-notification method with
/// <c>PushNotificationNotSupported</c>, which is the correct answer while the surface is off and exactly
/// what the Agent Card advertises. When an operator turns it on, these overrides accept a peer's
/// callback — after putting its URL through the same egress gate as any other outbound request — so the
/// card's <c>pushNotifications: true</c> is a promise the server actually keeps.
/// </remarks>
public sealed class ArcanumA2AServer(
    IAgentHandler handler,
    ITaskStore taskStore,
    ChannelEventNotifier notifier,
    A2APushNotificationRegistry pushNotifications,
    ILogger<A2AServer> logger,
    A2AServerOptions options,
    IServiceScopeFactory? scopeFactory = null,
    IOptionsMonitor<ArcanumSettings>? settings = null) : A2AServer(handler, taskStore, notifier, logger, options)
{

    private readonly IAgentHandler _handler = handler;

    private readonly ITaskStore _taskStore = taskStore;

    private IServiceScopeFactory? GuildScopes => scopeFactory ?? (_taskStore as ArcanumA2ATaskStore)?.GuildScopeFactory;

    private bool GuildEnabled => GuildA2AAdapter.Enabled(GuildScopes, settings);

    public override async Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!GuildEnabled)
        {
            if (request.Message.TaskId is { Length: > 0 } id
                && await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, id, cancellationToken).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();

            SendMessageResponse legacy = await base.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);

            if (GuildEnabled) throw GuildA2AAdapter.Missing();

            return legacy;
        }

        GuildInboundPeer peer = GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing();

        var accepted = await BeginGuildAsync(request, peer, cancellationToken).ConfigureAwait(false);

        AgentTask? latest = null;

        await foreach (StreamResponse frame in GuildA2AAdapter.WatchAsync(GuildScopes, peer, accepted.Binding.TaskId,
            request.Configuration?.ReturnImmediately == true, cancellationToken).ConfigureAwait(false)) latest = frame.Task;

        if (!GuildA2AAdapter.Authenticates(GuildScopes, peer)) throw GuildA2AAdapter.Missing();

        return new() { Task = latest! };
    }

    public override IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GuildEnabled
            ? SendGuildStreamAsync(request, GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing(), cancellationToken)
            : GuardLegacyStreamAsync(base.SendStreamingMessageAsync(request, cancellationToken), request.Message.TaskId, cancellationToken);
    }

    private async IAsyncEnumerable<StreamResponse> SendGuildStreamAsync(SendMessageRequest request, GuildInboundPeer peer,
        [EnumeratorCancellation] CancellationToken ct)
    {
        GuildInboundTask accepted = await BeginGuildAsync(request, peer, ct).ConfigureAwait(false);

        await foreach (StreamResponse frame in GuildA2AAdapter.WatchAsync(GuildScopes, peer, accepted.Binding.TaskId, false, ct).ConfigureAwait(false))
        {
            if (!GuildA2AAdapter.Authenticates(GuildScopes, peer)) throw GuildA2AAdapter.Missing();

            yield return frame;
        }
    }

    private async Task<GuildInboundTask> BeginGuildAsync(SendMessageRequest request, GuildInboundPeer peer, CancellationToken ct)
    {
        if (_handler is not ArcanumA2AAgentHandler guildHandler) throw GuildA2AAdapter.Missing();

        RequestContext context = new()
        {
            Message = request.Message,
            Configuration = request.Configuration,
            TaskId = request.Message.TaskId ?? Guid.NewGuid().ToString("N"),
            ContextId = request.Message.ContextId ?? Guid.NewGuid().ToString("N"),
            StreamingResponse = false,
        };

        var result = await guildHandler.BeginGuildAsync(context, peer, ct).ConfigureAwait(false);

        if (result.IsFailure) throw new A2AException(result.Error.Code, A2AErrorCode.InvalidParams);

        return result.Value;
    }

    public override async Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!GuildEnabled)
        {
            if (await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, request.Id, cancellationToken).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();

            return await base.GetTaskAsync(request, cancellationToken).ConfigureAwait(false);
        }

        GuildInboundPeer peer = GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing();

        return GuildA2AAdapter.Project(await GuildA2AAdapter.ReadAsync(GuildScopes, peer, request.Id, cancellationToken).ConfigureAwait(false));
    }

    public override Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) =>
        _taskStore.ListTasksAsync(request, cancellationToken);

    public override async Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!GuildEnabled)
        {
            if (await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, request.Id, cancellationToken).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();

            return await base.CancelTaskAsync(request, cancellationToken).ConfigureAwait(false);
        }

        GuildInboundPeer peer = GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing();

        if (GuildScopes is null) throw GuildA2AAdapter.Missing();

        await using AsyncServiceScope scope = GuildScopes.CreateAsyncScope();

        GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

        if (host is null) throw GuildA2AAdapter.Missing();

        var result = await host.CancelAsync(peer, request.Id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure || !GuildA2AAdapter.Authenticates(GuildScopes, peer)) throw GuildA2AAdapter.Missing();

        return GuildA2AAdapter.Project(result.Value);
    }

    public override IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) =>
        GuildEnabled
            ? GuildA2AAdapter.WatchAsync(GuildScopes, GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing(), request.Id, false, cancellationToken)
            : GuardLegacyStreamAsync(base.SubscribeToTaskAsync(request, cancellationToken), request.Id, cancellationToken);

    private async IAsyncEnumerable<StreamResponse> GuardLegacyStreamAsync(IAsyncEnumerable<StreamResponse> source,
        string? taskId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (GuildEnabled || taskId is not null && await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, taskId, ct).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();

        await foreach (StreamResponse frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            string? disclosedId = frame.Task?.Id ?? frame.StatusUpdate?.TaskId ?? frame.ArtifactUpdate?.TaskId ?? taskId;

            if (GuildEnabled || disclosedId is not null && await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, disclosedId, ct).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();

            yield return frame;
        }
    }

    private async Task GuardPushTaskAsync(string taskId, CancellationToken ct)
    {
        if (GuildEnabled)
        {
            GuildInboundPeer peer = GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing();

            _ = await GuildA2AAdapter.ReadAsync(GuildScopes, peer, taskId, ct, materialize: false).ConfigureAwait(false);
        }
        else if (await GuildA2AAdapter.IsGuildTaskAsync(GuildScopes, taskId, ct).ConfigureAwait(false)) throw GuildA2AAdapter.Missing();
    }

    public override async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        CreateTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        await GuardPushTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

        if (!pushNotifications.Enabled)
        {

            return await base.CreateTaskPushNotificationConfigAsync(request, cancellationToken).ConfigureAwait(false);

        }

        Result<PushNotificationConfig> registered = await pushNotifications
            .RegisterAsync(request.TaskId, request.Config, cancellationToken)
            .ConfigureAwait(false);

        if (registered.IsFailure)
        {

            // Named rather than swallowed: a peer that registered a callback which will never fire has to
            // learn that now, not by waiting forever for a notification.
            throw new A2AException(registered.Error.Message, A2AErrorCode.InvalidParams);

        }

        await GuardPushTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

        if (GuildEnabled)
        {
            GuildInboundPeer peer = GuildA2AAdapter.Capture(GuildScopes) ?? throw GuildA2AAdapter.Missing();

            if (_handler is not ArcanumA2AAgentHandler guildHandler || !guildHandler.StartGuildCallback(peer, request.TaskId))
                throw GuildA2AAdapter.Missing();
        }

        return new TaskPushNotificationConfig
        {
            Id = registered.Value.Id ?? request.ConfigId,
            TaskId = request.TaskId,
            PushNotificationConfig = registered.Value,
        };

    }

    public override async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        await GuardPushTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

        if (!pushNotifications.Enabled)
        {

            return await base.GetTaskPushNotificationConfigAsync(request, cancellationToken).ConfigureAwait(false);

        }

        return pushNotifications.Resolve(request.TaskId) is { } config
            ? new TaskPushNotificationConfig
            {
                Id = config.Id ?? request.Id,
                TaskId = request.TaskId,
                PushNotificationConfig = config,
            }
            : throw new A2AException(
                $"No push-notification configuration is registered for task '{request.TaskId}'.",
                A2AErrorCode.TaskNotFound);

    }

    public override async Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(
        ListTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        await GuardPushTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

        if (!pushNotifications.Enabled)
        {

            return await base.ListTaskPushNotificationConfigAsync(request, cancellationToken).ConfigureAwait(false);

        }

        return new ListTaskPushNotificationConfigResponse
        {
            Configs =
            [
                .. pushNotifications.List(request.TaskId).Select(config => new TaskPushNotificationConfig
                {
                    Id = config.Id ?? string.Empty,
                    TaskId = request.TaskId,
                    PushNotificationConfig = config,
                }),
            ],
        };

    }

    public override async Task DeleteTaskPushNotificationConfigAsync(
        DeleteTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        await GuardPushTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);

        if (!pushNotifications.Enabled)
        {

            await base.DeleteTaskPushNotificationConfigAsync(request, cancellationToken).ConfigureAwait(false);

            return;

        }

        pushNotifications.Remove(request.TaskId);

    }

}
