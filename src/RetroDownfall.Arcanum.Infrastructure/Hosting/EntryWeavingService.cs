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
/// RAG Phase 2 — imprints not-yet-embedded Grimoire entries into The Weave in the background.
/// "Weaving" is the act of turning an entry's content into a vector (via <see cref="IWeaveService"/>)
/// and persisting it to <c>entry_embeddings</c> (and, when available, <c>entry_embeddings_vec</c>) so
/// Divination (see <c>SessionDivinationEndpoints</c>) can later retrieve it.
///
/// Idles (1s poll of <c>Arcanum:Embeddings:Enabled</c> / <c>Arcanum:Embeddings:SessionSearchEnabled</c>)
/// when either flag is false — the default — so this is a no-op on the hot path until an operator opts
/// in. Same idle-when-disabled pattern as
/// <see cref="RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService"/>.
///
/// Idempotency: each tick's <c>LEFT JOIN ... WHERE ee."EntryId" IS NULL</c> query naturally skips
/// already-embedded entries, so ticks are safe to run repeatedly and a partially-processed batch (e.g.
/// after a shutdown mid-tick) is simply picked up again on the next tick. Re-embedding after a model or
/// dimension change requires manually truncating <c>entry_embeddings</c> (and
/// <c>entry_embeddings_vec</c>, when present) — see DESIGN.md §21.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService embedding queue scheduler; covered via EntryWeavingServiceTests exercising the tick logic directly.
internal sealed class EntryWeavingService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IWeaveService weaveService,
    WeaveIndexAvailability weaveIndexAvailability,
    IServiceScopeFactory scopeFactory,
    ILogger<EntryWeavingService> logger) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        bool wasEnabled = false;

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

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

                await RunTickAsync(embeddings, stoppingToken).ConfigureAwait(false);

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

    internal async Task RunTickAsync(EmbeddingSettings embeddings, CancellationToken cancellationToken)
    {

        if (!weaveService.IsAvailable)
        {

            logger.LogDebug(
                "Entry Weaving tick skipped: The Weave is unavailable (Provider/Model not configured, or Embeddings disabled).");

            return;

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(embeddings.BatchSize);

        List<(string EntryId, string Content)> pending = await FetchUnembeddedEntriesAsync(db, batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {

            return;

        }

        List<string> contents = pending.ConvertAll(static p => p.Content);

        Result<Embedding<float>[]> embedResult = await weaveService.EmbedBatchAsync(contents, cancellationToken).ConfigureAwait(false);

        if (embedResult.IsFailure)
        {

            logger.LogWarning(
                "Entry Weaving embed batch failed ({Code}): {Message}",
                embedResult.Error.Code,
                embedResult.Error.Message);

            return;

        }

        Embedding<float>[] generated = embedResult.Value;

        for (int i = 0; i < pending.Count; i++)
        {

            await UpsertEmbeddingAsync(db, pending[i].EntryId, generated[i].Vector.ToArray(), cancellationToken).ConfigureAwait(false);

        }

    }

    private static Task<List<(string EntryId, string Content)>> FetchUnembeddedEntriesAsync(
        ArcanumDbContext db,
        int batchSize,
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
                cmd.CommandText =
                    """
                    SELECT e."Id", e."Content"
                    FROM "Entries" e
                    LEFT JOIN "entry_embeddings" ee ON ee."EntryId" = e."Id"
                    WHERE ee."EntryId" IS NULL
                        AND TRIM(e."Content") != ''
                    ORDER BY e."CreatedAt" DESC
                    LIMIT @limit
                    """;

                AddParameter(cmd, "@limit", batchSize);

                List<(string EntryId, string Content)> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    results.Add((reader.GetString(0), reader.GetString(1)));

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

                await using DbCommand cmd = connection.CreateCommand();

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

                if (!weaveIndexAvailability.IsVecAvailable)
                {

                    return;

                }

                await using DbCommand vecCmd = connection.CreateCommand();

                vecCmd.CommandText =
                    """
                    INSERT OR REPLACE INTO "entry_embeddings_vec" ("EntryId", "Embedding")
                    VALUES (@entryId, @embedding)
                    """;

                AddParameter(vecCmd, "@entryId", entryId);

                AddParameter(vecCmd, "@embedding", encoded);

                _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private static async Task<DbConnection> OpenConnectionAsync(ArcanumDbContext db, CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
