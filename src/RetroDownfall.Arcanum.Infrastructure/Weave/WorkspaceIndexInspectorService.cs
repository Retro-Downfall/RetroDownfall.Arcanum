using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Phase 7 (RAG / The Weave inspector) — read-only inspector over the existing <c>workspace_file_chunks</c>
/// and <c>workspace_file_embeddings</c> tables. Scoped (depends on the scoped <see cref="ArcanumDbContext"/>).
/// Mirrors the raw-SQL connection pattern of <c>WorkspaceDivinationEndpoints</c>; never triggers indexing
/// and never mutates state. Skipped-file reasons are not persisted anywhere in Arcanum, so
/// <see cref="SkippedFilesNote"/> states that honestly rather than synthesizing causes.
/// </summary>
public sealed class WorkspaceIndexInspectorService : IWorkspaceIndexInspectorService
{

    /// <summary>Hard cap on the returned chunk content preview, in characters (surrogate-safe slice).</summary>
    public const int ContentPreviewChars = 500;

    public const string SkippedFilesNote =
        "Skipped-file reasons are not currently persisted by Arcanum. Files that were too large, " +
        "had no parseable text, failed to chunk/embed, or were skipped for any other reason are logged " +
        "server-side but are not surfaced here.";

    private readonly ArcanumDbContext _db;

    public WorkspaceIndexInspectorService(ArcanumDbContext db)
    {

        _db = db;

    }

    public async Task<WorkspaceIndexStatusDto> GetStatusAsync(
        WorkspaceInfo workspace,
        string vectorMode,
        string vectorDiagnostic,
        bool indexingEnabled,
        CancellationToken cancellationToken)
    {

        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        int totalFiles = 0;

        int totalChunks = 0;

        DateTimeOffset? oldestIndexedAt = null;

        DateTimeOffset? newestIndexedAt = null;

        await using (DbCommand cmd = connection.CreateCommand())
        {

            cmd.CommandText =
                """
                SELECT COUNT(DISTINCT "RelativePath"), COUNT(*), MIN("IndexedAt"), MAX("IndexedAt")
                FROM "workspace_file_chunks"
                WHERE "WorkspacePath" = @workspacePath
                """;

            AddParameter(cmd, "@workspacePath", workspace.Path);

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                totalFiles = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);

                totalChunks = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);

                oldestIndexedAt = ParseIsoOrNull(reader, 2);

