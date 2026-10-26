using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>Literal native inspection on the caller's admitted connection; every window precedes matching.</summary>
internal static class NativeMemorySearchReader
{
    internal const int ExaminedRowLimit = 512;

    internal const long ExaminedByteLimit = 2L * 1024 * 1024;

    internal const int StatementLimit = 64;

    internal const int CandidateLimit = 256;

    private sealed record Key(string Column, bool Descending, int Coordinate);

    private sealed record Source(string Table, string Id, string[] Fields, Key[] Keys, string? Owner, string Select, string Join, string? Filter);

    private static readonly Source[] Sources =
    [
        new("Entries", "Id", ["Content"], [new("CreatedAt", true, 0), new("Sequence", true, 1), new("Id", true, 3)], "SessionId", "d.Id,d.SessionId,d.Content,d.Role,COALESCE(s.Title,'')", " INNER JOIN Sessions s ON s.Id=d.SessionId", null),
        new("Sessions", "Id", ["Summary"], [new("UpdatedAt", true, 0), new("Id", true, 3)], "Id", "d.Id,COALESCE(d.Title,''),d.Summary", "", "Summary IS NOT NULL"),
        new("session_attachment_chunks", "ChunkId", ["Content","OriginalFileName","LogicalKey"], [new("IndexedAt", true, 0), new("ChunkIndex", false, 1), new("ChunkId", false, 3)], "SessionId", "d.ChunkId,d.SessionId,d.AttachmentId,d.LogicalKey,d.Version,d.OriginalFileName,d.ChunkIndex,d.Content,d.ContentSha256", "", null),
        new("workspace_file_chunks", "ChunkId", ["Content","RelativePath"], [new("IndexedAt", true, 0), new("RelativePath", false, 2), new("ChunkIndex", false, 1), new("ChunkId", false, 3)], "WorkspacePath", "d.ChunkId,d.WorkspacePath,d.RelativePath,d.ChunkIndex,d.Content,d.StartLine,d.EndLine", "", null),
    ];

    private sealed record Candidate(long RowId, MemorySearchPosition Position, long Bytes);

