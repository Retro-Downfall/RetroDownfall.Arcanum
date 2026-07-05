using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// RAG Phase 3 — indexes workspace files into The Weave for semantic codebase retrieval. Maintains a
/// thread-safe set of "known" workspace paths (populated by <see cref="RegisterWorkspace"/>, called by
/// <c>WizardIntelligenceProvider</c> on every inference turn) and re-indexes each of them on a
/// background interval, plus supports an immediate on-demand re-index via <see cref="IndexNowAsync"/>
/// (used by the manual <c>POST /api/workspaces/{id}/files/index</c> endpoint).
///
/// Idles (1s poll of <c>Arcanum:Embeddings:Enabled</c> / <c>Arcanum:Embeddings:CodebaseRetrievalEnabled</c>)
/// when either flag is false — the default — so this is a no-op on the hot path until an operator opts
/// in. Same idle-when-disabled pattern as <see cref="EntryWeavingService"/> and
/// <see cref="RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthProbeService"/>.
///
/// Change detection: a file is only re-chunked/re-embedded when its <c>LastWriteTimeUtc</c> differs from
/// the <c>FileLastWriteTime</c> recorded on its existing <c>workspace_file_chunks</c> rows — unchanged
/// files are skipped every tick, keeping steady-state re-index cost proportional to the number of edited
/// files, not the size of the workspace.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService workspace indexing scheduler; covered via WorkspaceIndexingServiceTests exercising the indexing logic directly.
internal sealed class WorkspaceIndexingService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IWeaveService weaveService,
    WeaveIndexAvailability weaveIndexAvailability,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkspaceIndexingService> logger) : BackgroundService, IWorkspaceIndexingService
{

    private static readonly HashSet<string> IgnoredDirectorySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        "node_modules",
        ".vs",
        ".nuget",
        "packages",
        "dist",
        "build",
    };

    /// <summary>
    /// Heuristic pre-read filter: UTF-8 never uses more than 4 bytes per character, so a file whose
    /// byte length already exceeds <c>MaxFileSizeChars * 4</c> cannot possibly be within the char limit
    /// and is skipped without reading it. Files that pass this filter still get an authoritative
    /// <c>content.Length &lt;= MaxFileSizeChars</c> check after being read.
    /// </summary>
    private const int MaxUtf8BytesPerChar = 4;

    /// <summary>
    /// Hard cap on total filesystem entries (files + directories combined) visited by a single
    /// indexing tick's walk of a workspace, independent of <c>CodebaseEmbeddingSettings.MaxFilesToIndex</c>
    /// (which only bounds how many changed files get re-embedded). Protects against a pathological or
    /// enormous directory tree (for example a workspace root that turns out to contain — or symlink to
    /// — a much larger tree than intended) making a tick run unboundedly long, on top of the
    /// ignored-directory pruning in <see cref="EnumerateCandidateFiles"/> which already skips the
    /// common cause of a slow walk (<c>node_modules</c>, <c>.git</c>, <c>bin</c>/<c>obj</c>, etc.)
    /// entirely rather than visiting and then discarding their contents.
    /// </summary>
    private const int MaxWalkEntries = 200_000;

    private readonly ConcurrentDictionary<string, byte> _knownWorkspaces = new(StringComparer.Ordinal);

    public void RegisterWorkspace(string workspacePath)
    {

        if (string.IsNullOrWhiteSpace(workspacePath))
        {

            return;

        }

        Result<string> validated = TryValidateWorkspacePath(workspacePath);

        if (validated.IsFailure)
        {

            logger.LogDebug(
                "Workspace registration skipped for {WorkspacePath}: {Reason}",
                workspacePath,
                validated.Error.Message);

            return;

        }

        _knownWorkspaces[validated.Value] = 0;

    }

    public async Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken)
    {

        Result<string> validated = TryValidateWorkspacePath(workspacePath);

        if (validated.IsFailure)
        {

            logger.LogWarning(
                "On-demand workspace re-index rejected for {WorkspacePath}: {Reason}",
                workspacePath,
                validated.Error.Message);

            return;

        }

        string normalized = validated.Value;

        _knownWorkspaces[normalized] = 0;

        try
        {

            EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

            if (!embeddings.Enabled || !embeddings.CodebaseRetrievalEnabled)
            {

                logger.LogDebug("Workspace re-index skipped for {WorkspacePath}: codebase retrieval is disabled.", normalized);

                return;

            }

            await IndexWorkspaceAsync(normalized, embeddings, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "On-demand workspace re-index failed for {WorkspacePath}; continuing.", workspacePath);

        }

    }

    /// <summary>
    /// Applies the same allowlist enforced at campaign-creation time
    /// (<see cref="CampaignPathPolicy.ValidateAndNormalizePath"/>, gated on
    /// <see cref="CampaignsSettings.AllowedRoots"/>) to workspace registration/indexing. Without this,
    /// any caller supplying an arbitrary <c>WorkingDirectory</c> on an inference request could get
    /// unrelated, non-campaign directories (for example a user's home directory or other system paths)
    /// background-indexed and persisted into The Weave, then retrieved via semantic search — bypassing
    /// every other workspace-touching feature's path containment (Spells, Perception, Campaigns).
    /// </summary>
    private Result<string> TryValidateWorkspacePath(string workspacePath) =>
        CampaignPathPolicy.ValidateAndNormalizePath(workspacePath, optionsMonitor.CurrentValue);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        bool wasEnabled = false;

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                EmbeddingSettings embeddings = optionsMonitor.CurrentValue.Embeddings ?? new EmbeddingSettings();

                bool enabled = embeddings.Enabled && embeddings.CodebaseRetrievalEnabled;

                if (!enabled)
                {

                    wasEnabled = false;

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    continue;

                }

                if (!wasEnabled)
                {

                    logger.LogInformation("Workspace Indexing started tracking known workspaces for semantic codebase retrieval.");

                    wasEnabled = true;

                }

                foreach (string workspacePath in _knownWorkspaces.Keys.ToArray())
                {

                    stoppingToken.ThrowIfCancellationRequested();

                    await IndexWorkspaceAsync(workspacePath, embeddings, stoppingToken).ConfigureAwait(false);

                }

                int intervalMinutes = ArcanumSettingClamps.EmbeddingsCodebaseIndexingIntervalMinutes(
                    embeddings.Codebase.IndexingIntervalMinutes);

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "Workspace Indexing tick failed; continuing.");

            }

        }

    }

    internal async Task IndexWorkspaceAsync(string workspacePath, EmbeddingSettings embeddings, CancellationToken cancellationToken)
    {

        if (!weaveService.IsAvailable)
        {

            logger.LogDebug(
                "Workspace indexing tick skipped for {WorkspacePath}: The Weave is unavailable (Provider/Model not configured, or Embeddings disabled).",
                workspacePath);

            return;

        }

        CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

        if (codebase.FileExtensions.Length == 0)
        {

            logger.LogDebug("Workspace indexing tick skipped for {WorkspacePath}: no file extensions configured.", workspacePath);

            return;

        }

        if (!Directory.Exists(workspacePath))
        {

            logger.LogWarning("Workspace indexing skipped: {WorkspacePath} does not exist or is not a directory.", workspacePath);

            return;

        }

        int maxFilesToIndex = ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex(codebase.MaxFilesToIndex);

        int maxFileSizeChars = ArcanumSettingClamps.EmbeddingsCodebaseMaxFileSizeChars(codebase.MaxFileSizeChars);

        HashSet<string> extensions = new(codebase.FileExtensions, StringComparer.OrdinalIgnoreCase);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        int filesIndexed = 0;

        List<string> candidates;

        bool truncated;

        try
        {

            candidates = EnumerateCandidateFiles(workspacePath, extensions, MaxWalkEntries, out truncated);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogWarning(ex, "Workspace indexing could not enumerate {WorkspacePath}.", workspacePath);

            return;

        }

        if (truncated)
        {

            logger.LogWarning(
                "Workspace indexing walk for {WorkspacePath} was truncated at {MaxWalkEntries} entries; some files may not be indexed this tick, and orphaned-chunk cleanup is skipped until a tick completes the full walk.",
                workspacePath,
                MaxWalkEntries);

        }

        // Every candidate that reaches this point still exists at its relative path, regardless of
        // whether this tick actually gets to re-embed it (see the maxFilesToIndex check below) — so
        // this set drives orphaned-chunk cleanup after the loop, not just the re-index decision.
        HashSet<string> seenRelativePaths = new(StringComparer.Ordinal);

        foreach (string fullPath in candidates)
        {

            cancellationToken.ThrowIfCancellationRequested();

            try
            {

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, fullPath, out string? resolvedFinalPath))
                {

                    // Escaping symlink — skipped, never followed.
                    continue;

                }

                string relativePath = Path.GetRelativePath(workspacePath, fullPath);

                seenRelativePaths.Add(relativePath);

                if (filesIndexed >= maxFilesToIndex)
                {

                    // Per-tick re-embed budget exhausted — the file is still "seen" (above) so a
                    // later orphan-cleanup pass never mistakes it for deleted, but re-indexing it is
                    // deferred to a future tick.
                    continue;

                }

                // Captures a stable file identity (dev/ino on Unix, volume+file index on Windows) at
                // the point of the lexical/symlink check above. IndexFileAsync re-checks this
                // identity immediately after opening the file, closing the TOCTOU window between this
                // check and the actual read (see SandboxedFileIo.TryOpenForRead for the same pattern).
                string identityPath = Path.GetFullPath(resolvedFinalPath ?? fullPath);

                if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
                {

                    // Could not resolve a stable identity (e.g. a race with a delete) — skip rather
                    // than risk reading through a path that may have been swapped.
                    continue;

                }

                FileInfo info = new(fullPath);

                if (info.Length > (long)maxFileSizeChars * MaxUtf8BytesPerChar)
                {

                    continue;

                }

                DateTime lastWriteUtc = info.LastWriteTimeUtc;

                DateTime? existingLastWriteUtc = await GetExistingFileLastWriteTimeAsync(
                    db,
                    workspacePath,
                    relativePath,
                    cancellationToken).ConfigureAwait(false);

                if (existingLastWriteUtc is { } existing && existing == lastWriteUtc)
                {

                    // Unchanged since last index — skip without consuming the per-tick file budget.
                    continue;

                }

                bool indexed = await IndexFileAsync(
                    db,
                    workspacePath,
                    relativePath,
                    fullPath,
                    expectedIdentity,
                    lastWriteUtc,
                    maxFileSizeChars,
                    cancellationToken).ConfigureAwait(false);

                if (indexed)
                {

                    filesIndexed++;

                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(ex, "Workspace indexing failed for file {FullPath}; continuing with the next file.", fullPath);

            }

        }

        if (!truncated)
        {

            await DeleteOrphanedChunksAsync(db, workspacePath, seenRelativePaths, cancellationToken).ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Manually walks <paramref name="workspacePath"/> breadth-first (mirroring
    /// <c>PhysicalFileSystemBrowser.ListAsync</c>'s recursive-listing walk), pruning
    /// <see cref="IgnoredDirectorySegments"/> and symlink-escaping subdirectories <b>before</b>
    /// descending into them — unlike <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/>
    /// with <c>RecurseSubdirectories = true</c>, which would still visit every entry under a huge
    /// ignored directory (for example <c>node_modules</c>) only to discard them one by one. Stops and
    /// reports <paramref name="truncated"/> once <paramref name="maxWalkEntries"/> total filesystem
    /// entries (files + directories) have been visited, bounding worst-case tick duration.
    /// </summary>
    private static List<string> EnumerateCandidateFiles(
        string workspacePath,
        HashSet<string> extensions,
        int maxWalkEntries,
        out bool truncated)
    {

        List<string> files = [];

        truncated = false;

        int visited = 0;

        Queue<string> pendingDirectories = new();

        pendingDirectories.Enqueue(workspacePath);

        while (pendingDirectories.Count > 0)
        {

            string directory = pendingDirectories.Dequeue();

            IEnumerable<string> entries;

            try
            {

                entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly);

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {

                // Inaccessible directory (permissions, race with a delete) — skip it and keep walking
                // the rest of the tree, mirroring EnumerationOptions.IgnoreInaccessible's old behavior.
                continue;

            }

            foreach (string fullPath in entries)
            {

                if (visited >= maxWalkEntries)
                {

                    truncated = true;

                    return files;

                }

                visited++;

                FileAttributes attributes;

                try
                {

                    attributes = File.GetAttributes(fullPath);

                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {

                    continue;

                }

                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {

                    continue;

                }

                if ((attributes & FileAttributes.Directory) != 0)
                {

                    string name = Path.GetFileName(fullPath);

                    if (IgnoredDirectorySegments.Contains(name))
                    {

                        // Pruned before recursion — its contents are never visited at all, rather
                        // than being walked and discarded one entry at a time.
                        continue;

                    }

                    if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, fullPath, out _))
                    {

                        // Escaping symlinked directory — never descended into.
                        continue;

                    }

                    pendingDirectories.Enqueue(fullPath);

                }
                else if (extensions.Contains(Path.GetExtension(fullPath)))
                {

                    files.Add(fullPath);

                }

            }

        }

        return files;

    }

    /// <summary>
    /// Deletes chunks for any previously-indexed file under <paramref name="workspacePath"/> whose
    /// relative path is not in <paramref name="seenRelativePaths"/> — i.e. a file that was deleted (or
    /// renamed, or moved outside every configured file extension) since the last successful full walk.
    /// Without this, a removed file's stale chunks/embeddings persist forever and keep surfacing in
    /// semantic search results for content that no longer exists. Only called after a non-truncated
    /// walk (see <see cref="IndexWorkspaceAsync"/>) so a budget-truncated tick never misclassifies an
    /// unvisited-but-still-present file as orphaned.
    /// </summary>
    private async Task DeleteOrphanedChunksAsync(
        ArcanumDbContext db,
        string workspacePath,
        HashSet<string> seenRelativePaths,
        CancellationToken cancellationToken)
    {

        List<string> indexedRelativePaths = await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT DISTINCT "RelativePath"
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                List<string> paths = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    paths.Add(reader.GetString(0));

                }

                return paths;

            },
            cancellationToken).ConfigureAwait(false);

        foreach (string relativePath in indexedRelativePaths)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (seenRelativePaths.Contains(relativePath))
            {

                continue;

            }

            try
            {

                await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    ex,
                    "Failed to delete orphaned chunks for removed file {RelativePath} in {WorkspacePath}; will retry next tick.",
                    relativePath,
                    workspacePath);

            }

        }

    }

    /// <summary>
    /// Chunks, embeds, and persists a single changed/new file. Returns <c>false</c> (without throwing)
    /// when the file is too large after reading, empty, or embedding fails — all graceful-degradation
    /// outcomes that simply do not consume the per-tick file budget.
    /// </summary>
    private async Task<bool> IndexFileAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        string fullPath,
        FileHandleIdentity expectedIdentity,
        DateTime lastWriteUtc,
        int maxFileSizeChars,
        CancellationToken cancellationToken)
    {

        string content;

        try
        {

            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Mirrors SandboxedFileIo.TryRevalidateOpenedHandle: the file could have been swapped
            // (e.g. to a symlink pointing outside the workspace) between the containment check in
            // IndexWorkspaceAsync and this open, so the opened handle's identity — and its path's
            // containment — must be re-verified before any content is read.
            if (!FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actualIdentity)
                || !FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity)
                || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspacePath, Path.GetFullPath(stream.Name), out _))
            {

                logger.LogWarning(
                    "Workspace indexing rejected {FullPath}: file identity changed between the containment check and open (possible symlink swap); skipping.",
                    fullPath);

                return false;

            }

            using StreamReader reader = new(stream);

            content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogWarning(ex, "Workspace indexing could not read {FullPath}; skipping.", fullPath);

            return false;

        }

        if (content.Length == 0 || content.Length > maxFileSizeChars)
        {

            return false;

        }

        Result<(string Chunk, int Offset)[]> chunkResult = await weaveService.ChunkAsync(content, cancellationToken).ConfigureAwait(false);

        if (chunkResult.IsFailure || chunkResult.Value.Length == 0)
        {

            return false;

        }

        (string Chunk, int Offset)[] chunks = chunkResult.Value;

        Result<Embedding<float>[]> embedResult = await weaveService
            .EmbedBatchAsync(chunks.Select(static c => c.Chunk).ToList(), cancellationToken)
            .ConfigureAwait(false);

        if (embedResult.IsFailure)
        {

            logger.LogWarning(
                "Workspace indexing embed batch failed for {FullPath} ({Code}): {Message}",
                fullPath,
                embedResult.Error.Code,
                embedResult.Error.Message);

            return false;

        }

        await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

        Embedding<float>[] generated = embedResult.Value;

        DateTimeOffset indexedAt = DateTimeOffset.UtcNow;

        for (int i = 0; i < chunks.Length; i++)
        {

            string chunkId = Guid.NewGuid().ToString("N");

            await InsertChunkAsync(
                db,
                chunkId,
                workspacePath,
                relativePath,
                chunkIndex: i,
                content: chunks[i].Chunk,
                charOffset: chunks[i].Offset,
                charLength: chunks[i].Chunk.Length,
                fileLastWriteTimeUtc: lastWriteUtc,
                indexedAt: indexedAt,
                vector: generated[i].Vector.ToArray(),
                cancellationToken).ConfigureAwait(false);

        }

        return true;

    }

    private static Task<DateTime?> GetExistingFileLastWriteTimeAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "FileLastWriteTime"
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    LIMIT 1
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                AddParameter(cmd, "@relativePath", relativePath);

                object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (result is null or DBNull)
                {

                    return (DateTime?)null;

                }

                return DateTime.Parse((string)result, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            },
            cancellationToken);

    }

    private Task DeleteExistingChunksAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand deleteVecCmd = connection.CreateCommand();

                    deleteVecCmd.CommandText =
                        """
                        DELETE FROM "workspace_file_embeddings_vec"
                        WHERE "ChunkId" IN (
                            SELECT "ChunkId" FROM "workspace_file_chunks"
                            WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                        )
                        """;

                    AddParameter(deleteVecCmd, "@workspacePath", workspacePath);

                    AddParameter(deleteVecCmd, "@relativePath", relativePath);

                    _ = await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using DbCommand deleteBlobCmd = connection.CreateCommand();

                deleteBlobCmd.CommandText =
                    """
                    DELETE FROM "workspace_file_embeddings"
                    WHERE "ChunkId" IN (
                        SELECT "ChunkId" FROM "workspace_file_chunks"
                        WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    )
                    """;

                AddParameter(deleteBlobCmd, "@workspacePath", workspacePath);

                AddParameter(deleteBlobCmd, "@relativePath", relativePath);

                _ = await deleteBlobCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand deleteChunksCmd = connection.CreateCommand();

                deleteChunksCmd.CommandText =
                    """
                    DELETE FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    """;

                AddParameter(deleteChunksCmd, "@workspacePath", workspacePath);

                AddParameter(deleteChunksCmd, "@relativePath", relativePath);

                _ = await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private Task InsertChunkAsync(
        ArcanumDbContext db,
        string chunkId,
        string workspacePath,
        string relativePath,
        int chunkIndex,
        string content,
        int charOffset,
        int charLength,
        DateTime fileLastWriteTimeUtc,
        DateTimeOffset indexedAt,
        float[] vector,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand chunkCmd = connection.CreateCommand();

                chunkCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_chunks"
                        ("ChunkId", "WorkspacePath", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "FileLastWriteTime", "IndexedAt")
                    VALUES
                        (@chunkId, @workspacePath, @relativePath, @chunkIndex, @content, @charOffset, @charLength, @fileLastWriteTime, @indexedAt)
                    """;

                AddParameter(chunkCmd, "@chunkId", chunkId);

                AddParameter(chunkCmd, "@workspacePath", workspacePath);

                AddParameter(chunkCmd, "@relativePath", relativePath);

                AddParameter(chunkCmd, "@chunkIndex", chunkIndex);

                AddParameter(chunkCmd, "@content", content);

                AddParameter(chunkCmd, "@charOffset", charOffset);

                AddParameter(chunkCmd, "@charLength", charLength);

                AddParameter(chunkCmd, "@fileLastWriteTime", fileLastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(chunkCmd, "@indexedAt", indexedAt.ToString("o", CultureInfo.InvariantCulture));

                _ = await chunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                byte[] encoded = EmbeddingBlobCodec.Encode(vector);

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_embeddings" ("ChunkId", "Embedding", "Dim")
                    VALUES (@chunkId, @embedding, @dim)
                    """;

                AddParameter(embeddingCmd, "@chunkId", chunkId);

                AddParameter(embeddingCmd, "@embedding", encoded);

                AddParameter(embeddingCmd, "@dim", vector.Length);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (!weaveIndexAvailability.IsVecAvailable)
                {

                    return;

                }

                await using DbCommand vecCmd = connection.CreateCommand();

                vecCmd.CommandText =
                    """
                    INSERT OR REPLACE INTO "workspace_file_embeddings_vec" ("ChunkId", "Embedding")
                    VALUES (@chunkId, @embedding)
                    """;

                AddParameter(vecCmd, "@chunkId", chunkId);

                AddParameter(vecCmd, "@embedding", encoded);

                _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private static async Task<DbConnection> OpenConnectionAsync(ArcanumDbContext db, CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        return connection;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

}
