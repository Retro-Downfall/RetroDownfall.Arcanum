using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Operator-facing reset for RAG embedding tables when
/// <c>Arcanum:Integrations:Embeddings:Dimensions</c> (or the embedding model) changes. Clears the
/// requested scope's embedding table(s) plus any companion metadata tables that would otherwise
/// make the reset silently ineffective. See <c>docs/Arcanum.DESIGN.md</c> §21.
/// </summary>
public sealed class EmbeddingsResetService(
    ArcanumDbContext db,
    WeaveIndexAvailability availability,
    IServiceProvider serviceProvider,
    ICovenantSensitiveArtifactPurger? purger = null)
{

    private readonly IGrimoireOrdinaryConnectionFactory _connections =
        serviceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

    private static readonly IReadOnlyList<string> EntryTables =
    [
        "entry_embeddings",
        "entry_embeddings_vec",
    ];

    private static readonly IReadOnlyList<string> WorkspaceFileTables =
    [
        "workspace_file_embeddings",
        "workspace_file_embeddings_vec",
        "workspace_file_chunks",
    ];

    /// <summary>
    /// The Saga scope's tables, and the one list in this file that names a row something else keeps a
    /// record of.
    /// </summary>
    /// <remarks>
    /// <c>saga_memories</c> is the subject an Annals claim binds to, and the Annals reach a subject only
    /// through the row that names it - so truncating this table without taking that store's claims would
    /// leave records describing memories that are gone, readable by no surface and clearable by no
    /// reset. <c>ResetAsync</c> takes them in the same transaction for that reason, and a table added
    /// here whose rows something else records has to be looked at the same way.
    /// </remarks>
    private static readonly IReadOnlyList<string> SagaTables =
    [
        "saga_memory_embeddings",
        "saga_memory_embeddings_vec",
        "saga_memories",
        "saga_extraction_watermarks",
    ];

    private static readonly IReadOnlyList<string> SessionAttachmentTables =
    [
        "session_attachment_embeddings_vec",
        "session_attachment_embeddings",
        "session_attachment_chunks",
        "session_attachment_index_state",
    ];

    /// <summary>
    /// The Tapestry's trees are derived data, so this scope drops exactly the <c>tapestry_*</c> tables
    /// and nothing else — the leaf corpora it was woven from stay indexed and the next background
    /// sweep rebuilds every tree from them.
    /// </summary>
    private static readonly IReadOnlyList<string> TapestryTables =
    [
        "tapestry_node_embeddings_vec",
        "tapestry_node_embeddings",
        "tapestry_nodes",
        "tapestry_generations",
    ];

    /// <summary>
    /// Dispatches every labelled artifact this scope would otherwise truncate, in bounded pages.
    /// </summary>
    /// <remarks>
    /// Keyset paging on the label identity rather than an offset walk: purged rows are gone, and an
    /// offset would skip whatever slid into their place. The scan reads <c>artifact_sensitivity</c>
    /// directly because that table <em>is</em> the answer to "which of these rows is protected" — asking
    /// the embedding tables instead would be a second opinion about it.
    ///
    /// <para>A composition with no purger, or an installation whose label table is absent, purges
    /// nothing and leaves the reset exactly as it was.</para>
    /// </remarks>
    private async Task<Result<CovenantSensitivePurgeOutcome>> PurgeLabeledScopeAsync(
        EmbeddingsResetScope scope,
        CancellationToken cancellationToken)
    {

        List<CovenantSensitivePurgeResult> results = [];

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        if (purger is null)
        {

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome(results, progress));

        }

        List<SensitiveArtifactKind> kinds = [];

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Entry)
        {

            kinds.Add(SensitiveArtifactKind.Embedding);

        }

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Saga)
        {

            kinds.Add(SensitiveArtifactKind.Saga);

        }

        foreach (SensitiveArtifactKind kind in kinds)
        {

            Result<CovenantSensitivePurgeOutcome> purgedKind = await PurgeLabeledKindAsync(
                kind,
                cancellationToken).ConfigureAwait(false);

            if (purgedKind.IsFailure)
            {

                return purgedKind.Error;

            }

            results.AddRange(purgedKind.Value.Results);

            progress = progress.Add(purgedKind.Value.Progress);

            if (purgedKind.Value.IsBlocked)
            {

                break;

            }

        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, progress));

    }

    private async Task<Result<CovenantSensitivePurgeOutcome>> PurgeLabeledKindAsync(
        SensitiveArtifactKind kind,
        CancellationToken cancellationToken)
    {

        const int PageSize = 128;

        List<CovenantSensitivePurgeResult> results = [];

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        string cursor = string.Empty;

        while (true)
        {

            List<(Guid ArtifactId, string LabelId)> page = [];

            {

                if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
                {
                    throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");

                }

                Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                    .AcquireScopedAsync(
                        scopedConnection,
                        CovenantSqliteConnectionMode.ReadOnly,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (acquired.IsFailure)
                {

                    return acquired.Error;

                }

                await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

                DbConnection connection = lease.Connection;

                await using (DbCommand command = connection.CreateCommand())
                {

                    command.CommandText = """
                        SELECT ArtifactId, LabelId
                        FROM artifact_sensitivity
                        WHERE ArtifactKindCode = $kind AND LabelId > $after
                        ORDER BY LabelId
                        LIMIT $limit;
                        """;

                    DbParameter kindParameter = command.CreateParameter();

                    kindParameter.ParameterName = "$kind";

                    kindParameter.Value = (int)kind;

                    command.Parameters.Add(kindParameter);

                    DbParameter afterParameter = command.CreateParameter();

                    afterParameter.ParameterName = "$after";

                    afterParameter.Value = cursor;

                    command.Parameters.Add(afterParameter);

                    DbParameter limitParameter = command.CreateParameter();

                    limitParameter.ParameterName = "$limit";

                    limitParameter.Value = PageSize;

                    command.Parameters.Add(limitParameter);

                    try
                    {

                        await using DbDataReader reader = await command
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);

                        await GrimoireScopedConsumerTestSeam
                            .PauseAsync("EmbeddingsResetService.PurgeLabeledKindAsync", cancellationToken)
                            .ConfigureAwait(false);

                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {

                            if (Guid.TryParse(reader.GetString(0), out Guid artifactId))
                            {

                                page.Add((artifactId, reader.GetString(1)));

                            }

                        }

                    }
                    catch (SqliteException)
                    {

                        // No label table on this installation: there is nothing protected to dispatch and
                        // the ordinary reset below is the whole operation.
                        return Result<CovenantSensitivePurgeOutcome>.Success(
                            new CovenantSensitivePurgeOutcome(results, progress));

                    }

                }

            }

            if (page.Count == 0)
            {

                break;

            }

            cursor = page[^1].LabelId;

            Result<CovenantSensitivePurgeOutcome> purged = await purger!
                .PurgeAsync(
                    [.. page.Select(entry => new CovenantSensitivePurgeTarget(kind, entry.ArtifactId))],
                    cancellationToken)
                .ConfigureAwait(false);

            if (purged.IsFailure)
            {

                return purged.Error;

            }

            results.AddRange(purged.Value.Results);

            progress = progress.Add(purged.Value.Progress);

            if (purged.Value.IsBlocked)
            {

                break;

            }

        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, progress));

    }

    public async Task<EmbeddingsResetResult> ResetAsync(
        EmbeddingsResetScope scope,
        CancellationToken cancellationToken = default)
    {

        List<string> targets = [];

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Entry)
        {
            targets.AddRange(EntryTables);
        }

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.WorkspaceFile)
        {
            targets.AddRange(WorkspaceFileTables);
        }

        bool clearsSagaMemories = scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Saga;

        if (clearsSagaMemories)
        {
            targets.AddRange(SagaTables);
        }

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.SessionAttachment)
        {
            targets.AddRange(SessionAttachmentTables);
        }

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Tapestry)
        {
            targets.AddRange(TapestryTables);
        }

        // Dispatched before the set-based truncation, never instead of it. Two of these tables carry
        // labelled rows — Entry embeddings and Saga memories — and a `DELETE FROM entry_embeddings`
        // would remove them without ever examining a label, which is the exact legacy path issue #117
        // exists to close. Everything the purger removes is already gone by the time the truncation
        // runs, so the statements below are unchanged and simply find less to do (§10.20.2).
        Result<CovenantSensitivePurgeOutcome> purged = await PurgeLabeledScopeAsync(
            scope,
            cancellationToken).ConfigureAwait(false);

        if (purged.IsFailure)
        {

            throw new InvalidOperationException(purged.Error.Message);

        }

        if (purged.Value.IsBlocked)
        {

            throw new InvalidOperationException(
                "A protected artifact selected by this embeddings reset could not be erased and was left unchanged.");

        }

        Dictionary<string, int> deleted = [];

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                foreach (string table in targets)
                {

                    int rows = await DeleteFromTableAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);

                    deleted[table] = rows;

                }

                if (clearsSagaMemories)
                {

                    // In this transaction rather than beside it, and ungated for the reason the store's
                    // own delete gives: a claim written while the Annals was enabled has to stay
                    // removable after it is disabled, or turning the feature off strands records no
                    // surface can reach. The order and the predicates come from AnnalsErasurePlan, which
                    // the store delete and the memory reset both read, so this reset cannot disagree
                    // with them about which rows one store's erasure owns.
                    //
                    // The rows are not reported below. What this result counts is the tables the
                    // requested scope names, and the Annals rows go because the memories did rather
                    // than because the scope asked for them.
                    await AnnalsClaimWriter.DeleteClaimsForStoreAsync(
                        connection,
                        transaction,
                        AnnalSubjectStore.Saga,
                        cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken).ConfigureAwait(false);

        return new EmbeddingsResetResult(deleted);

    }

    private async Task<int> DeleteFromTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {

        if (table.EndsWith("_vec", StringComparison.Ordinal)
            && !availability.IsVecAvailable)
        {

            return 0;

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.Transaction = transaction;

        cmd.CommandText = $"""DELETE FROM "{table}" """;

        try
        {

            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && table.EndsWith("_vec", StringComparison.Ordinal))
        {

            // The vec0 table may not exist if sqlite-vec became unavailable after schema creation.
            // Treat as 0 rows deleted and continue with the remaining tables.
            return 0;

        }

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

}

public enum EmbeddingsResetScope
{

    All,

    Entry,

    WorkspaceFile,

    Saga,

    SessionAttachment,

    Tapestry,

}

public sealed record EmbeddingsResetResult(Dictionary<string, int> DeletedRowCounts);
