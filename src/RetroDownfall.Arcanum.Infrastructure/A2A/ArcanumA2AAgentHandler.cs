using System.Collections.Concurrent;

using A2A;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// The A2A server side of the Conclave: implements the SDK's <see cref="IAgentHandler"/> so external A2A
/// clients can drive Arcanum Apprentices. An inbound message spawns a headless Apprentice via the same
/// <see cref="IConclaveArchmage"/> used by <c>cast_sending</c> and <c>POST /api/apprentices/{id}/cast</c>;
/// the Apprentice's own Chronicle is then forwarded onto the A2A task lifecycle until a terminal state.
/// </summary>
/// <remarks>
/// A2A tasks map to Apprentices, not Sessions: the task lifecycle IS the Apprentice lifecycle, so this
/// class does no independent scheduling — it starts the Apprentice via <see cref="IApprenticeRuntime"/> and
/// then simply relays <see cref="IApprenticeRuntime.SubscribeChronicleAsync"/>. The A2A task id ↔ Apprentice
/// id association lives only in <see cref="_taskToApprentice"/> (in-memory; no Grimoire persistence — see
/// persistence.md), so it does not survive a process restart.
/// </remarks>
public sealed class ArcanumA2AAgentHandler(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<ArcanumA2AAgentHandler> logger) : IAgentHandler, IAsyncDisposable
{

    private readonly ConcurrentDictionary<string, Guid> _taskToApprentice = new(StringComparer.Ordinal);

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {

        TaskUpdater updater = new(eventQueue, context.TaskId, context.ContextId);

        ArcanumSettings settings = options.CurrentValue;

        ConclaveA2ASettings a2a = settings.Conclave.A2A ?? new ConclaveA2ASettings();

        if (!settings.Conclave.Enabled || !a2a.Enabled || !a2a.ServerEnabled)
        {

            await RejectAsync(updater, "A2A is disabled on this Arcanum instance.", cancellationToken).ConfigureAwait(false);

            return;

        }

        string goal = context.UserText?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(goal))
        {

            await RejectAsync(updater, "A non-empty message is required to spawn an Apprentice.", cancellationToken).ConfigureAwait(false);

            return;

        }

        Result<string> workspaceResult = ResolveWorkspace(settings, a2a);

