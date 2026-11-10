using System.Runtime.CompilerServices;
using System.Text;
using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

public sealed partial class ArcanumA2AAgentHandler
{
    private readonly GuildCallbackWorkTracker _guildCallbacks = new(exception =>
        logger.LogWarning("A2A: callback work failed ({ExceptionType}).", exception.GetType().Name));

    internal async Task<Result<GuildInboundTask>> BeginGuildAsync(RequestContext context, GuildInboundPeer peer, CancellationToken ct)
    {
        ArcanumSettings current = options.CurrentValue;

        if (!current.Features.Guild || !current.ResolveA2A().ServerEnabled || !GuildA2AAdapter.Authenticates(scopeFactory, peer))
            return new Error(ErrorCodes.Guild.Forbidden, "The inbound task is unavailable.");

        string goal = context.UserText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(goal) || string.IsNullOrWhiteSpace(context.Message?.MessageId))
            return new Error(ErrorCodes.Guild.InvalidRequest, "An inbound Sending requires text and a stable message id.");

        Result<string> modality = A2AAgentCardPolicy.ValidateRequestedModes(current, context.Configuration?.AcceptedOutputModes);

        if (modality.IsFailure) return modality.Error;

        string[] inboundChain = ConclaveDelegationChain.Read(context.Message.Metadata);

        if (ConclaveDelegationChain.ContainsSelf(inboundChain))
            return new Error(ErrorCodes.Guild.InvalidRequest, "The Sending would create a delegation cycle.");

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

        if (host is null) return new Error(ErrorCodes.Guild.Unavailable, "The inbound Guild transport is unavailable.");

        Result<GuildInboundTask> accepted = context.Message.TaskId is { Length: > 0 } taskId
            ? await host.ContinueAsync(peer, taskId, context.Message.MessageId, goal, ct).ConfigureAwait(false)
            : await host.IssueAsync(peer, context.Message.MessageId, context.TaskId, context.Message.ContextId, goal, ct, ConclaveDelegationChain.Extend(inboundChain)).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        GuildInboundTask task = await GuildA2AAdapter.ReadAsync(scopeFactory, peer, accepted.Value.Binding.TaskId, ct).ConfigureAwait(false);

        await TryRegisterPushCallbackAsync(new RequestContext
        {
            TaskId = task.Binding.TaskId,
            ContextId = task.Binding.ContextId,
            Message = context.Message,
            Configuration = context.Configuration,
            StreamingResponse = context.StreamingResponse,
        }, ct).ConfigureAwait(false);

        _ = StartGuildCallback(peer, task.Binding.TaskId);

