using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// Indexes workspace files into The Weave for semantic codebase retrieval. Maintains a
/// thread-safe set of "known" workspace paths (populated by <see cref="RegisterWorkspace"/>, called by
/// <c>WizardIntelligenceProvider</c> on every inference turn) and re-indexes each of them on a
/// background interval, plus supports an immediate on-demand re-index via <see cref="QueueIndexNow"/>
/// (used by the manual <c>POST /api/workspaces/{id}/files/index</c> endpoint).
///
/// Idles unless <c>Arcanum:Features:CodebaseRetrieval</c> is enabled; the polling cadence is a
/// code-owned invariant. This is therefore a no-op on the hot path until an operator opts in, using
/// the same idle-when-disabled pattern as <see cref="EntryWeavingService"/>.
///
/// Change detection: a file is only re-chunked/re-embedded when its <c>LastWriteTimeUtc</c> differs from
/// the <c>FileLastWriteTime</c> recorded on its existing <c>workspace_file_chunks</c> rows — unchanged
/// files are skipped every tick, keeping steady-state re-index cost proportional to the number of edited
/// files, not the size of the workspace. Existing RelativePath → FileLastWriteTime pairs are loaded in
/// one query per workspace tick (not a per-file SELECT).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService workspace indexing scheduler; covered via WorkspaceIndexingServiceTests exercising the indexing logic directly.
internal sealed partial class WorkspaceIndexingService(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IWeaveService weaveService,
    WeaveIndexAvailability weaveIndexAvailability,
    IServiceScopeFactory scopeFactory,
    IWorkspaceFileWatcherFactory watcherFactory,
    IGrimoireConnectionAdmissionGate workAdmission,
    ILogger<WorkspaceIndexingService> logger) :
    BackgroundService,
    IWorkspaceIndexingService,
    IWorkspaceIndexRuntimeStatusProvider,
    IAsyncDisposable
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
    /// Path comparison for the visited canonical-directory set that terminates symlink cycles, matching
    /// <c>EyeOfTheWorldService.ScanWorkspace</c> and <c>SpellScanner</c>.
    /// </summary>
    private static readonly StringComparer CanonicalDirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The <c>FileLength</c> a row carries when no length was recorded for it.
    /// </summary>
    /// <remarks>
    /// Every row an upgrade to Core schema version 6 inherits holds this, and so does every row
    /// between its insert and the metadata pass that stamps the real value. No file can have a
    /// negative length, so a row holding it compares unequal to whatever the file now reports and is
    /// re-indexed once rather than being trusted half-written or unmeasured.
    /// </remarks>
    private const long UnrecordedFileLength = -1;

    private const int MaxPendingPathsPerWorkspace = 4_096;

    private readonly IGrimoireConnectionAdmissionGate _workAdmission = workAdmission;

    private async Task<WorkspaceUnitOutcome> ProcessIncrementalChangesAsync(
        string workspacePath,
        WorkspaceDemand demand,
        IGrimoireWorkLease workLease,
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {
        if (!weaveService.IsAvailable || !Directory.Exists(workspacePath))
        {
            return WorkspaceUnitOutcome.Failed;
        }

        CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

        HashSet<string> extensions = new(codebase.FileExtensions, StringComparer.OrdinalIgnoreCase);

        int maxFilesToIndex = ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex(codebase.MaxFilesToIndex);

        int filesIndexed = demand.FilesIndexed;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        foreach ((string fullPath, PendingPathAction action) in demand.Actions
                     .OrderBy(static pair => pair.Value == PendingPathAction.Delete ? 0 : 1)
                     .ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool consumed = true;

            try
            {
                string normalizedPath;

                try
                {
                    normalizedPath = Path.GetFullPath(fullPath);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!WorkspacePathPolicy.IsPathUnderWorkspace(workspacePath, normalizedPath))
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(workspacePath, normalizedPath);

                if (ContainsIgnoredDirectorySegment(relativePath))
                {
                    await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (action == PendingPathAction.Delete)
                {
                    if (Directory.Exists(normalizedPath))
                    {
                        return WorkspaceUnitOutcome.Failed;
                    }

                    if (!File.Exists(normalizedPath))
                    {
                        await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                        continue;
                    }
                }

                if (Directory.Exists(normalizedPath))
                {
                    return WorkspaceUnitOutcome.Failed;
                }

                bool ignored = !extensions.Contains(Path.GetExtension(normalizedPath));

                if (ignored || !File.Exists(normalizedPath))
                {
                    await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        workspacePath,
                        normalizedPath,
                        out string? resolvedFinalPath))
                {
                    await DeleteExistingChunksAsync(db, workspacePath, relativePath, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                string identityPath = Path.GetFullPath(resolvedFinalPath ?? normalizedPath);

                if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
                {
                    return WorkspaceUnitOutcome.Failed;
                }

                FileInfo info = new(normalizedPath);

                if (filesIndexed >= maxFilesToIndex)
                {
                    return WorkspaceUnitOutcome.Failed;
                }

                if (!workLease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admittedEffect))
                {
                    consumed = false;

                    return WorkspaceUnitOutcome.Deferred;
                }

                await using IGrimoireExternalEffectGroup effect = admittedEffect!;

                bool indexed = await IndexFileAsync(
                    db,
                    workspacePath,
                    relativePath,
                    normalizedPath,
                    expectedIdentity,
                    info.LastWriteTimeUtc,
                    info.Length,
                    cancellationToken).ConfigureAwait(false);

                if (!indexed)
                {
                    return WorkspaceUnitOutcome.Failed;
                }

                filesIndexed++;

                demand.FilesIndexed = filesIndexed;
            }
            finally
            {
                if (consumed)
                {
                    demand.Actions.Remove(fullPath);
                }
            }
        }

        return WorkspaceUnitOutcome.Completed;
    }

    internal async Task<bool> IndexWorkspaceAsync(string workspacePath, EmbeddingSettings embeddings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_workAdmission.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out IGrimoireWorkLease? admitted))
        {
            return false;
        }

        await using IGrimoireWorkLease workLease = admitted!;

        return await IndexWorkspaceCoreAsync(workspacePath, embeddings, new WorkspaceDemand { Full = true }, workLease, cancellationToken)
            .ConfigureAwait(false) == WorkspaceUnitOutcome.Completed;
    }

    private async Task<WorkspaceUnitOutcome> IndexWorkspaceCoreAsync(
        string workspacePath,
        EmbeddingSettings embeddings,
        WorkspaceDemand demand,
        IGrimoireWorkLease workLease,
        CancellationToken cancellationToken)
    {
        if (!weaveService.IsAvailable)
        {
            logger.LogDebug(
                "Workspace indexing tick skipped for {WorkspacePath}: The Weave is unavailable (enable an embedding-backed Arcanum:Features option and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model).",
                workspacePath);

            return WorkspaceUnitOutcome.Failed;
        }

        CodebaseEmbeddingSettings codebase = embeddings.Codebase ?? new CodebaseEmbeddingSettings();

        if (codebase.FileExtensions.Length == 0)
        {
            logger.LogDebug("Workspace indexing tick skipped for {WorkspacePath}: no file extensions configured.", workspacePath);

            return WorkspaceUnitOutcome.Failed;
        }

        if (!Directory.Exists(workspacePath))
        {
            logger.LogWarning("Workspace indexing skipped: {WorkspacePath} does not exist or is not a directory.", workspacePath);

            return WorkspaceUnitOutcome.Failed;
        }

        int maxFilesToIndex = ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex(codebase.MaxFilesToIndex);

        HashSet<string> extensions = new(codebase.FileExtensions, StringComparer.OrdinalIgnoreCase);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        int filesIndexed = demand.FilesIndexed;

        bool failed = false;

        IEnumerable<string> candidates =
            EnumerateCandidateFiles(workspacePath, extensions);

        // Every candidate that reaches this point still exists at its relative path, regardless of
        // whether this tick actually gets to re-embed it (see the maxFilesToIndex check below) — so
        // this set drives orphaned-chunk cleanup after the loop, not just the re-index decision.
        HashSet<string> seenRelativePaths = new(StringComparer.Ordinal);

        // One query per workspace tick: RelativePath → the recorded change-detection signals,
        // instead of a per-file SELECT (N+1 against workspace_file_chunks).
        Dictionary<string, WorkspaceFileSignature> existingSignatureByRelativePath =
            await LoadExistingFileSignaturesAsync(db, workspacePath, cancellationToken).ConfigureAwait(false);

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

                DateTime lastWriteUtc = info.LastWriteTimeUtc;

                long fileLength = info.Length;

                // Both signals have to agree before a file is called unchanged. The timestamp alone
                // cannot say so: a rewrite landing on the recorded value - a coarse-granularity
                // filesystem, a restored archive, a tool that puts the timestamp back - would be
                // skipped by every later tick, permanently, because nothing else revisits it. A row
                // that predates FileLength holds -1, which no file's length can be, so an inherited
                // row re-indexes once and then carries a real value.
                if (!demand.ShouldForceFile(fullPath)
                    && existingSignatureByRelativePath.TryGetValue(relativePath, out WorkspaceFileSignature existing)
                    && existing.LastWriteUtc == lastWriteUtc
                    && existing.FileLength == fileLength)
                {
                    // Unchanged since last index — skip without consuming the per-tick file budget.
                    continue;
                }

                if (!workLease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admittedEffect))
                {
                    return WorkspaceUnitOutcome.Deferred;
                }

                await using IGrimoireExternalEffectGroup effect = admittedEffect!;

                bool indexed = await IndexFileAsync(
                    db,
                    workspacePath,
                    relativePath,
                    fullPath,
                    expectedIdentity,
                    lastWriteUtc,
                    fileLength,
                    cancellationToken).ConfigureAwait(false);

                if (indexed)
                {
                    filesIndexed++;

                    demand.FilesIndexed = filesIndexed;

                    demand.MarkFileCompleted(fullPath);
                }
                else
                {
                    failed = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;

                logger.LogWarning(ex, "Workspace indexing failed for file {FullPath}; continuing with the next file.", fullPath);
            }
        }

        await DeleteOrphanedChunksAsync(db, workspacePath, seenRelativePaths, cancellationToken).ConfigureAwait(false);

        return failed ? WorkspaceUnitOutcome.Failed : WorkspaceUnitOutcome.Completed;
    }

    /// <summary>
    /// Manually walks <paramref name="workspacePath"/> breadth-first (mirroring
    /// <c>PhysicalFileSystemBrowser.ListAsync</c>'s recursive-listing walk), pruning
    /// <see cref="IgnoredDirectorySegments"/> and symlink-escaping subdirectories <b>before</b>
    /// descending into them — unlike <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/>
    /// with <c>RecurseSubdirectories = true</c>, which would still visit every entry under a huge
    /// ignored directory (for example <c>node_modules</c>) only to discard them one by one. The walk
    /// continues until every reachable candidate has been visited or the caller cancels, and terminates
    /// symlink cycles via a canonical visited set (the same guard <c>EyeOfTheWorldService.ScanWorkspace</c>
    /// and <c>SpellScanner</c> carry): containment accepts a directory link whose target resolves back
    /// under the root, so without the set a link such as <c>docs/latest -&gt; ..</c> re-reaches the whole
    /// tree at every depth. This is cycle termination, <b>not</b> a total-entry ceiling.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidateFiles(
        string workspacePath,
        HashSet<string> extensions)
    {
        HashSet<string> visitedCanonicalDirs = new(CanonicalDirectoryComparer)
        {
            ResolveCanonicalDirectory(workspacePath),
        };

        Queue<string> pendingDirectories = new();

        pendingDirectories.Enqueue(workspacePath);

        while (pendingDirectories.Count > 0)
        {

            string directory = pendingDirectories.Dequeue();

            foreach (string fullPath in EnumerateAccessibleEntries(directory))
            {

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

                    if (!visitedCanonicalDirs.Add(ResolveCanonicalDirectory(fullPath)))
                    {

                        // Symlink cycle — already visited this canonical directory.
                        continue;

                    }

                    pendingDirectories.Enqueue(fullPath);

                }
                else if (extensions.Contains(Path.GetExtension(fullPath)))
                {

                    yield return fullPath;

                }

            }

        }

    }

    /// <summary>
    /// The identity a directory is remembered by in the visited set: its fully resolved final target, so
    /// two aliases of one physical directory collapse to a single entry. A directory that cannot be
    /// resolved falls back to its absolute path and finally to the path as given, which keeps the walk
    /// going rather than failing the whole tick.
    /// </summary>
    private static string ResolveCanonicalDirectory(string directory)
    {

        try
        {

            return Directory.ResolveLinkTarget(directory, returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(directory);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {

            try
            {

                return Path.GetFullPath(directory);

            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
            {

                return directory;

            }

        }

    }

    private static IEnumerable<string> EnumerateAccessibleEntries(
        string directory)
    {

        IEnumerator<string>? enumerator = null;

        try
        {

            enumerator = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .GetEnumerator();

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            yield break;

        }

        using (enumerator)
        {

            while (true)
            {

                string fullPath;

                try
                {

                    if (!enumerator.MoveNext())
                    {

                        yield break;

                    }

                    fullPath = enumerator.Current;

                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {

                    yield break;

                }

                yield return fullPath;

            }

        }

    }

    /// <summary>
    /// Deletes chunks for any previously-indexed file under <paramref name="workspacePath"/> whose
    /// relative path is not in <paramref name="seenRelativePaths"/> — i.e. a file that was deleted (or
    /// renamed, or moved outside every configured file extension) since the last successful full walk.
    /// Without this, a removed file's stale chunks/embeddings persist forever and keep surfacing in
    /// semantic search results for content that no longer exists. It runs only after the complete
    /// cancellable workspace walk, so an unvisited-but-still-present file is never misclassified.
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
    /// when the file is empty or embedding fails — all graceful-degradation
    /// outcomes that simply do not consume the per-tick file budget.
    /// </summary>
    private async Task<bool> IndexFileAsync(
        ArcanumDbContext db,
        string workspacePath,
        string relativePath,
        string fullPath,
        FileHandleIdentity expectedIdentity,
        DateTime lastWriteUtc,
        long fileLength,
        CancellationToken cancellationToken)
    {

        int maxChunkChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            optionsMonitor.CurrentValue.ResolveEmbeddings().ChunkSizeChars);

        int readPageCharacters = checked(maxChunkChars * 8);

        char[] buffer = ArrayPool<char>.Shared.Rent(readPageCharacters);

        List<string> insertedIds = [];

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

            string extension = Path.GetExtension(relativePath);

            DateTimeOffset indexedAt = DateTimeOffset.UtcNow;

            HashSet<string> existingIds = await LoadExistingChunkIdsAsync(
                db,
                workspacePath,
                relativePath,
                cancellationToken).ConfigureAwait(false);

            HashSet<string> nextIds = new(StringComparer.Ordinal);

            Dictionary<string, int> occurrences = new(StringComparer.Ordinal);

            List<IndexedChunkMetadata> metadata = [];

            int globalCharacterOffset = 0;

            int globalLineOffset = 0;

            int globalChunkIndex = 0;

            // A high surrogate held back from the previous page, already sitting at buffer[0].
            int carriedChars = 0;

            while (true)
            {

                int requested = readPageCharacters - carriedChars;

                int read = await reader
                    .ReadBlockAsync(
                        buffer.AsMemory(carriedChars, requested),
                        cancellationToken)
                    .ConfigureAwait(false);

                int available = carriedChars + read;

                if (available == 0)
                {

                    break;

                }

                // ReadBlockAsync fills to a character count and knows nothing about UTF-16 pairs, so a
                // non-BMP character straddling the page boundary would leave this page ending in a lone
                // high surrogate and the next beginning with the orphaned low half. Both fragments would
                // then be embedded and persisted with the character replaced by U+FFFD, so the stored
                // chunk would no longer be the exact source slice. Holding the high surrogate back joins
                // it to its partner on the next page. A short read means end of stream: a trailing high
                // surrogate there is genuinely unpaired and is emitted rather than silently dropped.
                bool endOfStream = read < requested;

                int pageLength = !endOfStream && char.IsHighSurrogate(buffer[available - 1])
                    ? available - 1
                    : available;

                string page = new(buffer, 0, pageLength);

                WorkspaceCodeChunker.Chunk[] pageChunks =
                    WorkspaceCodeChunker.ChunkText(
                        page,
                        extension,
                        maxChunkChars);

                if (pageChunks.Length > 0)
                {

                    IndexedChunk[] indexedChunks = new IndexedChunk[pageChunks.Length];

                    for (int index = 0; index < pageChunks.Length; index++)
                    {

                        WorkspaceCodeChunker.Chunk chunk = pageChunks[index];

                        int occurrence = occurrences.GetValueOrDefault(chunk.Content);

                        occurrences[chunk.Content] = occurrence + 1;

                        indexedChunks[index] = new IndexedChunk(
                            CreateStableChunkId(
                                workspacePath,
                                relativePath,
                                chunk.Content,
                                occurrence),
                            chunk);

                    }

                    IndexedChunk[] missing = indexedChunks
                        .Where(chunk => !existingIds.Contains(chunk.ChunkId))
                        .ToArray();

                    Embedding<float>[] generated = [];

                    if (missing.Length > 0)
                    {

                        Result<Embedding<float>[]> embedResult = await weaveService
                            .EmbedBatchAsync(
                                missing
                                    .Select(static chunk => chunk.Chunk.Content)
                                    .ToList(),
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (embedResult.IsFailure
                            || embedResult.Value.Length != missing.Length)
                        {

                            logger.LogWarning(
                                "Workspace indexing embed page failed for {FullPath} ({Code}): {Message}",
                                fullPath,
                                embedResult.IsFailure
                                    ? embedResult.Error.Code
                                    : ErrorCodes.Embeddings.ProviderUnavailable,
                                embedResult.IsFailure
                                    ? embedResult.Error.Message
                                    : "Embedding provider returned an unexpected result count.");

                            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

                            return false;

                        }

                        generated = embedResult.Value;

                    }

                    Dictionary<string, float[]> vectorsByChunkId = new(StringComparer.Ordinal);

                    for (int index = 0; index < missing.Length; index++)
                    {

                        vectorsByChunkId[missing[index].ChunkId] = generated[index].Vector.ToArray();

                    }

                    for (int index = 0; index < indexedChunks.Length; index++)
                    {

                        IndexedChunk indexedChunk = indexedChunks[index];

                        WorkspaceCodeChunker.Chunk chunk = indexedChunk.Chunk;

                        int chunkIndex = globalChunkIndex++;

                        nextIds.Add(indexedChunk.ChunkId);

                        metadata.Add(new IndexedChunkMetadata(
                            indexedChunk.ChunkId,
                            chunkIndex,
                            globalCharacterOffset + chunk.CharOffset,
                            chunk.Content.Length,
                            globalLineOffset + chunk.StartLine,
                            globalLineOffset + chunk.EndLine));

                        if (existingIds.Contains(indexedChunk.ChunkId))
                        {

                            continue;

                        }

                        insertedIds.Add(indexedChunk.ChunkId);

                        await InsertChunkAsync(
                                db,
                                indexedChunk.ChunkId,
                                workspacePath,
                                relativePath,
                                chunkIndex,
                                chunk.Content,
                                globalCharacterOffset + chunk.CharOffset,
                                chunk.Content.Length,
                                globalLineOffset + chunk.StartLine,
                                globalLineOffset + chunk.EndLine,
                                DateTime.MinValue,
                                UnrecordedFileLength,
                                indexedAt,
                                vectorsByChunkId[indexedChunk.ChunkId],
                                cancellationToken)
                            .ConfigureAwait(false);

                    }

                }

                globalCharacterOffset += pageLength;

                globalLineOffset += page.Count(static character => character == '\n');

                carriedChars = available - pageLength;

                if (carriedChars == 1)
                {

                    buffer[0] = buffer[available - 1];

                }

            }

            foreach (string obsoleteId in existingIds.Where(id => !nextIds.Contains(id)))
            {

                await DeleteChunkByIdAsync(db, obsoleteId, cancellationToken).ConfigureAwait(false);

            }

            foreach (IndexedChunkMetadata chunk in metadata)
            {

                await UpdateChunkMetadataAsync(
                    db,
                    chunk.ChunkId,
                    chunk.ChunkIndex,
                    chunk.CharOffset,
                    chunk.CharLength,
                    chunk.StartLine,
                    chunk.EndLine,
                    lastWriteUtc,
                    fileLength,
                    cancellationToken).ConfigureAwait(false);

            }

            return true;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            throw;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            logger.LogWarning(ex, "Workspace indexing could not read {FullPath}; skipping.", fullPath);

            return false;

        }
        catch
        {

            await DeleteInsertedChunksAsync(db, insertedIds).ConfigureAwait(false);

            throw;

        }
        finally
        {

            ArrayPool<char>.Shared.Return(buffer);

        }

    }

    private async Task DeleteInsertedChunksAsync(
        ArcanumDbContext db,
        IEnumerable<string> insertedIds)
    {

        foreach (string chunkId in insertedIds)
        {

            try
            {

                await DeleteChunkByIdAsync(
                    db,
                    chunkId,
                    CancellationToken.None).ConfigureAwait(false);

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    ex,
                    "Workspace indexing could not clean up incomplete chunk {ChunkId}; reconciliation will retry it.",
                    chunkId);

            }

        }

    }

    private static string CreateStableChunkId(
        string workspacePath,
        string relativePath,
        string content,
        int occurrence)
    {

        string identity = string.Concat(
            workspacePath,
            "\0",
            relativePath,
            "\0",
            occurrence.ToString(CultureInfo.InvariantCulture),
            "\0",
            content);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    }

    private static Task<HashSet<string>> LoadExistingChunkIdsAsync(
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
                    SELECT "ChunkId"
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath AND "RelativePath" = @relativePath
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                AddParameter(cmd, "@relativePath", relativePath);

                HashSet<string> ids = new(StringComparer.Ordinal);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    ids.Add(reader.GetString(0));

                }

                return ids;

            },
            cancellationToken);

    }

    private Task UpdateChunkMetadataAsync(
        ArcanumDbContext db,
        string chunkId,
        int chunkIndex,
        int charOffset,
        int charLength,
        int startLine,
        int endLine,
        DateTime fileLastWriteTimeUtc,
        long fileLength,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "workspace_file_chunks"
                    SET "ChunkIndex" = @chunkIndex,
                        "CharOffset" = @charOffset,
                        "CharLength" = @charLength,
                        "StartLine" = @startLine,
                        "EndLine" = @endLine,
                        "FileLastWriteTime" = @fileLastWriteTime,
                        "FileLength" = @fileLength
                    WHERE "ChunkId" = @chunkId
                    """;

                AddParameter(cmd, "@chunkIndex", chunkIndex);

                AddParameter(cmd, "@charOffset", charOffset);

                AddParameter(cmd, "@charLength", charLength);

                AddParameter(cmd, "@startLine", startLine);

                AddParameter(cmd, "@endLine", endLine);

                AddParameter(cmd, "@fileLastWriteTime", fileLastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@fileLength", fileLength);

                AddParameter(cmd, "@chunkId", chunkId);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private Task DeleteChunkByIdAsync(
        ArcanumDbContext db,
        string chunkId,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.CommandText = """DELETE FROM "workspace_file_embeddings_vec" WHERE "ChunkId" = @chunkId""";

                    AddParameter(vecCmd, "@chunkId", chunkId);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.CommandText = """DELETE FROM "workspace_file_embeddings" WHERE "ChunkId" = @chunkId""";

                AddParameter(embeddingCmd, "@chunkId", chunkId);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand chunkCmd = connection.CreateCommand();

                chunkCmd.CommandText = """DELETE FROM "workspace_file_chunks" WHERE "ChunkId" = @chunkId""";

                AddParameter(chunkCmd, "@chunkId", chunkId);

                _ = await chunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    /// <summary>
    /// Loads every indexed file's recorded change-detection signals for
    /// <paramref name="workspacePath"/> in a single query, so a workspace tick does not issue one
    /// SELECT per candidate file.
    /// </summary>
    private static Task<Dictionary<string, WorkspaceFileSignature>> LoadExistingFileSignaturesAsync(
        ArcanumDbContext db,
        string workspacePath,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // All chunks for a given RelativePath share both signals - IndexFileAsync stamps them
                // together in one pass - so MIN and MAX agree and either would do. MIN is the one that
                // fails safe if they ever diverge: the smaller value is the one least likely to equal
                // what the file now reports, so a divergent set re-indexes rather than being trusted.
                cmd.CommandText =
                    """
                    SELECT "RelativePath", MIN("FileLastWriteTime"), MIN("FileLength")
                    FROM "workspace_file_chunks"
                    WHERE "WorkspacePath" = @workspacePath
                    GROUP BY "RelativePath"
                    """;

                AddParameter(cmd, "@workspacePath", workspacePath);

                Dictionary<string, WorkspaceFileSignature> map = new(StringComparer.Ordinal);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    string relativePath = reader.GetString(0);

                    DateTime lastWrite = DateTime.Parse(
                        reader.GetString(1),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind);

                    map[relativePath] = new WorkspaceFileSignature(
                        lastWrite,
                        reader.IsDBNull(2) ? UnrecordedFileLength : reader.GetInt64(2));

                }

                return map;

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
        int startLine,
        int endLine,
        DateTime fileLastWriteTimeUtc,
        long fileLength,
        DateTimeOffset indexedAt,
        float[] vector,
        CancellationToken cancellationToken)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);

                // One transaction over the chunk row, its embedding, and the optional vec0 mirror — the
                // shape TapestryStore.AppendNodesAsync already uses, for the same two reasons. A torn
                // write can never leave a chunk row without its embedding: LoadExistingChunkIdsAsync
                // reads only workspace_file_chunks, so such a row would count as already indexed forever
                // while DivinationService, which joins outward from the embeddings table, could never
                // return it — permanently unsearchable yet still counted by the index status endpoint.
                // And a SQLITE_BUSY retry restarts from a clean transaction instead of replaying the
                // chunk INSERT into a PRIMARY KEY violation, which is not retryable and aborts the file.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using DbCommand chunkCmd = connection.CreateCommand();

                chunkCmd.Transaction = transaction;

                chunkCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_chunks"
                        ("ChunkId", "WorkspacePath", "RelativePath", "ChunkIndex", "Content", "CharOffset", "CharLength", "StartLine", "EndLine", "FileLastWriteTime", "FileLength", "IndexedAt")
                    VALUES
                        (@chunkId, @workspacePath, @relativePath, @chunkIndex, @content, @charOffset, @charLength, @startLine, @endLine, @fileLastWriteTime, @fileLength, @indexedAt)
                    """;

                AddParameter(chunkCmd, "@chunkId", chunkId);

                AddParameter(chunkCmd, "@workspacePath", workspacePath);

                AddParameter(chunkCmd, "@relativePath", relativePath);

                AddParameter(chunkCmd, "@chunkIndex", chunkIndex);

                AddParameter(chunkCmd, "@content", content);

                AddParameter(chunkCmd, "@charOffset", charOffset);

                AddParameter(chunkCmd, "@charLength", charLength);

                AddParameter(chunkCmd, "@startLine", startLine);

                AddParameter(chunkCmd, "@endLine", endLine);

                AddParameter(chunkCmd, "@fileLastWriteTime", fileLastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(chunkCmd, "@fileLength", fileLength);

                AddParameter(chunkCmd, "@indexedAt", indexedAt.ToString("o", CultureInfo.InvariantCulture));

                _ = await chunkCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                byte[] encoded = EmbeddingBlobCodec.Encode(vector);

                await using DbCommand embeddingCmd = connection.CreateCommand();

                embeddingCmd.Transaction = transaction;

                embeddingCmd.CommandText =
                    """
                    INSERT INTO "workspace_file_embeddings" ("ChunkId", "Embedding", "Dim")
                    VALUES (@chunkId, @embedding, @dim)
                    """;

                AddParameter(embeddingCmd, "@chunkId", chunkId);

                AddParameter(embeddingCmd, "@embedding", encoded);

                AddParameter(embeddingCmd, "@dim", vector.Length);

                _ = await embeddingCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (weaveIndexAvailability.IsVecAvailable)
                {

                    await using DbCommand vecCmd = connection.CreateCommand();

                    vecCmd.Transaction = transaction;

                    vecCmd.CommandText =
                        """
                        INSERT OR REPLACE INTO "workspace_file_embeddings_vec" ("ChunkId", "Embedding")
                        VALUES (@chunkId, @embedding)
                        """;

                    AddParameter(vecCmd, "@chunkId", chunkId);

                    AddParameter(vecCmd, "@embedding", encoded);

                    _ = await vecCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            },
            cancellationToken);

    }

    private static async Task<DbConnection> OpenConnectionAsync(ArcanumDbContext db, CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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

    private static bool ContainsIgnoredDirectorySegment(string relativePath)
    {

        foreach (string segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {

            if (IgnoredDirectorySegments.Contains(segment))
            {

                return true;

            }

        }

        return false;

    }

    private enum PendingPathAction
    {

        Upsert,
        Delete,

    }

    private sealed class RuntimeStatusState
    {

        private readonly object _gate = new();

        private bool _watching;

        private bool _degraded;

        private bool _overflowed;

        private bool _reconciling;

        private DateTimeOffset? _lastEventAt;

        private DateTimeOffset? _lastSuccessfulIndexAt;

        public WorkspaceIndexRuntimeStatus Snapshot()
        {

            lock (_gate)
            {

                return new WorkspaceIndexRuntimeStatus(
                    _watching,
                    _degraded,
                    _overflowed,
                    _reconciling,
                    _lastEventAt,
                    _lastSuccessfulIndexAt);

            }

        }

        public void SetWatching(bool watching)
        {

            lock (_gate)
            {

                _watching = watching;

            }

        }

        public void MarkEvent()
        {

            lock (_gate)
            {

                _lastEventAt = DateTimeOffset.UtcNow;

            }

        }

        public void MarkDegraded(bool overflowed)
        {

            lock (_gate)
            {

                _degraded = true;

                _overflowed |= overflowed;

            }

        }

        public void SetReconciling(bool reconciling)
        {

            lock (_gate)
            {

                _reconciling = reconciling;

            }

        }

        public void MarkSuccessfulIndex()
        {

            lock (_gate)
            {

                _lastSuccessfulIndexAt = DateTimeOffset.UtcNow;

            }

        }

        public void MarkReconciled()
        {

            lock (_gate)
            {

                _degraded = false;

                _overflowed = false;

                _lastSuccessfulIndexAt = DateTimeOffset.UtcNow;

            }

        }

    }

    /// <summary>
    /// What a file looked like when it was last indexed: the two signals a tick compares before it
    /// decides the file is unchanged.
    /// </summary>
    /// <remarks>
    /// A struct rather than a class because the per-tick map holds one per indexed file and nothing
    /// stores it past the tick, and positional because it is a pair of values with no behaviour.
    /// </remarks>
    private readonly record struct WorkspaceFileSignature(DateTime LastWriteUtc, long FileLength);

    private sealed record IndexedChunk(
        string ChunkId,
        WorkspaceCodeChunker.Chunk Chunk);

    private sealed record IndexedChunkMetadata(
        string ChunkId,
        int ChunkIndex,
        int CharOffset,
        int CharLength,
        int StartLine,
        int EndLine);

}
