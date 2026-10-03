using System.Data.Common;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>What a vector mirror is in the database in front of the caller.</summary>
internal enum SagaVectorMirrorKind
{
    /// <summary>No table of that name exists, so there is nothing to write or remove.</summary>
    Absent = 1,

    /// <summary>An ordinary table this runtime can read, write, and delete from.</summary>
    PlainTable = 2,

    /// <summary>
    /// A virtual table, which is what a <c>vec0</c> accelerator built. This runtime ships no such
    /// module, so the table cannot be opened, and whatever it holds is unreachable residue.
    /// </summary>
    LegacyVirtualTable = 3,
}

/// <summary>
/// The one rule every writer of a vector mirror follows: classify the mirror from the catalog, delete
/// from a plain one whatever the accelerator flag says, and write only while the accelerator is live.
/// Entry, workspace-file, session-attachment, and Tapestry mirrors follow it through the table-named
/// overloads, which are the same rule over a different table.
/// </summary>
/// <remarks>
/// The mirror holds the embedding itself rather than a pointer to it, so a row left behind is content
/// left behind. Whether the mirror holds rows is a property of the database, not of whether this
/// process loaded an accelerator: a build running without one would otherwise skip a mirror an earlier
/// build filled, and every delete it skipped would be a retirement, correction, or erasure that stopped
/// at the BLOB table. So the classification reads <c>sqlite_master</c> inside the caller's transaction,
/// and the process flag decides only whether a new vector may be written.
///
/// <para>A legacy <c>vec0</c> virtual table is never opened. The shipping runtime has no module to
/// open it with, and a statement against it would fail the caller's whole transaction; skipping it and
/// reporting the kind lets the caller record the residue it could not reach instead of failing an
/// operation the operator asked for.</para>
///
/// <para>Every table name here is a code-owned literal, never input, which is what makes it safe to
/// interpolate into statement text SQLite gives no parameter form for.</para>
/// </remarks>
internal static class SagaVectorMirror
{
    /// <summary>Classifies the Saga mirror, <see cref="SagaStorageKeys.VectorTable"/>.</summary>
    internal static Task<SagaVectorMirrorKind> ClassifyAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken) =>
        ClassifyAsync(connection, transaction, SagaStorageKeys.VectorTable, cancellationToken);

    /// <summary>Classifies any conditionally installed mirror a purge plan names.</summary>
    /// <param name="table">A code-owned table literal, bound rather than interpolated.</param>
    internal static async Task<SagaVectorMirrorKind> ClassifyAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentException.ThrowIfNullOrEmpty(table);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // sqlite_master records a virtual table as an ordinary 'table', so the type alone cannot tell a
        // legacy vec0 mirror from a plain one. Its CREATE text can, and reading that text never opens
        // the table.
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = $table AND type IN ('table', 'view') LIMIT 1;";

        AddParameter(command, "$table", table);

        object? definition = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (definition is null)
        {
            return SagaVectorMirrorKind.Absent;
        }

        return definition is string sql
            && sql.Trim().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)
            ? SagaVectorMirrorKind.LegacyVirtualTable
            : SagaVectorMirrorKind.PlainTable;
    }

    /// <summary>Removes one memory's mirror row when the mirror is a plain table.</summary>
    /// <returns>The rows removed; zero when the mirror is absent or a legacy virtual table.</returns>
    internal static async Task<long> DeleteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string memoryId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(memoryId);

        if (await ClassifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            is not SagaVectorMirrorKind.PlainTable)
        {
            return 0;
        }

        return await DeleteRowAsync(connection, transaction, memoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every Saga mirror row when the mirror is a plain table.</summary>
    /// <returns>The rows removed; zero when the mirror is absent or a legacy virtual table.</returns>
    internal static Task<long> DeleteAllAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken) =>
        DeleteAllAsync(connection, transaction, SagaStorageKeys.VectorTable, cancellationToken);

    /// <summary>Removes every row of any conditionally installed mirror when it is a plain table.</summary>
    /// <param name="table">
    /// A code-owned table literal, interpolated into the statement because SQLite has no parameter form
    /// for a table name.
    /// </param>
    /// <returns>The rows removed; zero when the mirror is absent or a legacy virtual table.</returns>
    internal static async Task<long> DeleteAllAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);

        if (await ClassifyAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false)
            is not SagaVectorMirrorKind.PlainTable)
        {
            return 0;
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"""DELETE FROM "{table}" """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors one memory's current vector while the accelerator is live, and otherwise removes any
    /// mirror row the memory still has.
    /// </summary>
    /// <remarks>
    /// The removal is the half that matters. A memory whose vector just changed, or that was just put
    /// back, and whose mirror row this process cannot rewrite, would otherwise keep a row describing
    /// text it no longer holds, reachable by the next build that loads an accelerator.
    /// </remarks>
    /// <returns>The mirror's kind, so a caller can tell a skipped legacy mirror from a written one.</returns>
    internal static async Task<SagaVectorMirrorKind> UpsertAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string memoryId,
        ReadOnlyMemory<float> vector,
        bool vecAvailable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(memoryId);

        SagaVectorMirrorKind kind = await ClassifyAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (kind is not SagaVectorMirrorKind.PlainTable)
        {
            return kind;
        }

        if (!vecAvailable)
        {
            _ = await DeleteRowAsync(connection, transaction, memoryId, cancellationToken).ConfigureAwait(false);

            return kind;
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT OR REPLACE INTO "saga_memory_embeddings_vec" ("MemoryId", "Embedding")
            VALUES (@id, @embedding)
            """;

        AddParameter(command, "@id", memoryId);

        AddParameter(command, "@embedding", EmbeddingBlobCodec.Encode(vector.Span));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return kind;
    }

    private static async Task<long> DeleteRowAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string memoryId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """DELETE FROM "saga_memory_embeddings_vec" WHERE "MemoryId" = @id""";

        AddParameter(command, "@id", memoryId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}