        return task;
    }

    private async Task ExecuteGuildAsync(RequestContext context, AgentEventQueue queue, CancellationToken ct)
    {
        GuildInboundPeer? peer = GuildA2AAdapter.Capture(scopeFactory);

        if (peer is null)
        {
            await RejectAsync(new(queue, context.TaskId, context.ContextId), "The inbound task is unavailable.", ct).ConfigureAwait(false);

            return;
        }

        Result<GuildInboundTask> accepted = await BeginGuildAsync(context, peer, ct).ConfigureAwait(false);

        if (accepted.IsFailure)
        {
            await RejectAsync(new(queue, context.TaskId, context.ContextId), accepted.Error.Code, ct).ConfigureAwait(false);

            return;
        }

        await foreach (StreamResponse frame in GuildA2AAdapter.WatchAsync(scopeFactory, peer, accepted.Value.Binding.TaskId,
            context.Configuration?.ReturnImmediately == true, ct).ConfigureAwait(false))
        {
            if (!GuildA2AAdapter.Authenticates(scopeFactory, peer)) throw GuildA2AAdapter.Missing();

            await queue.WriteAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private async Task CancelGuildAsync(RequestContext context, AgentEventQueue queue, CancellationToken ct)
    {
        GuildInboundPeer peer = GuildA2AAdapter.Capture(scopeFactory) ?? throw GuildA2AAdapter.Missing();

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

        if (host is null) throw GuildA2AAdapter.Missing();

        var result = await host.CancelAsync(peer, context.TaskId, ct).ConfigureAwait(false);

        if (result.IsFailure || !GuildA2AAdapter.Authenticates(scopeFactory, peer)) throw GuildA2AAdapter.Missing();

        await queue.EnqueueTaskAsync(GuildA2AAdapter.Project(result.Value), ct).ConfigureAwait(false);
    }

    internal bool StartGuildCallback(GuildInboundPeer peer, string taskId)
    {
        if (pushNotifications?.Resolve(taskId) is null || !GuildA2AAdapter.Authenticates(scopeFactory, peer)) return false;

        using IServiceScope scope = scopeFactory.CreateScope();

        GuildInboundPeerRegistry.Watch? watch = scope.ServiceProvider.GetService<GuildInboundPeerRegistry>()?.CaptureWatch(peer);

        return watch is not null && _guildCallbacks.Start(taskId, watch, token => WatchGuildCallbackAsync(watch, taskId, token)) is not null;
    }

    private GuildInboundPeer? ResolveGuildCallbackPeer(GuildInboundPeerRegistry.Watch watch)
    {
        if (!options.CurrentValue.Features.Guild) return null;

        using IServiceScope scope = scopeFactory.CreateScope();

        return scope.ServiceProvider.GetService<GuildInboundPeerRegistry>()?.ResolveWatch(watch);
    }

    private async Task<Result<HttpResponseMessage>> AdmitGuildCallbackAsync(GuildInboundPeerRegistry.Watch watch, string taskId,
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        while (ResolveGuildCallbackPeer(watch) is { } peer)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

            if (host is null) return new Error(ErrorCodes.Guild.Unavailable, "The task is unavailable.");

            bool handedOff = false;

            var delivered = await host.AdmitDisclosureAsync(peer, taskId, token =>
            {
                handedOff = true;

                return send(token);
            }, ct).ConfigureAwait(false);

            if (delivered.IsSuccess || handedOff || ResolveGuildCallbackPeer(watch) is not { } current
                || ReferenceEquals(current, peer)) return delivered;
        }

        return new Error(ErrorCodes.Guild.Forbidden, "The task is unavailable.");
    }

    private async Task WatchGuildCallbackAsync(GuildInboundPeerRegistry.Watch watch, string taskId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (pushNotifications?.Resolve(taskId) is null) return;

                GuildInboundPeer? peer = ResolveGuildCallbackPeer(watch);

                if (peer is null) return;

                GuildInboundTask current;

                try
                {
                    current = await GuildA2AAdapter.ReadAsync(scopeFactory, peer, taskId, ct).ConfigureAwait(false);
                }
                catch (A2AException)
                {
                    if (ResolveGuildCallbackPeer(watch) is { } replacement && !ReferenceEquals(replacement, peer)) continue;

                    return;
                }

                if (GuildA2AAdapter.Settled(current))
                {
                    if (ResolveGuildCallbackPeer(watch) is null) return;

                    await NotifyPeerAsync(taskId, current.Quest.State switch
                    {
                        QuestState.InputRequired => "input-required",
                        QuestState.Completed => "completed",
                        QuestState.Canceled => "canceled",
                        QuestState.Rejected => "rejected",
                        _ => "failed",
                    }, ct, (send, token) => AdmitGuildCallbackAsync(watch, taskId, send, token)).ConfigureAwait(false);

                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (A2AException) { }
        catch (Exception exception)
        {
            logger.LogWarning("A2A: Guild callback observation stopped ({ExceptionType}).", exception.GetType().Name);
        }
    }
}

/// <summary>Protocol projections require current peer authentication and canonical durable ownership.</summary>
internal static class GuildA2AAdapter
{
    internal static bool Enabled(IServiceScopeFactory? factory, IOptionsMonitor<ArcanumSettings>? settings = null)
    {
        if (settings is not null) return settings.CurrentValue.Features.Guild;

        if (factory is null) return false;

        using IServiceScope scope = factory.CreateScope();

        return scope.ServiceProvider.GetService<IOptionsMonitor<ArcanumSettings>>()?.CurrentValue.Features.Guild == true;
    }

    internal static GuildInboundPeer? Capture(IServiceScopeFactory? factory)
    {
        if (factory is null) return null;

        using IServiceScope scope = factory.CreateScope();

        GuildInboundPeer? peer = scope.ServiceProvider.GetService<GuildInboundRequestContext>()?.Peer;

        return peer is not null && scope.ServiceProvider.GetService<GuildInboundPeerRegistry>()?.Authenticates(peer) == true ? peer : null;
    }

    internal static bool Authenticates(IServiceScopeFactory? factory, GuildInboundPeer peer)
    {
        if (factory is null) return false;

        using IServiceScope scope = factory.CreateScope();

        return scope.ServiceProvider.GetService<IOptionsMonitor<ArcanumSettings>>()?.CurrentValue.Features.Guild == true
            && scope.ServiceProvider.GetService<GuildInboundPeerRegistry>()?.Authenticates(peer) == true;
    }

    internal static async Task<bool> IsGuildTaskAsync(IServiceScopeFactory? factory, string taskId, CancellationToken ct)
    {
        if (factory is null) return false;

        await using AsyncServiceScope scope = factory.CreateAsyncScope();

        GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

        if (host is null) return false;

        var result = await host.IsGuildTaskAsync(taskId, ct).ConfigureAwait(false);

        return result.IsFailure || result.Value;
    }

    internal static async Task<GuildInboundTask> ReadAsync(IServiceScopeFactory? factory, GuildInboundPeer peer, string taskId, CancellationToken ct, bool materialize = true)
    {
        if (factory is null || !Authenticates(factory, peer)) throw Missing();

        await using AsyncServiceScope scope = factory.CreateAsyncScope();

        GuildInboundHost? host = scope.ServiceProvider.GetService<GuildInboundHost>();

        if (host is null) throw Missing();

        var result = await host.ReadAsync(peer, taskId, materialize, ct).ConfigureAwait(false);

        if (result.IsFailure || !Authenticates(factory, peer)) throw Missing();

        return result.Value;
    }

    internal static A2AException Missing() => new("The task is unavailable.", A2AErrorCode.TaskNotFound);

    internal static bool Settled(GuildInboundTask task) => task.Quest.State is QuestState.InputRequired
        or QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected;

    internal static AgentTask Project(GuildInboundTask task) => new()
    {
        Id = task.Binding.TaskId,
        ContextId = task.Binding.ContextId,
        Status = new()
        {
            State = task.Quest.State switch
            {
                QuestState.Submitted => TaskState.Submitted,
                QuestState.Working => TaskState.Working,
                QuestState.InputRequired => TaskState.InputRequired,
                QuestState.Completed => TaskState.Completed,
                QuestState.Failed => TaskState.Failed,
                QuestState.Canceled => TaskState.Canceled,
                _ => TaskState.Rejected,
            },
            Timestamp = task.Quest.UpdatedAt,
            Message = (task.Question ?? (task.Quest.State is QuestState.Failed or QuestState.Rejected ? task.Result : null)) is not { } statusText ? null : new()
            {
                Role = Role.Agent,
                MessageId = task.Quest.AwaitingInput?.ContinuationId.ToString("N") ?? task.Quest.Id.ToString("N"),
                Parts = [Part.FromText(statusText)],
            },
        },
        History = [],
        Artifacts = task.Result is null ? [] : [new()
        {
            ArtifactId = task.Quest.ResultRef?.ContentId.ToString("N") ?? task.Quest.Id.ToString("N"),
            Parts = [Part.FromText(task.Result)],
        }],
    };

    internal static async IAsyncEnumerable<StreamResponse> WatchAsync(IServiceScopeFactory? factory, GuildInboundPeer peer,
        string taskId, bool returnImmediately, [EnumeratorCancellation] CancellationToken ct)
    {
        long revision = -1;

        long watermark = -1;

        while (true)
        {
            GuildInboundTask current = await ReadAsync(factory, peer, taskId, ct).ConfigureAwait(false);

            if (current.Quest.Revision != revision || current.Quest.EventWatermark != watermark)
            {
                AgentTask projected = Project(current);

                if (!Authenticates(factory, peer)) throw Missing();

                yield return new() { Task = projected };

                revision = current.Quest.Revision;

                watermark = current.Quest.EventWatermark;
            }

            if (returnImmediately || Settled(current)) yield break;

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    internal static string EncodeCursor(string? taskId) => taskId is null ? string.Empty
        : "guild:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(taskId));

    internal static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;

        if (!cursor.StartsWith("guild:", StringComparison.Ordinal) || cursor.Length > 5470) throw new A2AException("Invalid page token.", A2AErrorCode.InvalidParams);

        try
        {
            string decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(cursor[6..]));

            return GuildInboundTaskStore.ValidIdentifier(decoded) ? decoded : throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new A2AException("Invalid page token.", A2AErrorCode.InvalidParams);
        }
    }
}
