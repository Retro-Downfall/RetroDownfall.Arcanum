using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed class SessionDivinationService(
    ArcanumDbContext db,
    IGrimoireOrdinaryConnectionFactory connections,
    ILogger<SessionDivinationService> logger) : ISessionDivinationService
{
    private const int ContentPreviewChars = 200;

    public async Task<Result<SemanticSearchResult>> SearchAsync(
        Embedding<float> queryEmbedding,
        Guid? campaignId,
        string? status,
        int limit,
        float similarityThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
            .AcquireScopedAsync(scopedConnection, CovenantSqliteConnectionMode.ReadOnly, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            throw GrimoireConnectionUnavailableException.FromError(acquired.Error);
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        SqliteConnection connection = lease.Connection;

        try
        {
            // The rank cursor establishes the snapshot; selected metadata is read in the same transaction.
            // A concurrent archive, Campaign move or Entry edit cannot mix two different versions.
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            int take = ArcanumSettingClamps.EmbeddingsMaxResults(limit);

            Result<RankedHit[]> ranked;

            SemanticSessionSearchResult[] results;

            // Microsoft.Data.Sqlite executes native VM work synchronously. Token checks between rows
            // cannot interrupt an active step. The registration owns only this admitted scoped handle
            // and is joined before transaction cleanup or lease release can reuse/close it.
            using (CancellationTokenRegistration interrupt = cancellationToken.Register(
                static handle => SQLitePCL.raw.sqlite3_interrupt((SQLitePCL.sqlite3)handle!), connection.Handle))
            {
                ranked = await RankAsync(connection, transaction, queryEmbedding,
                    campaignId, status, take + 1, similarityThreshold, cancellationToken).ConfigureAwait(false);

                if (ranked.IsFailure)
                {
                    return Result<SemanticSearchResult>.Failure(ranked.Error);
                }

                results = await ReadMetadataAsync(connection, transaction,
                    ranked.Value.Take(take).ToArray(), cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<SemanticSearchResult>.Success(new(results, ranked.Value.Length > take, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9 && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning("Session Divination failed with SQLite code {Code} and extended code {ExtendedCode}.",
                ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);

            return Result<SemanticSearchResult>.Failure(new Error(ErrorCodes.Embeddings.ProviderUnavailable,
                "Session semantic search is temporarily unavailable."));
        }
    }

    private async Task<Result<RankedHit[]>> RankAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Embedding<float> queryEmbedding,
        Guid? campaignId,
        string? status,
        int take,
        float threshold,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT e."Id", e."CreatedAt", v."Embedding"
            FROM "entry_embeddings" v
            INNER JOIN "Entries" e ON e."Id" = v."EntryId"
            INNER JOIN "Sessions" s ON s."Id" = e."SessionId"
            WHERE s."Status" = @status
            """;

        command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant());

        if (campaignId is { } campaign)
        {
            command.CommandText += " AND s.\"CampaignId\" = @campaignId";

            command.Parameters.AddWithValue("@campaignId", campaign.ToString("D").ToUpperInvariant());
        }

        float[] query = queryEmbedding.Vector.ToArray();

        double norm = EmbeddingBlobCodec.NormSquared(query);

        PriorityQueue<RankedHit, RankedHit> top = new(WeakestFirst.Instance);

        int examined = 0;

        int scored = 0;

        int mismatched = 0;

        int unreadable = 0;

        int noSignal = 0;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                examined++;

                switch (DivinationService.TryScoreRow(reader[2], query, norm, out float similarity))
                {
                    case DivinationService.RowScore.DimensionMismatch:

                        mismatched++;

                        continue;

                    case DivinationService.RowScore.Unreadable:

                        unreadable++;

                        continue;

                    case DivinationService.RowScore.NoSignal:

                        noSignal++;

                        continue;
                }

                scored++;

                if (similarity < threshold)
                {
                    continue;
                }

                RankedHit hit = new(reader.GetString(0), reader.GetString(1), similarity);

                if (top.Count < take)
                {
                    top.Enqueue(hit, hit);
                }
                else if (top.TryPeek(out _, out RankedHit weakest) && WeakestFirst.Instance.Compare(hit, weakest) > 0)
                {
                    top.DequeueEnqueue(hit, hit);
                }
            }
        }

        if (mismatched + unreadable + noSignal > 0)
        {
            logger.LogWarning("Session Divination skipped embeddings: {Mismatched} of another width, {Unreadable} unreadable, {NoSignal} with no direction; query width {QueryWidth}.",
                mismatched, unreadable, noSignal, query.Length);
        }

        if (scored == 0 && mismatched > 0)
        {
            return Result<RankedHit[]>.Failure(new Error(ErrorCodes.Embeddings.DimensionMismatch,
                $"The stored embeddings are not {query.Length}-dimension vectors, so nothing could be compared with this query. Run an embeddings reset (POST /api/embeddings/reset?confirm=true) and re-index."));
        }

        RankedHit[] results = new RankedHit[top.Count];

        for (int i = results.Length - 1; i >= 0; i--)
        {
            results[i] = top.Dequeue();
        }

        await GrimoireScopedConsumerTestSeam.PauseAsync("SessionDivinationService.RankAsync",
            GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, examined, cancellationToken).ConfigureAwait(false);

        return Result<RankedHit[]>.Success(results);
    }

    private static async Task<SemanticSessionSearchResult[]> ReadMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RankedHit[] hits,
        CancellationToken cancellationToken)
    {
        if (hits.Length == 0)
        {
            return [];
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        string[] parameters = new string[hits.Length];

        for (int i = 0; i < hits.Length; i++)
        {
            parameters[i] = "@id" + i.ToString(CultureInfo.InvariantCulture);

            command.Parameters.AddWithValue(parameters[i], hits[i].Id);
        }

        command.Parameters.AddWithValue("@previewChars", ContentPreviewChars);

        command.CommandText = $"""
            SELECT e."Id", e."SessionId", substr(e."Content", 1, @previewChars), e."Role", e."CreatedAt", s."Title"
            FROM "Entries" e
            INNER JOIN "Sessions" s ON s."Id" = e."SessionId"
            WHERE e."Id" IN ({string.Join(", ", parameters)})
            """;

        Dictionary<string, SemanticSessionSearchResult> metadata = new(StringComparer.Ordinal);

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string id = reader.GetString(0);

                string content = reader.GetString(2);

                metadata.Add(id, new(Guid.Parse(reader.GetString(1)), reader.IsDBNull(5) ? null : reader.GetString(5),
                    Guid.Parse(id), ((MessageRole)reader.GetInt32(3)).ToString().ToLowerInvariant(),
                    content[..Utf8Truncation.SafeCharSliceLength(content, ContentPreviewChars)], 0,
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            }
        }

        SemanticSessionSearchResult[] results = hits.Select(hit => metadata[hit.Id] with { Similarity = hit.Similarity }).ToArray();

        await GrimoireScopedConsumerTestSeam.PauseAsync("SessionDivinationService.ReadMetadataAsync",
            GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, results.Length, cancellationToken).ConfigureAwait(false);

        return results;
    }

    private readonly record struct RankedHit(string Id, string CreatedAt, float Similarity);

    private sealed class WeakestFirst : IComparer<RankedHit>
    {
        public static WeakestFirst Instance { get; } = new();

        public int Compare(RankedHit left, RankedHit right)
        {
            int similarity = left.Similarity.CompareTo(right.Similarity);

            if (similarity != 0)
            {
                return similarity;
            }

            // Canonical UTC instants preserve every 100 ns tick under ordinal comparison.
            int date = string.CompareOrdinal(left.CreatedAt, right.CreatedAt);

            return date != 0 ? date : string.CompareOrdinal(right.Id, left.Id);
        }
    }
}