        if (workspaceResult.IsFailure)
        {

            await RejectAsync(updater, $"No usable workspace is configured: {workspaceResult.Error.Message}", cancellationToken).ConfigureAwait(false);

            return;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IConclaveArchmage archmage = scope.ServiceProvider.GetRequiredService<IConclaveArchmage>();

        Result<Apprentice> castResult = await archmage
            .CastAsync(new ConclaveCastRequest(goal, WorkspacePath: workspaceResult.Value), cancellationToken)
            .ConfigureAwait(false);

        if (castResult.IsFailure)
        {

            await RejectAsync(updater, $"Could not spawn an Apprentice: {castResult.Error.Message}", cancellationToken).ConfigureAwait(false);

            return;

        }

        Apprentice apprentice = castResult.Value;

        _taskToApprentice[context.TaskId] = apprentice.Id;

        await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        Result<string> startResult = await runtime.StartAsync(apprentice.Id, cancellationToken).ConfigureAwait(false);

        if (startResult.IsFailure)
        {

            await FailAsync(updater, $"Apprentice '{apprentice.Id}' was created but could not be started: {startResult.Error.Message}", cancellationToken)
                .ConfigureAwait(false);

            _taskToApprentice.TryRemove(context.TaskId, out _);

            return;

        }

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {

            await ForwardChronicleToTaskAsync(apprentice.Id, updater, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _taskToApprentice.TryRemove(context.TaskId, out _);

        }

    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {

        if (!_taskToApprentice.TryGetValue(context.TaskId, out Guid apprenticeId))
        {

            return;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        // Best-effort: this triggers Apprentice cancellation, which ExecuteAsync's own Chronicle
        // subscription (still running as the SDK's background task for this same A2A task) will
        // observe as ApprenticeCancelled and use to drive the terminal TaskUpdater.CancelAsync
        // transition itself. We do not call updater.CancelAsync here too, to avoid racing two
        // terminal-state transitions on the same task from two different call paths.
        Result<string> cancelResult = await runtime.CancelAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (cancelResult.IsFailure)
        {

            logger.LogInformation(
                "A2A CancelAsync: Apprentice {ApprenticeId} for task {TaskId} could not be cancelled: {Message}",
                apprenticeId,
                context.TaskId,
                cancelResult.Error.Message);

        }

    }

    private async Task ForwardChronicleToTaskAsync(Guid apprenticeId, TaskUpdater updater, CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRuntime runtime = scope.ServiceProvider.GetRequiredService<IApprenticeRuntime>();

        await foreach (ApprenticeEvent @event in runtime.SubscribeChronicleAsync(apprenticeId, cancellationToken).ConfigureAwait(false))
        {

            switch (@event.Type)
            {

                case ApprenticeEventType.ApprenticeEscalated:

                    await RequireInputAsync(
                        updater,
                        @event.Error ?? @event.Summary ?? "The Apprentice petitioned the Dungeon Master for guidance.",
                        cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeCompleted:

                    string finalText = await ExtractFinalTextAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

                    await updater.AddArtifactAsync([Part.FromText(finalText)], cancellationToken: cancellationToken).ConfigureAwait(false);

                    await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeFailed:

                    await FailAsync(updater, @event.Error ?? "The Apprentice failed.", cancellationToken).ConfigureAwait(false);

                    return;

                case ApprenticeEventType.ApprenticeCancelled:

                    await updater.CancelAsync(cancellationToken).ConfigureAwait(false);

                    return;

                default:

                    // Step-level events (planGenerated, stepStarted/Completed, toolCall/Result, ...) have
                    // no A2A TaskState equivalent beyond "Working" (already emitted); only the terminal
                    // transitions above matter for the A2A task lifecycle in this pass.
                    continue;

            }

        }

    }

    private async Task<string> ExtractFinalTextAsync(Guid apprenticeId, CancellationToken cancellationToken)
    {

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IApprenticeRepository apprenticeRepo = scope.ServiceProvider.GetRequiredService<IApprenticeRepository>();

        Apprentice? apprentice = await apprenticeRepo.GetByIdAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (apprentice?.SessionId is not { } sessionId)
        {

            return "(The Apprentice completed without a recorded session.)";

        }

        ISessionRepository sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        List<Entry> entries = await sessionRepo.GetEntriesAscendingAsync(sessionId, takeLast: 20, cancellationToken).ConfigureAwait(false);

        Entry? lastAssistantEntry = entries.LastOrDefault(static e => e.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(e.Content));

        return lastAssistantEntry?.Content ?? "(The Apprentice completed without a textual final message.)";

    }

    private static Result<string> ResolveWorkspace(ArcanumSettings settings, ConclaveA2ASettings a2a)
    {

        if (!string.IsNullOrWhiteSpace(a2a.DefaultWorkspace))
        {

            return CampaignPathPolicy.ValidateAndNormalizePath(a2a.DefaultWorkspace, settings);

        }

        if (!string.IsNullOrWhiteSpace(settings.Host?.Workspace))
        {

            return CampaignPathPolicy.ValidateAndNormalizePath(settings.Host.Workspace, settings);

        }

        return CampaignPathPolicy.ValidateAndNormalizePath(Directory.GetCurrentDirectory(), settings);

    }

    private static Task RejectAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken) =>
        RunTerminalTransitionAsync(
            async ct =>
            {
                await updater.SubmitAsync(ct).ConfigureAwait(false);

                await updater.RejectAsync(BuildAgentMessage(reason), ct).ConfigureAwait(false);
            },
            cancellationToken);

    private static Task FailAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken) =>
        RunTerminalTransitionAsync(ct => updater.FailAsync(BuildAgentMessage(reason), ct).AsTask(), cancellationToken);

    private static Task RequireInputAsync(TaskUpdater updater, string reason, CancellationToken cancellationToken) =>
        RunTerminalTransitionAsync(ct => updater.RequireInputAsync(BuildAgentMessage(reason), ct).AsTask(), cancellationToken);

    /// <summary>
    /// Terminal <see cref="TaskUpdater"/> transitions are best-effort: if the task already reached a
    /// terminal state via another path (e.g. a concurrent cancel), the SDK may reject a second
    /// transition. That race is expected here and must never surface as an unhandled exception from an
    /// <see cref="IAgentHandler"/> callback.
    /// </summary>
    private static async Task RunTerminalTransitionAsync(Func<CancellationToken, Task> transition, CancellationToken cancellationToken)
    {

        try
        {

            await transition(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception)
        {

            // Best-effort terminal transition; see remarks above.

        }

    }

    private static Message BuildAgentMessage(string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        Parts = [Part.FromText(text)],
    };

    public ValueTask DisposeAsync()
    {

        _taskToApprentice.Clear();

        return ValueTask.CompletedTask;

    }

}
