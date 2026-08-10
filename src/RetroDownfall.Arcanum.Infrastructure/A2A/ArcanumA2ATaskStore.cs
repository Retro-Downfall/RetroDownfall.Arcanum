using System.Collections.Concurrent;

using A2A;

using A2ATaskStatus = A2A.TaskStatus;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// The A2A SDK's task store, backed by process memory for live tasks and by the durable Sending ledger
/// for one that outlived the process which created it.
/// </summary>
/// <remarks>
/// <para>
/// The SDK resolves the task <em>before</em> it ever calls <see cref="IAgentHandler"/>: a message or a
/// <c>tasks/cancel</c> naming an id the store does not know is answered <c>TaskNotFound</c> and the
/// handler never runs. With a purely in-memory store that made every post-restart continuation and every
/// post-restart peer cancel unreachable, however durable the Apprentice underneath was — the handler's
/// own ledger fallback could never be reached to matter (issues #62, #68).
/// </para>
/// <para>
/// Only a Sending <em>parked awaiting an answer</em> is rehydrated. A task that was merely mid-flight
/// cannot be resurrected — its relay and its peer connection both died — and reconciliation abandons it
/// with a named reason instead (§5.7.1.2). Rehydration is therefore deliberately minimal: the id, the
/// context it belongs to, and <c>input-required</c>, which is the state the peer is answering.
/// </para>
/// </remarks>
internal sealed class ArcanumA2ATaskStore(
    IServiceScopeFactory? scopeFactory,
    ILogger<ArcanumA2ATaskStore> logger) : ITaskStore
{

    private readonly ConcurrentDictionary<string, AgentTask> _live = new(StringComparer.Ordinal);

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return null;

        }

        if (_live.TryGetValue(taskId, out AgentTask? live))
        {

            return live;

        }

        if (scopeFactory is null)
        {

            return null;

        }

        try
        {

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

            if (A2ASendingLedgerScope.Resolve(scope.ServiceProvider) is not { } ledger)
            {

                return null;

            }

            // A read, not a claim: the SDK looks a task up on every store miss, and taking the record's
            // lease here would churn its attempt count without anyone intending to own it. The handler
            // claims it when it actually resumes the Apprentice.
            A2AParkedSending? parked = await ledger
                .FindParkedInboundAsync(taskId, takeLease: false, cancellationToken)
                .ConfigureAwait(false);

            if (parked is null)
            {

                return null;

            }

            logger.LogInformation(
                "A2A: rehydrating parked task {TaskId} from the durable Sending record so the peer's "
                + "follow-up reaches the Apprentice that asked for it.",
                taskId);

            return new AgentTask
            {
                Id = taskId,
                ContextId = parked.Value.ContextId ?? Guid.NewGuid().ToString("N"),
                Status = new A2ATaskStatus { State = TaskState.InputRequired },
                History = [],
                Artifacts = [],
            };

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            // Best-effort, exactly like every other ledger read: a Grimoire that is unavailable degrades
            // to the pre-#68 behavior rather than failing the peer's request.
            logger.LogWarning(ex, "A2A: could not look up a durable record for task {TaskId}.", taskId);

            return null;

        }

    }

    public Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        ArgumentNullException.ThrowIfNull(task);

        _live[taskId] = task;

        return Task.CompletedTask;

    }

    public Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {

        _live.TryRemove(taskId, out _);

        return Task.CompletedTask;

    }

    /// <summary>
    /// Lists the tasks this process is serving.
    /// </summary>
    /// <remarks>
    /// Durable records are deliberately absent: the ledger is a correspondence index keyed by task id,
    /// not a task archive, and a listing that mixed live tasks with skeletal rehydrated ones would report
    /// A2A task state this instance cannot actually produce. Operators observe an inbound Sending as the
    /// Apprentice it is (§5.7.1.1) and its durable row through <c>arcanum operations</c>.
    /// </remarks>
    public Task<ListTasksResponse> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ListTasksResponse { Tasks = [.. _live.Values] });

}