    internal static async Task<Result<NativeMemorySearchPage>> ReadAsync(
        DbConnection connection, string query, MemorySearchScope scope, Guid? sessionId,
        (string Path, string Label)? workspace, MemorySearchPosition start, int limit, CancellationToken cancellationToken)
    {
        Error unavailable = new(ErrorCodes.MemorySearch.IndexUnavailable,
            "Persisted-memory search requires a healthy, completed Core search projection. Inspect arcanum doctor --only grimoire.schema_transition and allow a compatible host to finish any pending transition.");

        await using (DbCommand readiness = connection.CreateCommand())
        {
            readiness.CommandText = """
                SELECT SchemaVersion>=28
                    AND NOT EXISTS(SELECT 1 FROM grimoire_schema_transitions WHERE TransactionTierCode=0)
                    AND EXISTS(SELECT 1 FROM memory_search_fts_config WHERE k='secure-delete' AND v=1)
                FROM grimoire_feature_schemas WHERE FamilyCode=0 AND TransactionTierCode=0
                """;

            try
            {
                if (await readiness.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long ready || ready != 1)
                    return Result<NativeMemorySearchPage>.Failure(unavailable);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 11 or 26)
            {
                return Result<NativeMemorySearchPage>.Failure(unavailable);
            }
        }

        return Result<NativeMemorySearchPage>.Success(await ReadCoreAsync(connection, query, scope, sessionId,
            workspace, start, limit, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<NativeMemorySearchPage> ReadCoreAsync(
        DbConnection connection, string query, MemorySearchScope scope, Guid? sessionId,
        (string Path, string Label)? workspace, MemorySearchPosition start, int limit, CancellationToken cancellationToken)
    {
        (List<long>? sparse, int seedStatements) = scope is MemorySearchScope.Saga or MemorySearchScope.Lexicon
            ? (null, 0) : await ReadSparseCandidatesAsync(connection, query, cancellationToken).ConfigureAwait(false);
        List<NativeMemorySearchHit> hits = [];
        MemorySearchPosition position = start;
        int examined = 0;
        long bytes = 0;
        long returned = 0;
        int statements = seedStatements + 1;

        for (int phase = start.Phase; phase < Sources.Length; phase++)
        {
            MemorySearchScope currentScope = phase < 2 ? MemorySearchScope.Session : phase == 2 ? MemorySearchScope.Attachments : MemorySearchScope.Workspace;
            if (scope != MemorySearchScope.All && scope != currentScope)
            {
                position = new(phase + 1);
                continue;
            }

            if (phase >= 2 && hits.Count >= limit) return Page(MemorySearchTruncationReason.ResultLimit);
            Source source = Sources[phase];
            if (sparse is { Count: 0 }) { position = new(phase + 1); continue; }
            string? owner = phase == 3 ? workspace?.Path : sessionId is { } id ? phase < 2 ? GrimoireEntitySql.Format(id) : id.ToString("D") : null;
            MemorySearchPosition? after = position.Phase == phase && position.Id is not null ? position : null;
            position = after ?? new(phase);
            if (examined >= ExaminedRowLimit) return Page(MemorySearchTruncationReason.ExaminedRows);
            if (bytes >= ExaminedByteLimit) return Page(MemorySearchTruncationReason.ExaminedBytes);
            List<Candidate> window = [];
            int branchCount = after is null || sparse is not null ? 1 : source.Keys.Length;
            for (int branch = 0; branch < branchCount && window.Count < ExaminedRowLimit - examined; branch++)
            {
                if (++statements > StatementLimit) return Page(MemorySearchTruncationReason.StatementLimit);
                await using DbCommand metadata = connection.CreateCommand();
                string predicate = source.Filter is null ? "1=1" : "d." + source.Filter;
                if (owner is not null) { predicate += " AND d." + source.Owner + "=@owner"; Bind(metadata, "@owner", owner); }
                if (after is not null)
                {
                    IEnumerable<int> ranges = sparse is null ? [source.Keys.Length - 1 - branch] : Enumerable.Range(0, source.Keys.Length);
                    predicate += " AND (" + string.Join(" OR ", ranges.Select(range => "(" + string.Join(" AND ",
                        source.Keys.Take(range + 1).Select((key, index) => "d." + key.Column
                            + (index < range ? "=" : key.Descending ? "<" : ">") + "@key" + index)) + ")")) + ")";
                    for (int key = 0; key < source.Keys.Length; key++) Bind(metadata, "@key" + key, Coordinate(after, source.Keys[key].Coordinate));
                }

                string from = source.Table + " d";
                if (sparse is not null)
                {
                    from = "memory_search_documents p CROSS JOIN " + source.Table + " d ON d." + source.Id + "=p.SourceId";
                    predicate += " AND p.Corpus=@corpus AND p.SearchId IN (" + string.Join(',', sparse.Select((_, index) => "@candidate" + index)) + ")";
                    Bind(metadata, "@corpus", phase);
                    for (int index = 0; index < sparse.Count; index++) Bind(metadata, "@candidate" + index, sparse[index]);
                }

                string time = source.Keys[0].Column;
                string number = source.Keys.Any(k => k.Coordinate == 1) ? "d." + source.Keys.Single(k => k.Coordinate == 1).Column : "0";
                string path = source.Keys.Any(k => k.Coordinate == 2) ? "d." + source.Keys.Single(k => k.Coordinate == 2).Column : "NULL";
                metadata.CommandText = $"SELECT d.rowid,d.{source.Id},d.{time},{number},{path},{string.Join('+', source.Fields.Select(f => $"COALESCE(octet_length(d.{f}),0)"))} FROM {from} WHERE {predicate} ORDER BY {Order(source, "d.")} LIMIT @take";
                Bind(metadata, "@take", ExaminedRowLimit - examined - window.Count);
                await using DbDataReader reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) window.Add(new(reader.GetInt64(0), new(phase, reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(1)), reader.GetInt64(5)));
            }

            if (window.Count == 0) { position = new(phase + 1); continue; }
            List<Candidate> charged = [];
            foreach (Candidate candidate in window)
            {
                if (bytes >= ExaminedByteLimit) break;
                charged.Add(candidate);
                examined++;
                bytes += candidate.Bytes;
            }

            if (++statements > StatementLimit) return Page(MemorySearchTruncationReason.StatementLimit);
            await using DbCommand match = connection.CreateCommand();
            string names = string.Join(',', charged.Select((_, i) => "(@row" + i + ")"));
            for (int i = 0; i < charged.Count; i++) Bind(match, "@row" + i, charged[i].RowId);
            Bind(match, "@query", query);
            string guard = "";
            if (owner is not null)
            {
                guard += " AND d." + source.Owner + "=@owner";
                Bind(match, "@owner", owner);
            }

            if (source.Filter is not null) guard += " AND d." + source.Filter;
            match.CommandText = $"WITH selected(rowid) AS MATERIALIZED (VALUES {names}) SELECT d.rowid,{source.Select} FROM selected c CROSS JOIN {source.Table} d ON d.rowid=c.rowid{source.Join} WHERE ({string.Join(" OR ", source.Fields.Select(f => $"instr(lower(d.{f}),lower(@query))>0"))}){guard} ORDER BY {Order(source, "d.")}";
            Dictionary<long, MemorySearchResultDto> matches = [];
            await using (DbDataReader reader = await match.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) matches.Add(reader.GetInt64(0), ReadHit(reader, phase, workspace?.Label));
            }

            foreach (Candidate candidate in charged)
            {
                if (matches.TryGetValue(candidate.RowId, out MemorySearchResultDto? hit))
                {
                    if (hits.Count >= limit) return Page(MemorySearchTruncationReason.ResultLimit);
                    hits.Add(new(hit, candidate.Position));
                    returned += System.Text.Encoding.UTF8.GetByteCount(hit.Content);
                }
                position = candidate.Position;
                if (returned >= 8L * 1024 * 1024) return Page(MemorySearchTruncationReason.ContentBytes);
            }

            if (bytes >= ExaminedByteLimit) return Page(MemorySearchTruncationReason.ExaminedBytes);
            if (window.Count == ExaminedRowLimit - (examined - charged.Count)) return Page(MemorySearchTruncationReason.ExaminedRows);
            position = new(phase + 1);
        }

        return new([.. hits], new(4), null);

        NativeMemorySearchPage Page(MemorySearchTruncationReason reason) => new([.. hits], position, reason);
    }

    private static async Task<(List<long>? Ids, int Statements)> ReadSparseCandidatesAsync(DbConnection connection, string query, CancellationToken cancellationToken)
    {
        if (query.Contains('\0')) return (null, 0);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT lower(@query)";
        Bind(command, "@query", query);
        string normalized = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        Rune[] runes = normalized.EnumerateRunes().ToArray();
        if (runes.Length < 3) return (null, 1);
        HashSet<string> tried = new(StringComparer.Ordinal);
        int statements = 1;
        foreach (int offset in new[] { 0, Math.Min(runes.Length / 2, runes.Length - 3), runes.Length - 3 })
        {
            string seed = string.Concat(runes.Skip(offset).Take(3).Select(rune => rune.ToString()));
            if (!tried.Add(seed)) continue;
            command.Parameters.Clear();
            command.CommandText = "SELECT rowid FROM memory_search_fts WHERE memory_search_fts MATCH @seed LIMIT @cap";
            Bind(command, "@seed", "\"" + seed.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            Bind(command, "@cap", CandidateLimit + 1);
            List<long> ids = [];
            statements++;
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
            }

            if (ids.Count <= CandidateLimit) return (ids, statements);
        }

        return (null, statements);
    }

    private static object Coordinate(MemorySearchPosition position, int coordinate) => coordinate switch { 0 => position.Time!, 1 => position.Number, 2 => position.Path!, _ => position.Id! };

    private static string Order(Source source, string prefix) => string.Join(',', source.Keys.Select(k => prefix + k.Column + (k.Descending ? " DESC" : " ASC")));

    private static void Bind(DbCommand command, string name, object value) { DbParameter p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }

    private static MemorySearchResultDto ReadHit(DbDataReader r, int phase, string? workspace) => phase switch
    {
        0 => new(MemorySearchScope.Session, $"Session entry ({(MessageRole)r.GetInt32(4)})", r.GetString(3), $"Session {(r.GetString(5).Length == 0 ? r.GetString(2) : r.GetString(5))}, entry {r.GetString(1)}", "Session lifetime; archiving retains it and session purge removes it.", r.GetString(1)),
        1 => new(MemorySearchScope.Session, "Session Summary", r.GetString(3), $"Session {(r.GetString(2).Length == 0 ? r.GetString(1) : r.GetString(2))}, Summary field", "Session lifetime; archiving retains it and session purge removes it.", r.GetString(1)),
        2 => new(MemorySearchScope.Attachments, $"{r.GetString(6)} chunk {r.GetInt32(7)}", r.GetString(8), $"Session {r.GetString(2)}, attachment {r.GetString(3)}, logical key {r.GetString(4)}, version {r.GetInt32(5)}, content hash {r.GetString(9)}", "Derived and rebuildable from bound attachment versions; removed with the attachment or index reset.", r.GetString(1)),
        _ => new(MemorySearchScope.Workspace, $"{r.GetString(3)} lines {r.GetInt32(6)}-{r.GetInt32(7)}", r.GetString(5), $"Workspace {workspace ?? "indexed workspace"}, relative file {r.GetString(3)}, chunk {r.GetInt32(4)}", "Derived and rebuildable from registered workspace files; not session memory.", r.GetString(1)),
    };
}
