using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Data;
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
    WeaveIndexAvailability availability)
{

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

        if (scope is EmbeddingsResetScope.All or EmbeddingsResetScope.Saga)
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

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
