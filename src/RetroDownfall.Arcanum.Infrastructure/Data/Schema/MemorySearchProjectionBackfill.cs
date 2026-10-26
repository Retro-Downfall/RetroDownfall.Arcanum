using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>Journaled, transaction-bound projection of existing native literal-search rows.</summary>
internal sealed class MemorySearchProjectionBackfill : IGrimoireSchemaBackfill
{
    public string Name => "memory-search-literal-projection";

    public int MaxRowsPerBatch => 200;

    private static readonly (string Table, string Id, string Insert)[] Sources =
    [
        ("Entries","Id","INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 0,s.Id,s.SessionId,s.CreatedAt,s.Sequence,'','','',lower(s.Content),lower(''),lower('') FROM Entries s WHERE s.rowid IN ({rows});"),
        ("Sessions","Id","INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 1,s.Id,s.Id,s.UpdatedAt,0,'','','',lower(s.Summary),lower(''),lower('') FROM Sessions s WHERE s.rowid IN ({rows}) AND s.Summary IS NOT NULL;"),
        ("session_attachment_chunks","ChunkId","INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 2,s.ChunkId,s.SessionId,s.IndexedAt,s.ChunkIndex,'',s.AttachmentId,s.GenerationId,lower(s.Content),lower(s.OriginalFileName),lower(s.LogicalKey) FROM session_attachment_chunks s WHERE s.rowid IN ({rows});"),
        ("workspace_file_chunks","ChunkId","INSERT INTO memory_search_documents(Corpus, SourceId, Owner, SortAt, SortNumber, SortPath, ConflictOwner, ConflictGeneration, Content, AuxiliaryOne, AuxiliaryTwo) SELECT 3,s.ChunkId,s.WorkspacePath,s.IndexedAt,s.ChunkIndex,s.RelativePath,'','',lower(s.Content),lower(s.RelativePath),lower('') FROM workspace_file_chunks s WHERE s.rowid IN ({rows});"),
    ];

    public async Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(SqliteConnection connection, SqliteTransaction transaction, string? cursor, CancellationToken cancellationToken)
    {
        int phase = 0;
        long? after = null;

        if (cursor is not null)
        {
            string[] parts = cursor.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out phase) || phase is < 0 or > 3
                || (parts[1].Length != 0 && !long.TryParse(parts[1], CultureInfo.InvariantCulture, out _))) throw new InvalidDataException("Invalid memory-search backfill position.");
            after = parts[1].Length == 0 ? null : long.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        (string table, string id, string insert) = Sources[phase];
        List<long> ids = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT rowid FROM {table} " + (after is null ? "" : "WHERE rowid>@after ") + "ORDER BY rowid LIMIT @limit";
        _ = command.Parameters.AddWithValue("@after", after ?? 0);
        _ = command.Parameters.AddWithValue("@limit", MaxRowsPerBatch);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
        }

        if (ids.Count != 0)
        {
            string rows = string.Join(',', ids.Select((_, index) => "@row" + index));
            for (int index = 0; index < ids.Count; index++) _ = command.Parameters.AddWithValue("@row" + index, ids[index]);
            command.CommandText = $"DELETE FROM memory_search_documents WHERE Corpus={phase} AND SourceId IN (SELECT {id} FROM {table} WHERE rowid IN ({rows})); " + insert.Replace("{rows}", rows, StringComparison.Ordinal);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        bool drained = ids.Count < MaxRowsPerBatch;
        return new GrimoireSchemaBackfillBatch(drained ? phase == 3 ? null : (phase + 1).ToString(CultureInfo.InvariantCulture) + ":"
            : phase.ToString(CultureInfo.InvariantCulture) + ":" + ids[^1].ToString(CultureInfo.InvariantCulture), ids.Count, drained && phase == 3);
    }
}
