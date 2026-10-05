using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Session Divination support: imprints not-yet-embedded Grimoire entries into The Weave in the background.
/// "Weaving" is the act of turning an entry's content into a vector (via <see cref="IWeaveService"/>)
/// and persisting it to <c>entry_embeddings</c> (and, when available, <c>entry_embeddings_vec</c>) so
/// Divination (see <c>SessionDivinationEndpoints</c>) can later retrieve it.
///
/// Idles unless <c>Arcanum:Features:SessionSearch</c> is enabled; the polling cadence is a code-owned
/// invariant. This is therefore a no-op on the hot path until an operator opts in.
///
/// Idempotency: each tick's <c>LEFT JOIN ... WHERE ee."EntryId" IS NULL</c> query naturally skips
/// already-embedded entries, so ticks are safe to run repeatedly and a partially-processed batch (e.g.
/// after a shutdown mid-tick) is simply picked up again on the next tick. Re-embedding after a model or
/// dimension change requires manually truncating <c>entry_embeddings</c> (and
/// <c>entry_embeddings_vec</c>, when present) — see <c>docs/Arcanum.DESIGN.md</c> §21.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService embedding queue scheduler; covered via EntryWeavingServiceTests exercising the tick logic directly.
internal sealed class EntryWeavingService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IWeaveService weaveService,
    WeaveIndexAvailability weaveIndexAvailability,
    IServiceScopeFactory scopeFactory,
    IGrimoireConnectionAdmissionGate admissionGate,
    ILogger<EntryWeavingService> logger) : BackgroundService
{
    /// <summary>
    /// The most entries whose failed batches are tracked at once. A full table stops tracking new
    /// failures rather than growing, which only costs the ladder, never correctness.
    /// </summary>
    private const int MaxTrackedFailures = 256;

    /// <summary>The longest an entry waits between retries, in ticks (an hour at the default cadence).</summary>
    private const int MaxBackoffTicks = 360;

    private readonly object _failuresGate = new();

    /// <summary>
    /// Process-local failure ladder, keyed by entry id. A restart starts every ladder over, which is
    /// the right trade for a bookkeeping table that exists to stop a failing batch being billed again
    /// every interval, not to remember failures.
    /// </summary>
    private readonly Dictionary<string, EmbeddingFailure> _failures = new(StringComparer.Ordinal);

    private long _tick;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        bool wasEnabled = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

                bool enabled = embeddings.Enabled && embeddings.SessionSearchEnabled;

                if (!enabled)
                {
                    wasEnabled = false;

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    continue;
                }

                if (!wasEnabled)
                {
                    logger.LogInformation("Entry Weaving started imprinting Grimoire entries into The Weave.");

                    wasEnabled = true;
                }

                EntryWeavingTickOutcome outcome = await RunTickAsync(embeddings, stoppingToken)
                    .ConfigureAwait(false);

                if (outcome == EntryWeavingTickOutcome.DeferredForMaintenance)
                {
                    // Debug, and then the ordinary cadence below. A maintenance window is expected
                    // and temporary, so it must not reach the catch-all's Error log or its
                    // one-second backoff: that would report a deliberate refusal as a product fault
                    // once a second for the length of the window, each with a stack trace.
                    logger.LogDebug(
                        "Entry Weaving deferred this tick: maintenance owns Grimoire admission.");
                }

                int intervalSeconds = ArcanumSettingClamps.EmbeddingsEmbeddingQueueIntervalSeconds(
                    embeddings.EmbeddingQueueIntervalSeconds);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Entry Weaving tick failed; continuing.");

                // Unlike the normal-path delay above (which uses the configured queue interval), a
                // persistent failure here (e.g. a broken DB connection) must still back off before
                // retrying — without this, a tight loop with no delay would spin, flooding logs and
                // burning CPU until the underlying problem is fixed.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Runs one imprinting tick, reporting whether maintenance stood it down.</summary>
    /// <remarks>
    /// The work lease is declared before the scope so that reverse-order disposal releases the scope
    /// first. That ordering is the point of the lease, not an incidental detail: releasing it around
    /// the scope instead would let a transition's stage one conclude the worker had drained while
    /// the pooled context and its enrolled physical handle were still going back, which is the exact
    /// window an offline transition must not close in.
    /// </remarks>
    internal async Task<EntryWeavingTickOutcome> RunTickAsync(
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {
        if (!weaveService.IsAvailable)
        {
            logger.LogDebug(
                "Entry Weaving tick skipped: The Weave is unavailable (enable an embedding-backed Arcanum:Features option and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model).");

            return EntryWeavingTickOutcome.Woven;
        }

        if (!admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.EntryWeaving,
                out IGrimoireWorkLease? workLease))
        {
            return EntryWeavingTickOutcome.DeferredForMaintenance;
        }

        await using IGrimoireWorkLease lease = workLease!;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(embeddings.BatchSize);

        int chunkSizeChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(embeddings.ChunkSizeChars);

        (long tick, int backingOff) = BeginTick();

        // Entries still waiting out a failure are filtered after the read, so the read is widened by
        // their number: filtering without widening would let them hold the head of the queue and starve
        // every entry behind them, which is the reason empty content is filtered in SQL.
        List<(string EntryId, string Content)> fetched = await FetchUnembeddedEntriesAsync(
                db,
                batchSize + backingOff,
                chunkSizeChars,
                cancellationToken)
            .ConfigureAwait(false);

        List<(string EntryId, string Content)> pending = SelectEligible(fetched, tick, batchSize);

        if (pending.Count == 0)
        {
            return EntryWeavingTickOutcome.Woven;
        }

        // One group for the provider call and every write that follows from it. The span is wider
        // than the upserts because IWeaveService.EmbedBatchAsync opens an accounting scope of its
        // own and writes an inference-run row before any provider I/O, then completes it after — so
        // a group that began after the provider returned would leave the billable call and its own
        // durable row outside the frontier entirely.
        if (!lease.TryBeginExternalEffectGroup(
                out IGrimoireExternalEffectGroup? effectGroup))
        {
            return EntryWeavingTickOutcome.DeferredForMaintenance;
        }

        await using IGrimoireExternalEffectGroup effect = effectGroup!;

        List<string> contents = pending.ConvertAll(static p => p.Content);

        // The host token, never the lease's revocation. Once the frontier is won maintenance waits
        // through this group and its durable disposition rather than cancelling into it.
        Result<Embedding<float>[]> embedResult;

        try
        {
            embedResult = await weaveService.EmbedBatchAsync(contents, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure(pending, 0, tick);

            throw;
        }

        if (embedResult.IsFailure)
        {
            logger.LogWarning(
                "Entry Weaving embed batch failed ({Code}): {Message}",
                embedResult.Error.Code,
                embedResult.Error.Message);

            RecordFailure(pending, 0, tick);

            return EntryWeavingTickOutcome.Woven;
        }

        Embedding<float>[] generated = embedResult.Value;

        // IWeaveService answers exactly one vector per input or fails (WeaveService enforces it at the
        // provider boundary), so generated[i] always pairs with pending[i] and a short provider reply
        // can no longer reach the upsert loop as an IndexOutOfRangeException retry-and-rebill spin.
        //
        // The provider call is billed once it returns, so the writes that keep its answer run on a token
        // the host cannot cancel. A shutdown arriving here would otherwise drop what was paid for, and
        // the next tick would select the same entries and pay for them again.
        for (int i = 0; i < pending.Count; i++)
        {
            try
            {
                await UpsertEmbeddingAsync(db, pending[i].EntryId, generated[i].Vector.ToArray(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The failure is in storage, not in the provider, but the rows from here on are
                // re-selected and re-billed all the same.
                RecordFailure(pending, i, tick);

                throw;
            }

            ClearFailure(pending[i].EntryId);
        }

        return EntryWeavingTickOutcome.Woven;
    }

    /// <summary>
    /// Starts a tick that reached the entry queue and reports how many entries are still waiting out a
    /// failure. A tick refused its work lease never gets here, so a maintenance window does not age
    /// the ladder.
    /// </summary>
    private (long Tick, int BackingOff) BeginTick()
    {
        lock (_failuresGate)
        {
            long tick = ++_tick;

            int backingOff = 0;

            foreach (EmbeddingFailure failure in _failures.Values)
            {
                if (failure.RetryAtTick > tick)
                {
                    backingOff++;
                }
            }

            return (tick, backingOff);
        }
    }

    private List<(string EntryId, string Content)> SelectEligible(
        List<(string EntryId, string Content)> fetched,
        long tick,
        int batchSize)
    {
        List<(string EntryId, string Content)> eligible = new(Math.Min(fetched.Count, batchSize));

        lock (_failuresGate)
        {
            foreach ((string entryId, string content) in fetched)
            {
                if (eligible.Count == batchSize)
                {
                    break;
                }

                if (_failures.TryGetValue(entryId, out EmbeddingFailure failure) && failure.RetryAtTick > tick)
                {
                    continue;
                }

                eligible.Add((entryId, content));
            }
        }

        return eligible;
    }

    /// <summary>
    /// Charges a failed batch to its entries: each waits one tick, then two, then four and so on, up to
    /// <see cref="MaxBackoffTicks"/>, before it is selected again, so a batch the provider keeps
    /// refusing is not billed again every interval and does not hold back the entries behind it.
    /// </summary>
    private void RecordFailure(List<(string EntryId, string Content)> batch, int firstFailed, long tick)
    {
        lock (_failuresGate)
        {
            for (int index = firstFailed; index < batch.Count; index++)
            {
                string entryId = batch[index].EntryId;

                int attempts = 1;

                if (_failures.TryGetValue(entryId, out EmbeddingFailure existing))
                {
                    attempts = existing.Attempts + 1;
                }
                else if (_failures.Count >= MaxTrackedFailures && !TryMakeRoom(tick))
                {
                    continue;
                }

                long wait = Math.Min(1L << Math.Min(attempts - 1, 30), MaxBackoffTicks);

                _failures[entryId] = new EmbeddingFailure(attempts, tick + 1 + wait);
            }
        }
    }

    /// <summary>Drops entries whose wait is over to make room, reporting whether any room was made.</summary>
    private bool TryMakeRoom(long tick)
    {
        string[] settled = [.. _failures.Where(pair => pair.Value.RetryAtTick <= tick).Select(static pair => pair.Key)];

        foreach (string entryId in settled)
        {
            _ = _failures.Remove(entryId);
        }

        return _failures.Count < MaxTrackedFailures;
    }

    private void ClearFailure(string entryId)
    {
        lock (_failuresGate)
        {
            _ = _failures.Remove(entryId);
        }
    }

    private readonly record struct EmbeddingFailure(int Attempts, long RetryAtTick);

    private static Task<List<(string EntryId, string Content)>> FetchUnembeddedEntriesAsync(
        ArcanumDbContext db,
        int batchSize,
        int chunkSizeChars,
        CancellationToken cancellationToken)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // TRIM(e."Content") != '' filters empty-content entries in SQL rather than after the
                // fetch: filtering post-fetch would let empty-content rows permanently occupy LIMIT
                // slots every tick (they never gain an entry_embeddings row, so the LEFT JOIN keeps
                // re-selecting them), starving real work.
                //
                // SUBSTR(..., 1, @chunkSize) applies the same code-owned per-item ceiling that
                // WeaveService.EmbedBatchAsync applies before the provider.
                cmd.CommandText =
                    """
                    SELECT e."Id", SUBSTR(e."Content", 1, @chunkSize)
                    FROM "Entries" e
                    LEFT JOIN "entry_embeddings" ee ON ee."EntryId" = e."Id"
                    WHERE ee."EntryId" IS NULL
                        AND TRIM(e."Content") != ''
                    ORDER BY e."CreatedAt" DESC
                    LIMIT @limit
                    """;

                AddParameter(cmd, "@limit", batchSize);

                AddParameter(cmd, "@chunkSize", chunkSizeChars);

                List<(string EntryId, string Content)> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string content = reader.GetString(1);

                    // Defensive surrogate-safe slice: SQLite SUBSTR may land on a UTF-16 high
                    // surrogate; match WeaveService.EmbedBatchAsync's SafeCharSliceLength boundary.
                    if (content.Length > chunkSizeChars)
                    {
                        content = content[..Utf8Truncation.SafeCharSliceLength(content, chunkSizeChars)];
                    }
                    else if (content.Length > 0 && char.IsHighSurrogate(content[^1]))
                    {
                        content = content[..^1];
                    }

                    results.Add((reader.GetString(0), content));
                }

                return results;
            },
            cancellationToken);
    }

    private Task UpsertEmbeddingAsync(
        ArcanumDbContext db,
        string entryId,
        float[] vector,
        CancellationToken cancellationToken)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                byte[] encoded = EmbeddingBlobCodec.Encode(vector);

                // One transaction for the embedding and its mirror row, so a mirror row an earlier build
                // wrote never outlives the embedding that replaced it.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using (DbCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    cmd.CommandText =
                        """
                        INSERT INTO "entry_embeddings" ("EntryId", "Embedding", "Dim")
                        VALUES (@entryId, @embedding, @dim)
                        ON CONFLICT("EntryId") DO UPDATE SET
                            "Embedding" = @embedding,
                            "Dim" = @dim
                        """;

                    AddParameter(cmd, "@entryId", entryId);

                    AddParameter(cmd, "@embedding", encoded);

                    AddParameter(cmd, "@dim", vector.Length);

                    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (weaveIndexAvailability.IsVecAvailable)
                {
                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText =
                        """
                        INSERT OR REPLACE INTO "entry_embeddings_vec" ("EntryId", "Embedding")
                        VALUES (@entryId, @embedding)
                        """;

                    AddParameter(vecCmd, "@entryId", entryId);

                    AddParameter(vecCmd, "@embedding", encoded);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The mirror cannot be rewritten without the accelerator, so a row an earlier build
                    // wrote would keep describing an embedding this entry no longer has. It goes through
                    // the shared helper, which classifies the mirror first: a plain one loses the row,
                    // and a legacy virtual one this runtime cannot open is left alone.
                    _ = await SagaVectorMirror.DeleteAsync(
                        connection,
                        transaction,
                        "entry_embeddings_vec",
                        "EntryId",
                        entryId,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static async Task<DbConnection> OpenConnectionAsync(ArcanumDbContext db, CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);
    }
}
