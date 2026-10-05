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
    IServiceProvider serviceProvider,
    ICovenantSensitiveArtifactPurger? purger = null)
{
    private readonly IGrimoireOrdinaryConnectionFactory _connections =
        serviceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

    /// <summary>
    /// The guard the truncating transaction asks, resolved from the scope the database context is in so
    /// it reads through the same composition every other raw delete does.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, and resolved here for the reason the connection factory is: this
    /// class is public and the guard's transaction forms are not, so the constructor cannot name it. A
    /// composition that does not register it fails when the service is built, not by truncating without
    /// asking.
    /// </remarks>
    private readonly ICovenantLabeledArtifactTransactionGuard _labeledArtifactGuard =
        serviceProvider.GetRequiredService<ICovenantLabeledArtifactTransactionGuard>();

    /// <summary>
    /// The record of failed Tapestry builds, forgotten when this reset drops the trees. Optional because a
    /// composition that never runs the Tapestry has none, and resolved here for the reason the other
    /// collaborators are: this class is public and the record is not.
    /// </summary>
    private readonly TapestryBuildBackoff? _tapestryBackoff =
        serviceProvider.GetService<TapestryBuildBackoff>();

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
    /// The kinds of labelled artifact this scope truncates the rows of.
    /// </summary>
    /// <remarks>
    /// Two of the tables this service clears carry labelled rows — Entry embeddings and Saga memories —
    /// and every other table it clears is derived data no label names. The purge walk dispatches these
    /// kinds before the truncation, and the truncating transaction asks the guard about the same ones, so
    /// the two cannot disagree about which labels this reset is answerable for.
    /// </remarks>
    private static List<SensitiveArtifactKind> LabeledKinds(EmbeddingsResetScope scope)
    {
        List<SensitiveArtifactKind> kinds = [];

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Entry)
        {
            kinds.Add(SensitiveArtifactKind.Embedding);
        }

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Saga)
        {
            kinds.Add(SensitiveArtifactKind.Saga);
        }

        return kinds;
    }

    /// <summary>
    /// Dispatches every labelled artifact this scope would otherwise truncate, in bounded pages.
    /// </summary>
    /// <remarks>
    /// Keyset paging on the label identity rather than an offset walk: purged rows are gone, and an
    /// offset would skip whatever slid into their place. The scan reads <c>artifact_sensitivity</c>
    /// directly because that table <em>is</em> the answer to "which of these rows is protected" — asking
    /// the embedding tables instead would be a second opinion about it.
    ///
    /// <para>A composition with no purger purges nothing and leaves the reset exactly as it was. A label
    /// table that cannot be read stops the reset instead, with <c>Covenant.Unavailable</c>: the table is a
    /// Core object at every schema version, so a scan that fails is a Grimoire whose protection cannot be
    /// checked, and the set-based truncation that follows would remove rows nothing had been asked
    /// about. A row the scan read but could not parse is the same condition and gets the same answer,
    /// because the artifact column has no format check and a label that cannot be dispatched is one the
    /// truncation would remove unexamined. The walk's position is the last label identity read and it ends
    /// only on a page that read no rows, so no row is stepped over without being either dispatched or
    /// refused.</para>
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

        foreach (SensitiveArtifactKind kind in LabeledKinds(scope))
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

            // Counted apart from the page: a row that was read is a row the walk has examined, whether or
            // not it parsed, and the walk's position and its end are decided by what was read.
            int rowsRead = 0;

            string lastLabelId = cursor;

            bool unreadableRow = false;

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

                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            rowsRead++;

                            lastLabelId = reader.GetString(1);

                            if (Guid.TryParse(reader.GetString(0), out Guid artifactId))
                            {
                                page.Add((artifactId, lastLabelId));
                            }
                            else
                            {
                                unreadableRow = true;
                            }
                        }

                        await GrimoireScopedConsumerTestSeam
                            .PauseAsync(
                                "EmbeddingsResetService.PurgeLabeledKindAsync",
                                GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
                                page.Count,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SqliteException)
                    {
                        // Not "no label table, so nothing is protected": the table is a Core object at every
                        // schema version, so this is a Grimoire whose protection cannot be checked. The
                        // reset stops here, before the truncation that would remove whatever it never
                        // examined. Artifacts an earlier kind's pages already purged stay purged, so the
                        // message claims no more than that, and it names no artifact and carries no
                        // provider detail.
                        return Result<CovenantSensitivePurgeOutcome>.Failure(
                            new Error(
                                ErrorCodes.Covenant.Unavailable,
                                "The sensitivity labels could not be read, so this embeddings reset was "
                                    + "refused before its truncation ran."));
                    }
                }
            }

            if (unreadableRow)
            {
                // A label this walk cannot parse is a label it cannot dispatch, and dropping it let the
                // truncation that follows remove the artifact it names. The column has no format check, so
                // this is corruption or tampering, and the Grimoire's protection cannot be shown: the same
                // condition, and the same answer, as a table that cannot be read. Refused before this page
                // is dispatched, so nothing is purged on the strength of a page that was only partly
                // understood; pages before it stay purged, and the message names no artifact.
                return Result<CovenantSensitivePurgeOutcome>.Failure(
                    new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "A sensitivity label could not be read, so this embeddings reset was refused "
                            + "before its truncation ran."));
            }

            if (rowsRead == 0)
            {
                break;
            }

            cursor = lastLabelId;

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
            // Typed so the route answers the walk's own refusal, not a blanket "erase it by hand": an
            // unreadable label table is the Grimoire's condition to repair, and a stale label is a retry.
            throw new LabeledArtifactRefusalException(purged.Error);
        }

        if (purged.Value.IsBlocked)
        {
            // A walk that failed after it had already erased an item comes back blocked rather than as a
            // failure, so its erased items are not lost, under the blocker the failure's own code names.
            // Storage that stopped answering (or a cancellation, or an unexpected exception) is
            // StorageUnavailable: still the Grimoire's condition to repair, answered as the failure arm
            // above answers it. Any other blocker is the refusal below, as it is for an item the kernel
            // itself blocked.
            if (purged.Value.Results.Any(static result =>
                    result.Disposition is CovenantSensitivePurgeDisposition.Blocked
                    && result.Blocker is CovenantErasureBlocker.StorageUnavailable))
            {
                throw new LabeledArtifactRefusalException(new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The sensitivity purge could not finish, so this embeddings reset was stopped before any table was truncated."));
            }

            throw new InvalidOperationException(
                "A protected artifact selected by this embeddings reset could not be erased and was left unchanged.");
        }

        Dictionary<string, int> deleted = [];

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                // Asked here, in the transaction that deletes, for each kind it truncates. The walk above
                // read the label table in an earlier read and dispatched the purger, and the statements
                // below examine no identity at all, so a label committed between the two would be removed
                // with its rows and leave a label naming nothing. The transaction already holds the write
                // lock every label writer needs, so the answer and the truncation are one moment. A
                // refusal throws before the first table is touched and the transaction rolls back.
                foreach (SensitiveArtifactKind kind in LabeledKinds(scope))
                {
                    Result none = await _labeledArtifactGuard
                        .EnsureNoneLabeledAsync(kind, connection, transaction, cancellationToken)
                        .ConfigureAwait(false);

                    if (none.IsFailure)
                    {
                        throw new LabeledArtifactRefusalException(none.Error);
                    }
                }

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

        // After the commit, because the trees are only gone once it has: the rebuild the operator asked for
        // must not be told to wait out a failure of a tree that no longer exists.
        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Tapestry)
        {
            _tapestryBackoff?.Clear();
        }

        return new EmbeddingsResetResult(deleted);
    }

    private static async Task<int> DeleteFromTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        // Every vector mirror follows the rule every Saga write follows, read from the catalog rather
        // than the process flag: a plain mirror an earlier build filled is emptied whatever the flag
        // says, because its rows hold the embeddings of the content this reset removes, and a legacy
        // vec0 mirror this runtime cannot open is skipped rather than failing the reset. A mirror that
        // is not there counts zero rows.
        if (table.EndsWith("_vec", StringComparison.Ordinal))
        {
            return checked((int)await SagaVectorMirror
                .DeleteAllAsync(connection, transaction, table, cancellationToken)
                .ConfigureAwait(false));
        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.Transaction = transaction;

        cmd.CommandText = $"""DELETE FROM "{table}" """;

        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
