using System.Collections.Concurrent;
using System.Globalization;

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

    /// <summary>
    /// How many tasks this process keeps in memory. A code-owned retention bound, not an operator knob:
    /// it is a ceiling on what is kept for nothing, and an operator who lowered it would only be choosing
    /// how quickly a peer's <c>tasks/get</c> for a finished Sending starts answering nothing.
    /// </summary>
    internal const int RetainedTaskCap = 512;

    /// <summary>Page size a <c>tasks/list</c> that names none gets, matching the SDK's own store.</summary>
    private const int DefaultListPageSize = 50;

    /// <summary>Ceiling on a requested page size, matching what the JSON-RPC surface already validates.</summary>
    private const int MaxListPageSize = 100;

    private readonly ConcurrentDictionary<string, RetainedTask> _live = new(StringComparer.Ordinal);

    private readonly Lock _evictionGate = new();

    private long _saves;

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return null;

        }

        if (_live.TryGetValue(taskId, out RetainedTask? live))
        {

            return live.Task;

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

        _live[taskId] = new RetainedTask(Interlocked.Increment(ref _saves), task);

        Evict();

        return Task.CompletedTask;

    }

    /// <summary>
    /// Drops the least recently touched <em>settled</em> tasks once the process is holding more than
    /// <see cref="RetainedTaskCap"/> of them.
    /// </summary>
    /// <remarks>
    /// The A2A SDK documents that it never calls <see cref="DeleteTaskAsync"/> and leaves pruning to the
    /// store, and nothing in Arcanum calls it either — so without this a host serving inbound Sendings
    /// keeps every one of them, and with <c>AutoAppendHistory</c> on that means the whole relayed history
    /// and the Apprentice's final artifact, until it restarts.
    /// <para>
    /// Only a task in a terminal state may go. One still <c>working</c> or <c>input-required</c> is live
    /// correspondence: dropping it would answer a peer that is still driving it <c>TaskNotFound</c>. A
    /// settled one is only being kept so a late <c>tasks/get</c> can still read it, and the durable
    /// Sending record — not this dictionary — is what carries a parked Sending across a restart.
    /// </para>
    /// </remarks>
    private void Evict()
    {

        if (_live.Count <= RetainedTaskCap)
        {

            return;

        }

        lock (_evictionGate)
        {

            int excess = _live.Count - RetainedTaskCap;

            if (excess <= 0)
            {

                return;

            }

            KeyValuePair<string, RetainedTask>[] evictable =
            [
                .. _live
                    .Where(static entry => TaskStateExtensions.IsTerminal(entry.Value.Task.Status.State))
                    .OrderBy(static entry => entry.Value.Sequence)
                    .Take(excess),
            ];

            foreach (KeyValuePair<string, RetainedTask> entry in evictable)
            {

                // Value-matched removal: a task re-saved since the snapshot carries a later sequence and
                // is left exactly where it is.
                _live.TryRemove(entry);

            }

        }

    }

    public Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {

        _live.TryRemove(taskId, out _);

        return Task.CompletedTask;

    }

    /// <summary>
    /// Lists the tasks this process is serving, honouring the request's filters and cursor.
    /// </summary>
    /// <remarks>
    /// Durable records are deliberately absent: the ledger is a correspondence index keyed by task id,
    /// not a task archive, and a listing that mixed live tasks with skeletal rehydrated ones would report
    /// A2A task state this instance cannot actually produce. Operators observe an inbound Sending as the
    /// Apprentice it is (§5.7.1.1) and its durable row through <c>arcanum operations</c>.
    /// <para>
    /// The filters, the cursor, and the projection are all the store's own responsibility — the SDK
    /// hands the request straight through — so a peer that scopes its query to one context, asks for one
    /// page, or declines the artifacts gets what it asked for rather than everything this process holds.
    /// </para>
    /// </remarks>
    public Task<ListTasksResponse> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        // Newest first, with the id as a tiebreak so a cursor means the same thing on every page even
        // when two tasks carry the same status timestamp.
        AgentTask[] matches =
        [
            .. _live.Values
                .Select(static entry => entry.Task)
                .Where(task => Matches(task, request))
                .OrderByDescending(static task => task.Status.Timestamp ?? DateTimeOffset.MinValue)
                .ThenBy(static task => task.Id, StringComparer.Ordinal),
        ];

        int cursor = ParseCursor(request.PageToken);

        int pageSize = request.PageSize is { } requested && requested > 0
            ? Math.Min(requested, MaxListPageSize)
            : DefaultListPageSize;

        List<AgentTask> page =
        [
            .. matches
                .Skip(cursor)
                .Take(pageSize)
                .Select(task => Project(task, request)),
        ];

        int next = cursor + page.Count;

        return Task.FromResult(new ListTasksResponse
        {
            Tasks = page,
            PageSize = page.Count,
            TotalSize = matches.Length,
            NextPageToken = next < matches.Length ? next.ToString(CultureInfo.InvariantCulture) : string.Empty,
        });

    }

    private static bool Matches(AgentTask task, ListTasksRequest request)
    {

        if (request.ContextId is { Length: > 0 } contextId
            && !string.Equals(task.ContextId, contextId, StringComparison.Ordinal))
        {

            return false;

        }

        if (request.Status is { } status && task.Status.State != status)
        {

            return false;

        }

        return request.StatusTimestampAfter is not { } after
            || (task.Status.Timestamp is { } timestamp && timestamp > after);

    }

    /// <summary>
    /// Builds the listed copy of a task, so trimming a listing never edits the task being served.
    /// </summary>
    private static AgentTask Project(AgentTask task, ListTasksRequest request) => new()
    {
        Id = task.Id,
        ContextId = task.ContextId,
        Status = task.Status,
        History = TrimHistory(task.History, request.HistoryLength),
        Artifacts = request.IncludeArtifacts == true ? task.Artifacts : null,
        Metadata = task.Metadata,
    };

    private static List<Message>? TrimHistory(List<Message>? history, int? historyLength)
    {

        if (historyLength is not { } length)
        {

            return history;

        }

        return length <= 0 || history is null
            ? null
            : [.. history.TakeLast(length)];

    }

    /// <summary>
    /// Reads the opaque cursor a previous page handed out.
    /// </summary>
    /// <remarks>
    /// An unreadable cursor is refused rather than treated as "start again": a client that silently got
    /// page one back every time would page forever without ever noticing it was not making progress.
    /// </remarks>
    private static int ParseCursor(string? pageToken)
    {

        if (string.IsNullOrEmpty(pageToken))
        {

            return 0;

        }

        return int.TryParse(pageToken, NumberStyles.None, CultureInfo.InvariantCulture, out int cursor)
            ? cursor
            : throw new A2AException($"Invalid pageToken: {pageToken}");

    }

    /// <summary>A retained task and the save that last touched it, which is the eviction order.</summary>
    private sealed record RetainedTask(long Sequence, AgentTask Task);

}