                newestIndexedAt = ParseIsoOrNull(reader, 3);

            }

        }

        int? embeddingsDimensions = await ReadStoredDimensionsAsync(connection, workspace.Path, cancellationToken).ConfigureAwait(false);

        return new WorkspaceIndexStatusDto(
            workspace.Id,
            workspace.Name,
            workspace.Path,
            vectorMode,
            vectorDiagnostic,
            indexingEnabled,
            totalFiles,
            totalChunks,
            oldestIndexedAt,
            newestIndexedAt,
            embeddingsDimensions,
            SkippedFilesNote);

    }

    public async Task<WorkspaceFileChunkPage> GetChunksAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {

        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        bool filtered = !string.IsNullOrEmpty(relativePath);

        List<(string ChunkId, string RelativePath, int ChunkIndex, string Content, int CharOffset, int CharLength, string IndexedAtRaw, string FileLastWriteTimeRaw)> rows = [];

        await using (DbCommand cmd = connection.CreateCommand())
        {

            cmd.CommandText =
                """
                SELECT "ChunkId", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "IndexedAt", "FileLastWriteTime"
                FROM "workspace_file_chunks"
                WHERE "WorkspacePath" = @workspacePath
                """;

            AddParameter(cmd, "@workspacePath", workspace.Path);

            if (filtered)
            {

                cmd.CommandText += " AND \"RelativePath\" = @relativePath";

                AddParameter(cmd, "@relativePath", relativePath!);

            }

            cmd.CommandText += " ORDER BY \"RelativePath\", \"ChunkIndex\" LIMIT @limit OFFSET @offset";

            AddParameter(cmd, "@limit", limit);

            AddParameter(cmd, "@offset", offset);

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetString(7)));

            }

        }

        int total = await ReadTotalCountAsync(connection, workspace.Path, filtered ? relativePath : null, cancellationToken).ConfigureAwait(false);

        Dictionary<string, int> totalChunksByPath = await ReadTotalChunksByPathAsync(
            connection,
            workspace.Path,
            rows.Select(static r => r.RelativePath).Distinct(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);

        WorkspaceFileChunkDto[] chunks = rows
            .Select(r => new WorkspaceFileChunkDto(
                r.ChunkId,
                r.RelativePath,
                r.ChunkIndex,
                totalChunksByPath.GetValueOrDefault(r.RelativePath, 1),
                r.Content[..Utf8Truncation.SafeCharSliceLength(r.Content, ContentPreviewChars)],
                r.CharOffset,
                r.CharLength,
                ParseIsoOrMin(r.IndexedAtRaw),
                ParseIsoOrMin(r.FileLastWriteTimeRaw)))
            .ToArray();

        return new WorkspaceFileChunkPage(
            chunks,
            total,
            limit,
            offset,
            (offset + chunks.Length) < total,
            filtered ? relativePath : null);

    }

    private static async Task<int?> ReadStoredDimensionsAsync(
        DbConnection connection,
        string workspacePath,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT DISTINCT e."Dim"
            FROM "workspace_file_embeddings" e
            JOIN "workspace_file_chunks" c ON c."ChunkId" = e."ChunkId"
            WHERE c."WorkspacePath" = @workspacePath
            LIMIT 1
            """;

        AddParameter(cmd, "@workspacePath", workspacePath);

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
        {

            return reader.GetInt32(0);

        }

        return null;

    }

    private static async Task<int> ReadTotalCountAsync(
        DbConnection connection,
        string workspacePath,
        string? relativePath,
        CancellationToken cancellationToken)
    {

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = "SELECT COUNT(*) FROM \"workspace_file_chunks\" WHERE \"WorkspacePath\" = @workspacePath";

        AddParameter(cmd, "@workspacePath", workspacePath);

        if (!string.IsNullOrEmpty(relativePath))
        {

            cmd.CommandText += " AND \"RelativePath\" = @relativePath";

            AddParameter(cmd, "@relativePath", relativePath);

        }

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return scalar is int i ? i : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);

    }

    private static async Task<Dictionary<string, int>> ReadTotalChunksByPathAsync(
        DbConnection connection,
        string workspacePath,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {

        List<string> paths = [.. relativePaths];

        Dictionary<string, int> result = new(StringComparer.Ordinal);

        if (paths.Count == 0)
        {

            return result;

        }

        await using DbCommand cmd = connection.CreateCommand();

        System.Text.StringBuilder sql = new(
            """
            SELECT "RelativePath", COUNT(*)
            FROM "workspace_file_chunks"
            WHERE "WorkspacePath" = @workspacePath AND "RelativePath" IN (
            """);

        AddParameter(cmd, "@workspacePath", workspacePath);

        for (int i = 0; i < paths.Count; i++)
        {

            if (i > 0)
            {

                sql.Append(", ");

            }

            string paramName = $"@path{i.ToString(CultureInfo.InvariantCulture)}";

            sql.Append(paramName);

            AddParameter(cmd, paramName, paths[i]);

        }

        sql.Append(") GROUP BY \"RelativePath\"");

        cmd.CommandText = sql.ToString();

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            result[reader.GetString(0)] = reader.GetInt32(1);

        }

        return result;

    }

    private static DateTimeOffset? ParseIsoOrNull(DbDataReader reader, int ordinal)
    {

        if (reader.IsDBNull(ordinal))
        {

            return null;

        }

        string raw = reader.GetString(ordinal);

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

    }

    private static DateTimeOffset ParseIsoOrMin(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : DateTimeOffset.MinValue;

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

}
