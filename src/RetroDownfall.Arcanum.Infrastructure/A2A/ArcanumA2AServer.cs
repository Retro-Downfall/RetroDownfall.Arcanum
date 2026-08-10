using A2A;

using Microsoft.Extensions.Logging;

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
    A2AServerOptions options) : A2AServer(handler, taskStore, notifier, logger, options)
{

    public override async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        CreateTaskPushNotificationConfigRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

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

        if (!pushNotifications.Enabled)
        {

            await base.DeleteTaskPushNotificationConfigAsync(request, cancellationToken).ConfigureAwait(false);

            return;

        }

        pushNotifications.Remove(request.TaskId);

    }

}
