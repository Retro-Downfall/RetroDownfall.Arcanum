using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>Which side of the door a durable Sending record describes.</summary>
public enum A2ASendingRecordDirection
{

    Inbound = 0,

    Outbound = 1,

}

/// <summary>
/// Durable record of an in-flight A2A correspondence, stored in the <c>LongRunningOperations</c> ledger.
/// </summary>
/// <remarks>
/// The runtime task-id map is process memory (DESIGN &#167;5.4.4). That was fine for the map itself, but it also
/// meant a restart lost the only record that a remote task existed: an inbound peer's <c>tasks/cancel</c>
/// hit nothing, and an outbound remote task nobody could name kept running and billing (issue #62). This
/// is that missing record — deliberately the shared ledger from #39 rather than a bespoke A2A table.
/// </remarks>
public sealed record A2ASendingRecord
{

    /// <summary>Checkpoint schema version. Bumped only on a breaking shape change.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = A2ASendingLedger.CheckpointVersion;

    [JsonPropertyName("direction")]
    public A2ASendingRecordDirection Direction { get; init; }

    /// <summary>Inbound: the peer's A2A task id. Outbound: the remote agent's task id.</summary>
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    /// <summary>Inbound only: the Apprentice serving this task.</summary>
    [JsonPropertyName("apprenticeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ApprenticeId { get; init; }

    /// <summary>Outbound only: the discovery URL the Sending was dispatched to, needed to cancel it later.</summary>
    [JsonPropertyName("agentUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentUrl { get; init; }

}

/// <summary>Handle on a durable Sending record, used to close it out when the Sending settles.</summary>
public readonly record struct A2ASendingLedgerEntry(Guid OperationId, string OwnerId)
{

    public bool IsRecorded => OperationId != Guid.Empty;

}

/// <summary>
/// Registers and resolves durable A2A task correspondences.
/// </summary>
public interface IA2ASendingLedger
{

    Task<A2ASendingLedgerEntry> RegisterInboundAsync(string taskId, Guid apprenticeId, CancellationToken cancellationToken = default);

    Task<A2ASendingLedgerEntry> RegisterOutboundAsync(string remoteTaskId, string agentUrl, CancellationToken cancellationToken = default);

    /// <summary>Marks a recorded Sending settled, so reconciliation ignores it after the next restart.</summary>
    Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the Apprentice serving an inbound A2A task, including one recorded by a previous process.
    /// </summary>
    Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default);

}

/// <summary>
/// <see cref="IA2ASendingLedger"/> over the shared durable-operation ledger.
/// </summary>
/// <remarks>
/// Every method is best-effort: A2A must keep working when the Grimoire is unavailable, so a ledger
/// failure degrades to "no durable record" (the pre-#62 behavior) and is logged, never thrown at a peer.
/// </remarks>
internal sealed class A2ASendingLedger(
    ILongRunningOperationStore store,
    TimeProvider timeProvider,
    ILogger<A2ASendingLedger> logger) : IA2ASendingLedger
{

    internal const int CheckpointVersion = 1;

    /// <summary>
    /// A Sending has no whole-operation deadline (#55), so the lease is renewed by reconciliation rather
    /// than sized to the work. It exists to mark ownership, not to time the Sending out.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private static readonly string OwnerId = $"a2a-{Environment.ProcessId}";

    /// <summary>Rows fetched per lookup round-trip. A paging step, not a cap on in-flight Sendings.</summary>
    private const int PageSize = 200;

    public Task<A2ASendingLedgerEntry> RegisterInboundAsync(
        string taskId,
        Guid apprenticeId,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(
            LongRunningOperationKinds.A2AInboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Inbound,
                TaskId = taskId,
                ApprenticeId = apprenticeId,
            },
            $"Inbound A2A Sending serving task {taskId}.",
            cancellationToken);

    public Task<A2ASendingLedgerEntry> RegisterOutboundAsync(
        string remoteTaskId,
        string agentUrl,
        CancellationToken cancellationToken = default) =>
        RegisterAsync(
            LongRunningOperationKinds.A2AOutboundSending,
            new A2ASendingRecord
            {
                Direction = A2ASendingRecordDirection.Outbound,
                TaskId = remoteTaskId,
                AgentUrl = agentUrl,
            },
            $"Outbound A2A Sending awaiting remote task {remoteTaskId}.",
            cancellationToken);

    public async Task ReleaseAsync(A2ASendingLedgerEntry entry, CancellationToken cancellationToken = default)
    {

        if (!entry.IsRecorded)
        {

            return;

        }

        try
        {

            LongRunningOperation? current = await store.GetAsync(entry.OperationId, cancellationToken).ConfigureAwait(false);

            if (current is null)
            {

                return;

            }

            await store.TryTransitionAsync(
                    entry.OperationId,
                    current.Revision,
                    entry.OwnerId,
                    LongRunningOperationState.Completed,
                    timeProvider.GetUtcNow(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not close durable Sending record {OperationId}.", entry.OperationId);

        }

    }

    public async Task<Guid?> FindInboundApprenticeAsync(string taskId, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(taskId))
        {

            return null;

        }

        try
        {

            // Paged to exhaustion rather than capped: a lookup that quietly stopped after N rows would
            // be a hidden ceiling on how many Sendings an operator may have in flight, and the peer whose
            // task fell past it would be told "nothing to cancel" while the work kept running.
            for (int offset = 0; ; offset += PageSize)
            {

                IReadOnlyList<LongRunningOperation> page = await store
                    .ListAsync(
                        new LongRunningOperationQuery(
                            LongRunningOperationKinds.A2AInboundSending,
                            Limit: PageSize,
                            Offset: offset),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (page.Count == 0)
                {

                    break;

                }

                foreach (LongRunningOperation candidate in page)
                {

                    if (candidate.State is LongRunningOperationState.Completed
                        or LongRunningOperationState.Failed
                        or LongRunningOperationState.Abandoned)
                    {

                        continue;

                    }

                    if (TryRead(candidate) is { } record
                        && record.Direction == A2ASendingRecordDirection.Inbound
                        && string.Equals(record.TaskId, taskId, StringComparison.Ordinal)
                        && record.ApprenticeId is { } apprenticeId)
                    {

                        return apprenticeId;

                    }

                }

                if (page.Count < PageSize)
                {

                    break;

                }

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "A2A: could not resolve a durable Sending record for task {TaskId}.", taskId);

        }

        return null;

    }

    /// <summary>Reads a checkpointed record, treating any unreadable shape as "no record".</summary>
    internal static A2ASendingRecord? TryRead(LongRunningOperation operation)
    {

        if (operation.CheckpointPayload is not { Length: > 0 } payload
            || operation.CheckpointVersion != CheckpointVersion)
        {

            return null;

        }

        try
        {

            A2ASendingRecord? record = JsonSerializer.Deserialize(
                payload,
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            return record is { TaskId.Length: > 0 } ? record : null;

        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {

            return null;

        }

    }

    private async Task<A2ASendingLedgerEntry> RegisterAsync(
        string kind,
        A2ASendingRecord record,
        string publicSummary,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(record.TaskId))
        {

            return default;

        }

        try
        {

            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRunningOperation operation = await store
                .CreateAsync(
                    new LongRunningOperationCreateRequest(
                        kind,
                        LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                        publicSummary,
                        now),
                    cancellationToken)
                .ConfigureAwait(false);

            LongRunningOperationLeaseResult lease = await store
                .TryAcquireLeaseAsync(operation.Id, OwnerId, now, now.Add(LeaseDuration), cancellationToken)
                .ConfigureAwait(false);

            if (!lease.Acquired)
            {

                return default;

            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                record,
                A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

            await store
                .SaveCheckpointAsync(
                    operation.Id,
                    OwnerId,
                    expectedCheckpointVersion: 0,
                    checkpointVersion: CheckpointVersion,
                    payload,
                    checkpointReference: null,
                    publicSummary,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            return new A2ASendingLedgerEntry(operation.Id, OwnerId);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            // A2A stays usable without the Grimoire: losing the durable record costs restart
            // reconciliation, not the Sending itself.
            logger.LogWarning(ex, "A2A: could not record a durable Sending for task {TaskId}.", record.TaskId);

            return default;

        }

    }

}

/// <summary>
/// Resolves an <see cref="IA2ASendingLedger"/> from a scope, for the singleton A2A services.
/// </summary>
internal static class A2ASendingLedgerScope
{

    internal static IA2ASendingLedger? Resolve(IServiceProvider services) =>
        services.GetService<IA2ASendingLedger>();

}

/// <summary>Source-generated contract so the durable Sending checkpoint stays Native AOT-safe.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(A2ASendingRecord))]
internal sealed partial class A2ASendingLedgerJsonContext : JsonSerializerContext;
