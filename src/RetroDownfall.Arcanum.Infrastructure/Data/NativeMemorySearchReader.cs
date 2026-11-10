using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
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
        (string Path, string Label)? workspace, MemorySearchPosition start, int limit, CancellationToken cancellationToken, ArcanumSettings? settings = null, bool modelDisclosure = false, int contentByteLimit = 65536, long pageByteLimit = 8L * 1024 * 1024)
    {
        if (GuildMemoryAccess.IsGuildInvocation && scope is MemorySearchScope.Workspace or MemorySearchScope.All)
        {
            return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Mixed or workspace search requires separate host-bound source admissions.");
        }

        modelDisclosure |= GuildMemoryAccess.IsGuildInvocation;

        Result availability = await CheckAvailabilityAsync(connection, cancellationToken).ConfigureAwait(false);

        if (availability.IsFailure) return availability.Error;

        try
        {
            return Result<NativeMemorySearchPage>.Success(await ReadCoreAsync(connection, query, scope, sessionId,
                workspace, start, limit, cancellationToken, settings, modelDisclosure, contentByteLimit, pageByteLimit).ConfigureAwait(false));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1
            && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return SearchUnavailable();
        }
    }

    private static Error SearchUnavailable() => new(ErrorCodes.MemorySearch.IndexUnavailable,
        "Persisted-memory search requires a healthy, completed Core search projection. Inspect arcanum doctor --only grimoire.schema_transition and allow a compatible host to finish any pending transition.");

    internal static async Task<Result> CheckAvailabilityAsync(DbConnection connection, CancellationToken cancellationToken)
    {
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
                    return SearchUnavailable();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 1 or 11 or 26)
            {
                return SearchUnavailable();
            }
        }

        return Result.Success();
    }

    private static async Task<NativeMemorySearchPage> ReadCoreAsync(
        DbConnection connection, string query, MemorySearchScope scope, Guid? sessionId,
        (string Path, string Label)? workspace, MemorySearchPosition start, int limit, CancellationToken cancellationToken,
        ArcanumSettings? settings, bool modelDisclosure, int contentByteLimit, long pageByteLimit)
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
                // Scope and sensitivity are metadata predicates. Full-content digest verification
                // waits until the candidate window has been charged to the examined-byte budget.
                if (modelDisclosure) predicate += " AND (" + SourcePolicy(metadata, phase, settings, verifySummaryDigest: false) + ")";
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

            // Establish only matching identities first. No returned content is selected until its
            // result slot and byte allocation have both been admitted below.
            if (++statements > StatementLimit) return Page(MemorySearchTruncationReason.StatementLimit);

            HashSet<long> matched = [];

            await using (DbCommand match = CreateMatchCommand(connection, source, charged, query, owner,
                phase, modelDisclosure, settings, contentByteLimit, hydrate: false, Math.Max(0, limit - hits.Count) + 1))
            {
                await using DbDataReader reader = await match.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) matched.Add(reader.GetInt64(0));
            }

            List<Candidate> admitted = [];

            long reservedBytes = returned;

            MemorySearchTruncationReason? omitted = null;

            foreach (Candidate candidate in charged)
            {
                if (!matched.Contains(candidate.RowId)) continue;

                if (hits.Count + admitted.Count >= limit)
                {
                    omitted = MemorySearchTruncationReason.ResultLimit;

                    break;
                }

                long candidateBytes = modelDisclosure ? Math.Min(candidate.Bytes, contentByteLimit) : candidate.Bytes;

                if (reservedBytes >= pageByteLimit
                    || modelDisclosure && reservedBytes + candidateBytes > pageByteLimit)
                {
                    omitted = MemorySearchTruncationReason.ContentBytes;

                    break;
                }

                admitted.Add(candidate);

                reservedBytes += candidateBytes;
            }

            Dictionary<long, MemorySearchResultDto> matches = [];

            if (admitted.Count > 0)
            {
                if (++statements > StatementLimit) return Page(MemorySearchTruncationReason.StatementLimit);

                await using DbCommand hydration = CreateMatchCommand(connection, source, admitted, query, owner,
                    phase, modelDisclosure, settings, contentByteLimit, hydrate: true, 0);

                await using DbDataReader reader = await hydration.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    MemorySearchResultDto hydrated = ReadHit(reader, phase, workspace?.Label, modelDisclosure);

                    await GrimoireScopedConsumerTestSeam.PauseAsync(HydrationCheckpoint(connection),
                        GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, Encoding.UTF8.GetByteCount(hydrated.Content), cancellationToken).ConfigureAwait(false);

                    matches.Add(reader.GetInt64(0), hydrated);
                }
            }

            HashSet<long> admittedIds = [.. admitted.Select(candidate => candidate.RowId)];

            foreach (Candidate candidate in charged)
            {
                if (matched.Contains(candidate.RowId) && !admittedIds.Contains(candidate.RowId))
                    return Page(omitted ?? MemorySearchTruncationReason.ResultLimit);

                if (matches.TryGetValue(candidate.RowId, out MemorySearchResultDto? hit))
                {
                    hits.Add(new(hit, candidate.Position));

                    returned += Encoding.UTF8.GetByteCount(hit.Content);
                }

                position = candidate.Position;

                if (returned >= pageByteLimit) return Page(MemorySearchTruncationReason.ContentBytes);
            }

            if (bytes >= ExaminedByteLimit) return Page(MemorySearchTruncationReason.ExaminedBytes);
            if (window.Count == ExaminedRowLimit - (examined - charged.Count)) return Page(MemorySearchTruncationReason.ExaminedRows);
            position = new(phase + 1);
        }

        return new([.. hits], new(4), null);

        NativeMemorySearchPage Page(MemorySearchTruncationReason reason) => new([.. hits], position, reason);
    }

    private static DbCommand CreateMatchCommand(DbConnection connection, Source source, IReadOnlyList<Candidate> candidates,
        string query, string? owner, int phase, bool modelDisclosure, ArcanumSettings? settings,
        int contentByteLimit, bool hydrate, int matchingTake)
    {
        DbCommand command = connection.CreateCommand();

        string names = string.Join(',', candidates.Select((_, index) => "(@row" + index + ")"));

        for (int index = 0; index < candidates.Count; index++) Bind(command, "@row" + index, candidates[index].RowId);

        Bind(command, "@query", query);

        string guard = "";

        if (owner is not null)
        {
            guard += " AND d." + source.Owner + "=@owner";

            Bind(command, "@owner", owner);
        }

        if (source.Filter is not null) guard += " AND d." + source.Filter;

        if (modelDisclosure) guard += " AND (" + SourcePolicy(command, phase, settings) + ")";

        string select = source.Select;

        if (hydrate && modelDisclosure)
        {
            select = select.Replace("COALESCE(s.Title,'')", "''", StringComparison.Ordinal)
                .Replace("COALESCE(d.Title,'')", "''", StringComparison.Ordinal);

            // Eligibility verifies the retained summary digest before the bounded projection.
            if (phase == 1) select = select.Replace("d.Summary", "CASE WHEN octet_length(d.Summary)>@searchContentBytes THEN substr(d.Summary,1,@searchContentChars)||@searchExcerptMarker ELSE d.Summary END", StringComparison.Ordinal);

            select = select.Replace("d.Content,", "CASE WHEN octet_length(d.Content)>@searchContentBytes THEN substr(d.Content,1,@searchContentChars)||@searchExcerptMarker ELSE d.Content END,", StringComparison.Ordinal);

            Bind(command, "@searchContentBytes", contentByteLimit);

            Bind(command, "@searchContentChars", Math.Max(1, (contentByteLimit - 96) / 4));

            Bind(command, "@searchExcerptMarker", " [TRUNCATED excerpt; inspect the source for full content]");
        }

        string projection = hydrate ? "d.rowid," + select : "d.rowid";

        string limit = hydrate ? "" : " LIMIT @matchingTake";

        if (!hydrate) Bind(command, "@matchingTake", matchingTake);

        command.CommandText = $"WITH selected(rowid) AS MATERIALIZED (VALUES {names}) SELECT {projection} FROM selected c CROSS JOIN {source.Table} d ON d.rowid=c.rowid{source.Join} WHERE ({string.Join(" OR ", source.Fields.Select(field => $"arcanum_ordinal_contains(d.{field},@query)"))}){guard} ORDER BY {Order(source, "d.")}{limit}";

        return command;
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
        // Core28 stores SQLite's ASCII fold. Only printable ASCII seeds are complete supersets
        // of ordinal case-insensitive matches; Unicode-only queries use the bounded seek lane.
        // Search every possible window so mixed text can still use a rare safe interior seed.
        int[] safe = Enumerable.Range(0, runes.Length - 2)
            .Where(offset => runes[offset].Value is >= 32 and <= 126
                && runes[offset + 1].Value is >= 32 and <= 126
                && runes[offset + 2].Value is >= 32 and <= 126).ToArray();
        if (safe.Length == 0) return (null, 1);
        HashSet<string> tried = new(StringComparer.Ordinal);
        int statements = 1;
        foreach (int offset in new[] { safe[0], safe[safe.Length / 2], safe[^1] })
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

    internal static string HydrationCheckpoint(DbConnection connection) =>
        "NativeMemorySearchReader.Hydrate:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(connection).ToString(CultureInfo.InvariantCulture);

    private static object Coordinate(MemorySearchPosition position, int coordinate) => coordinate switch { 0 => position.Time!, 1 => position.Number, 2 => position.Path!, _ => position.Id! };

    private static string Order(Source source, string prefix) => string.Join(',', source.Keys.Select(k => prefix + k.Column + (k.Descending ? " DESC" : " ASC")));

    private static void Bind(DbCommand command, string name, object value) { DbParameter p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }

    private static string SourcePolicy(DbCommand command, int phase, ArcanumSettings? settings, bool verifySummaryDigest = true)
    {
        if (phase == 3)
        {
            if (GuildMemoryAccess.IsGuildInvocation) throw new GuildMemoryAccessException(new(ErrorCodes.Guild.AuthorityUnavailable, "Workspace source authority is unavailable."));

            return "1=1";
        }

        GuildMemoryStore store = phase == 2 ? GuildMemoryStore.Attachment : GuildMemoryStore.Transcript;

        string scope = GuildMemoryAccess.SqlSession(command, store, GuildMemoryOperation.Read, phase == 1 ? "d.Id" : "d.SessionId");

        if (phase == 0) return scope + " AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity search_label WHERE search_label.ArtifactKindCode=1 AND lower(replace(search_label.ArtifactId,'-',''))=lower(replace(d.Id,'-','')))";

        if (phase == 1) return scope + " AND " + SummaryEligible("d")
            + (verifySummaryDigest ? " AND arcanum_derived_content_matches(d.Summary," + SummaryDigestProjection("d") + ")" : "");

        scope += " AND " + SessionAttachmentGuildReadPolicy.Indexed("d");

        if (settings is null || !SessionAttachmentComprehensionPolicy.IsEnabled(settings))
        {
            scope += $" AND {SessionAttachmentDocumentMimeSql.NormalizedExpression("d.MimeType")} NOT IN ({SessionAttachmentDocumentMimeSql.ParameterList})";

            foreach (KeyValuePair<string, string> mime in SessionAttachmentDocumentMimeSql.Parameters) Bind(command, mime.Key, mime.Value);
        }

        return scope;
    }

    private static string SummaryEligible(string session) => $"""
        EXISTS(SELECT 1 FROM session_summary_state search_state
          JOIN session_summary_artifacts search_artifact ON search_artifact.ArtifactId=search_state.CurrentArtifactId
            AND search_artifact.SessionId=search_state.SessionId AND search_artifact.Revision=search_state.Revision
          WHERE search_state.SessionId={session}.Id COLLATE NOCASE AND search_artifact.SensitivityCode=0
            AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity search_label WHERE search_label.ArtifactKindCode=3
              AND lower(replace(search_label.ArtifactId,'-',''))=lower(replace(search_artifact.ArtifactId,'-',''))))
        """;

    private static string SummaryDigestProjection(string session) => $"""
        (SELECT search_artifact.ContentDigest FROM session_summary_state search_state
          JOIN session_summary_artifacts search_artifact ON search_artifact.ArtifactId=search_state.CurrentArtifactId
            AND search_artifact.SessionId=search_state.SessionId AND search_artifact.Revision=search_state.Revision
          WHERE search_state.SessionId={session}.Id COLLATE NOCASE)
        """;

    private static MemorySearchResultDto ReadHit(DbDataReader r, int phase, string? workspace, bool modelDisclosure)
    {
        return phase switch
        {
        0 => new(MemorySearchScope.Session, $"Session entry ({(MessageRole)r.GetInt32(4)})", r.GetString(3), $"Session {(r.GetString(5).Length == 0 ? r.GetString(2) : r.GetString(5))}, entry {r.GetString(1)}", "Session lifetime; archiving retains it and session purge removes it.", r.GetString(1)),
        1 => new(MemorySearchScope.Session, "Session Summary", r.GetString(3), $"Session {(r.GetString(2).Length == 0 ? r.GetString(1) : r.GetString(2))}, Summary field", "Session lifetime; archiving retains it and session purge removes it.", r.GetString(1)),
        2 => new(MemorySearchScope.Attachments, $"{r.GetString(6)} chunk {r.GetInt32(7)}", r.GetString(8), $"Session {r.GetString(2)}, attachment {r.GetString(3)}, logical key {r.GetString(4)}, version {r.GetInt32(5)}, content hash {r.GetString(9)}", "Derived and rebuildable from bound attachment versions; removed with the attachment or index reset.", r.GetString(1)),
        _ => new(MemorySearchScope.Workspace, $"{r.GetString(3)} lines {r.GetInt32(6)}-{r.GetInt32(7)}", r.GetString(5), $"Workspace {workspace ?? "indexed workspace"}, relative file {r.GetString(3)}, chunk {r.GetInt32(4)}", "Derived and rebuildable from registered workspace files; not session memory.", r.GetString(1)),
        };
    }
}
