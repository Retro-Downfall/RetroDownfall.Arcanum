using System.Data.Common;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>Scoped index discovery followed by retained physical-file verification.</summary>
internal static class GuildWorkspaceMemorySearchReader
{
    internal static async Task<Result<NativeMemorySearchPage>> ReadAsync(DbConnection connection, string query,
        (string Path, string Label) workspace, Guid workspaceId, GuildGrant grant, GuildWorkspaceAuthority physical,
        MemorySearchPosition start, int limit, long byteLimit, IReadOnlySet<string> extensions, List<GuildWorkspaceAuthority.RetainedRead> retained,
        CancellationToken ct)
    {
        string[] scopes = grant.Workspaces.Where(item => item.WorkspaceId == workspaceId)
            .Select(item => item.RelativeRoot).Distinct(StringComparer.Ordinal).ToArray();

        if (scopes.Length == 0) return new Error(ErrorCodes.Guild.Forbidden, "Workspace read authority is unavailable.");

        if (limit <= 0) return new NativeMemorySearchPage([], start.Phase == 3 ? start : new(3), MemorySearchTruncationReason.ResultLimit);

        if (byteLimit <= 0) return new NativeMemorySearchPage([], start.Phase == 3 ? start : new(3), MemorySearchTruncationReason.ContentBytes);

        await using SqliteTransaction snapshot = ((SqliteConnection)connection).BeginTransaction(deferred: true);

        await using DbCommand metadata = connection.CreateCommand();

        metadata.Transaction = snapshot;

        List<string> scopePredicates = [];

        for (int i = 0; i < scopes.Length; i++)
        {
            string scope = scopes[i] is "" or "." ? "" : scopes[i].TrimEnd('/') + "/";

            if (scope.Length == 0) { scopePredicates.Add("1=1"); continue; }

            Bind(metadata, "@scope" + i, scope);

            scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i));
        }

        Bind(metadata, "@root", workspace.Path);

        string after = "";

        if (start.Phase == 3 && start.Id is not null)
        {
            if (start.Time is null || start.Path is null) return new Error(ErrorCodes.Validation.InvalidBody, "The workspace search cursor is invalid.");

            after = " AND (IndexedAt<@time OR (IndexedAt=@time AND (RelativePath>@path OR (RelativePath=@path AND (ChunkIndex>@number OR (ChunkIndex=@number AND ChunkId>@id))))))";

            Bind(metadata, "@time", start.Time);
            Bind(metadata, "@path", start.Path);
            Bind(metadata, "@number", start.Number);
            Bind(metadata, "@id", start.Id);
        }

        metadata.CommandText = "SELECT rowid,IndexedAt,RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),coalesce(octet_length(IndexedAt),0)+coalesce(octet_length(RelativePath),0)+coalesce(octet_length(ChunkId),0) FROM workspace_file_chunks WHERE WorkspacePath=@root AND ("
            + string.Join(" OR ", scopePredicates) + ")" + after
            + " ORDER BY IndexedAt DESC,RelativePath ASC,ChunkIndex ASC,ChunkId ASC LIMIT 513";

        List<(long Row, MemorySearchPosition Position, long Offset, long Length, long Bytes)> candidates = [];

        long metadataBytes = 0;

        bool metadataTruncated = false;

        await using (DbDataReader reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long size = reader.GetInt64(8);

                if (size > NativeMemorySearchReader.ExaminedByteLimit - metadataBytes)
                {
                    if (candidates.Count == 0)
                        return new Error(ErrorCodes.MemorySearch.IndexUnavailable, "Workspace search metadata exceeds its bounded window.");

                    metadataTruncated = true;

                    break;
                }

                metadataBytes += size;

                MemorySearchPosition position = new(3, reader.GetString(1), reader.GetInt64(3), reader.GetString(2), reader.GetString(4));

                candidates.Add((reader.GetInt64(0), position, reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
            }
        }

        List<NativeMemorySearchHit> hits = [];
        MemorySearchPosition cursor = start.Phase == 3 ? start : new(3);
        long examined = 0;
        long returned = 0;
        int statements = 1;
        int rows = 0;

        foreach (var candidate in candidates)
        {
            if (rows >= NativeMemorySearchReader.ExaminedRowLimit) return Page(MemorySearchTruncationReason.ExaminedRows);
            if (examined >= NativeMemorySearchReader.ExaminedByteLimit) return Page(MemorySearchTruncationReason.ExaminedBytes);
            if (++statements > NativeMemorySearchReader.StatementLimit) return Page(MemorySearchTruncationReason.StatementLimit);

            rows++;
            examined += candidate.Bytes;
            MemorySearchPosition previous = cursor;
            cursor = candidate.Position;

            if (candidate.Offset < 0 || candidate.Length < 0 || candidate.Length > NativeMemorySearchReader.ExaminedByteLimit
                || candidate.Bytes < 0 || candidate.Bytes > NativeMemorySearchReader.ExaminedByteLimit) continue;

            await using DbCommand hydrate = connection.CreateCommand();
            hydrate.Transaction = snapshot;
            hydrate.CommandText = "SELECT StartLine,EndLine FROM workspace_file_chunks WHERE rowid=@row AND WorkspacePath=@root AND RelativePath=@path AND IndexedAt=@time AND ChunkIndex=@number AND ChunkId=@id AND octet_length(Content)=@bytes AND (arcanum_ordinal_contains(Content,@query) OR arcanum_ordinal_contains(RelativePath,@query))";
            Bind(hydrate, "@root", workspace.Path);
            Bind(hydrate, "@path", candidate.Position.Path!);
            Bind(hydrate, "@time", candidate.Position.Time!);
            Bind(hydrate, "@number", candidate.Position.Number);
            Bind(hydrate, "@id", candidate.Position.Id!);
            Bind(hydrate, "@bytes", candidate.Bytes);
            Bind(hydrate, "@row", candidate.Row);
            Bind(hydrate, "@query", query);

            long first;
            long last;
            await using (DbDataReader row = await hydrate.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(ct).ConfigureAwait(false)) continue;
                first = row.GetInt64(0);
                last = row.GetInt64(1);
            }

            Result<GuildWorkspaceAuthority.RetainedRead> opened = await physical.OpenRetainedReadAsync(grant, workspaceId, candidate.Position.Path!, ct).ConfigureAwait(false);

            if (opened.IsFailure) continue;

            GuildWorkspaceAuthority.RetainedRead file = opened.Value;
            bool accepted = false;
            try
            {
                if (!file.IsIndexEligible(extensions)) continue;

                using StreamReader text = new(file.Stream, leaveOpen: true);
                char[] buffer = new char[4096];
                long skip = candidate.Offset;
                while (skip > 0)
                {
                    int n = await text.ReadBlockAsync(buffer.AsMemory(0, (int)Math.Min(skip, buffer.Length)), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    skip -= n;
                }

                if (skip != 0) continue;
                using SqliteBlob blob = new((SqliteConnection)connection, "workspace_file_chunks", "Content", candidate.Row, readOnly: true);

                if (blob.Length != candidate.Bytes) continue;

                using StreamReader indexed = new(blob, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);

                char[] expected = new char[4096];

                StringBuilder beginning = new(1024);

                long compared = 0;

                bool matches = true;

                using IncrementalHash contentDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                while (true)
                {
                    int n = await indexed.ReadBlockAsync(expected.AsMemory(), ct).ConfigureAwait(false);

                    if (n == 0) break;

                    int actual = await text.ReadBlockAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);

                    if (actual != n || !expected.AsSpan(0, n).SequenceEqual(buffer.AsSpan(0, n))) { matches = false; break; }

                    compared += n;

                    contentDigest.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan(0, n)));

                    if (beginning.Length < 1025) beginning.Append(expected, 0, Math.Min(n, 1025 - beginning.Length));
                }

                if (!matches || compared != candidate.Length) continue;

                file.RecordContentEvidence(candidate.Offset, candidate.Length, contentDigest.GetHashAndReset());

                if ((await file.ValidateAsync(ct).ConfigureAwait(false)).IsFailure || !file.IsIndexEligible(extensions)) continue;

                string excerpt = Clip(beginning.ToString(), 1024, compared > beginning.Length);
                int size = Encoding.UTF8.GetByteCount(excerpt);
                if (returned + size > byteLimit) { cursor = previous; return Page(MemorySearchTruncationReason.ContentBytes); }
                if (hits.Count >= limit) { cursor = previous; return Page(MemorySearchTruncationReason.ResultLimit); }

                hits.Add(new(new(MemorySearchScope.Workspace, $"{candidate.Position.Path} lines {first}-{last}", excerpt,
                    $"Workspace {workspace.Label}, relative file {candidate.Position.Path}, chunk {candidate.Position.Number}",
                    "Derived and rebuildable from registered workspace files; not session memory.", candidate.Position.Id!), candidate.Position));
                returned += size;
                retained.Add(file);
                accepted = true;
            }
            catch (Exception error) when (error is IOException or DecoderFallbackException)
            {
                // A vanished, unreadable or malformed source cannot disclose cached index text.
            }
            finally { if (!accepted) file.Dispose(); }
        }

        if (metadataTruncated) return Page(MemorySearchTruncationReason.ExaminedBytes);

        return new NativeMemorySearchPage([.. hits], new(4), null);

        NativeMemorySearchPage Page(MemorySearchTruncationReason reason) => new([.. hits], cursor, reason);
    }

    internal static string ScopePrefixPredicate(bool windows, int index) => windows
        ? "arcanum_ordinal_contains(substr(RelativePath,1,length(@scope" + index + ")),@scope" + index + ")"
        : "substr(RelativePath,1,length(@scope" + index + "))=@scope" + index + " COLLATE BINARY";

    private static string Clip(string text, int bytes, bool hasMore)
    {
        const string marker = " [TRUNCATED excerpt; inspect the source for full content]";

        if (!hasMore && Encoding.UTF8.GetByteCount(text) <= bytes) return text;

        int end = 0;
        int used = 0;
        int available = bytes - Encoding.UTF8.GetByteCount(marker);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > available) break;
            used += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }

        return text[..Math.Min(end, text.Length)] + marker;
    }

    private static void Bind(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
