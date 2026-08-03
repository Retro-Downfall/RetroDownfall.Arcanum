using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed record SpellMetadata(
    string Name,
    string Description,
    string FilePath,
    string[]? Tags = null,
    string[]? Tools = null);

public sealed record ParsedSpell(
    string Name,
    string Description,
    string FilePath,
    string FullContent,
    string DirectoryPath,
    IReadOnlyList<string> AvailableScripts)
{

    public string[] Tags { get; init; } = Array.Empty<string>();

    public string? SystemPrompt { get; init; }

    public string? Template { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string[] Tools { get; init; } = Array.Empty<string>();

    public string[] RequiredMcpServers { get; init; } = Array.Empty<string>();

    public string Body { get; init; } = string.Empty;

    public SkillMetadata? SkillMetadata { get; init; }

}

internal static class SpellScanner
{

    private const int FullSpellCacheCapacity = 256;

    private const int MetadataScanCacheCapacity = 32;

    private static readonly BoundedLruCache<string, ParsedSpell> FullSpellCache = new(FullSpellCacheCapacity);

    private static readonly BoundedLruCache<MetadataScanCacheKey, MetadataScanCacheEntry> MetadataScanCache =
        new(MetadataScanCacheCapacity);

    /// <summary>
    /// Per-key single-flight map for in-flight <see cref="ScanMetadataAsync"/> miss waves, so
    /// concurrent misses for the same workspace share one scan task instead of stampeding.
    /// Entries are cleaned up once the shared task completes (see <see cref="SingleFlight"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<MetadataScanCacheKey, Lazy<Task<IReadOnlyList<SpellMetadata>>>> MetadataScanInFlight = new();

    /// <summary>
    /// Per-key single-flight map for in-flight <see cref="LoadFullAsync"/> miss waves, keyed by
    /// the same <c>$"{fullPath}|{mtimeTicks}"</c> string as <see cref="FullSpellCache"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<ParsedSpell?>>> FullSpellInFlight = new();

    private readonly record struct MetadataScanCacheKey(string GlobalRoot, string WorkspaceRoot);

    private sealed class MetadataScanCacheEntry(IReadOnlyList<SpellMetadata> metadata, DateTimeOffset cachedAt)
    {

        public IReadOnlyList<SpellMetadata> Metadata { get; } = metadata;

        public DateTimeOffset CachedAt { get; } = cachedAt;

    }

    private static readonly HashSet<string> HeavyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "out",
        "dist",
    };

    /// <summary>
    /// Path comparison used for the visited canonical-directory set that breaks symlink cycles.
    /// </summary>
    private static readonly StringComparer CanonicalDirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static async Task<IReadOnlyList<ParsedSpell>> ScanAsync(
        string? workspaceRoot,
        CancellationToken cancellationToken,
        long maxFileSizeBytes,
        int? maxDeclaredTools = null)
    {
        string globalSpellsDir = ArcanumPaths.GlobalSpellsDirectory;

        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(globalSpellsDir);
        }
        catch
        {
            globalRoot = string.Empty;
        }

        List<ParsedSpell> globalSpells = [];

        if (globalRoot.Length > 0 && Directory.Exists(globalRoot))
        {
            globalSpells = await Task.Run(
                () => ScanTreeAsync(globalRoot, maxFileSizeBytes, maxDeclaredTools, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        List<ParsedSpell> localSpells = [];

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string localRoot;

            try
            {
                localRoot = Path.GetFullPath(workspaceRoot.Trim());
            }
            catch
            {
                localRoot = string.Empty;
            }

            if (localRoot.Length > 0 && Directory.Exists(localRoot))
            {
                localSpells = await Task.Run(
                    () => ScanTreeAsync(localRoot, maxFileSizeBytes, maxDeclaredTools, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return MergeSpells(globalSpells, localSpells);
    }

    internal static async Task<IReadOnlyList<SpellMetadata>> ScanMetadataAsync(
        string? workspaceRoot,
        CancellationToken cancellationToken,
        long maxFileSizeBytes,
        int metadataScanCacheTtlSeconds = 0)
    {
        string globalSpellsDir = ArcanumPaths.GlobalSpellsDirectory;

        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(globalSpellsDir);
        }
        catch
        {
            globalRoot = string.Empty;
        }

        string localRoot = string.Empty;

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                localRoot = Path.GetFullPath(workspaceRoot.Trim());
            }
            catch
            {
                localRoot = string.Empty;
            }
        }

        int ttlSeconds = ArcanumSettingClamps.MetadataScanCacheTtlSeconds(metadataScanCacheTtlSeconds);

        MetadataScanCacheKey cacheKey = new(globalRoot, localRoot);

        if (ttlSeconds > 0
            && MetadataScanCache.TryGetValue(cacheKey, out MetadataScanCacheEntry? cachedEntry)
            && DateTimeOffset.UtcNow - cachedEntry.CachedAt < TimeSpan.FromSeconds(ttlSeconds))
        {
            return cachedEntry.Metadata;
        }

        // Single-flight the miss path: concurrent misses for the same (global, local) root pair
        // share one scan task. Only the leader scans and populates the LRU cache.
        IReadOnlyList<SpellMetadata> merged = await SingleFlight.CoalesceAsync(
            MetadataScanInFlight,
            cacheKey,
            async () =>
            {

                List<SpellMetadata> globalSpells = [];

                if (globalRoot.Length > 0 && Directory.Exists(globalRoot))
                {
                    globalSpells = await Task.Run(
                        () => ScanMetadataTreeAsync(globalRoot, maxFileSizeBytes, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                List<SpellMetadata> localSpells = [];

                if (localRoot.Length > 0 && Directory.Exists(localRoot))
                {
                    localSpells = await Task.Run(
                        () => ScanMetadataTreeAsync(localRoot, maxFileSizeBytes, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                IReadOnlyList<SpellMetadata> result = MergeSpellMetadata(globalSpells, localSpells);

                if (ttlSeconds > 0)
                {
                    MetadataScanCache.Set(cacheKey, new MetadataScanCacheEntry(result, DateTimeOffset.UtcNow));
                }

                return result;

            }).ConfigureAwait(false);

        return merged;
    }

    /// <summary>
    /// Streams metadata from one catalog root while retaining only live traversal state. Callers
    /// choose their own bounded page representation; this method never builds a catalog-wide list.
    /// </summary>
    internal static async IAsyncEnumerable<SpellMetadata> StreamMetadataTreeAsync(
        string rootFullPath,
        long maxFileSizeBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(rootFullPath)
            || !Directory.Exists(rootFullPath))
        {

            yield break;

        }

        string root = Path.GetFullPath(rootFullPath);

        foreach (string filePath in EnumerateSpellFilesDepthFirst(
                     root,
                     cancellationToken))
        {

            cancellationToken.ThrowIfCancellationRequested();

            SpellMetadata? metadata = await TryParseSpellMetadataAsync(
                    filePath,
                    maxFileSizeBytes,
                    root,
                    cancellationToken)
                .ConfigureAwait(false);

            if (metadata is not null)
            {

                yield return metadata;

            }

        }

    }

    internal static async Task<ParsedSpell?> LoadFullAsync(
        string filePath,
        CancellationToken cancellationToken,
        long maxFileSizeBytes,
        int? maxDeclaredTools = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        long mtimeTicks;

        try
        {
            mtimeTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await TryParseSpellFileAsync(fullPath, maxFileSizeBytes, maxDeclaredTools, workspaceRootForRevalidation: null, cancellationToken).ConfigureAwait(false);
        }

        string cacheKey = $"{fullPath}|{mtimeTicks}";

        if (FullSpellCache.TryGetValue(cacheKey, out ParsedSpell? cached))
        {
            return cached;
        }

        // Single-flight the miss path: concurrent misses for the same (path, mtime) key share
        // one parse task. Only the leader parses and populates the LRU cache.
        ParsedSpell? parsed = await SingleFlight.CoalesceAsync(
            FullSpellInFlight,
            cacheKey,
            async () =>
            {

                ParsedSpell? result = await TryParseSpellFileAsync(
                    fullPath,
                    maxFileSizeBytes,
                    maxDeclaredTools,
                    workspaceRootForRevalidation: null,
                    cancellationToken).ConfigureAwait(false);

                if (result is not null)
                {
                    FullSpellCache.Set(cacheKey, result);
                }

                return result;

            }).ConfigureAwait(false);

        return parsed;
    }

    internal static async Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary>> ScanSummariesAsync(
        string? workspaceRoot,
        CancellationToken cancellationToken,
        long maxFileSizeBytes,
        int metadataScanCacheTtlSeconds = 0)
    {

        IReadOnlyList<SpellMetadata> metadata = await ScanMetadataAsync(
            workspaceRoot,
            cancellationToken,
            maxFileSizeBytes,
            metadataScanCacheTtlSeconds).ConfigureAwait(false);

        var summaries = new RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary[metadata.Count];

        for (int i = 0; i < metadata.Count; i++)
        {
            summaries[i] = MapMetadataToSummary(metadata[i]);
        }

        return summaries;
    }

    private static RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary MapMetadataToSummary(SpellMetadata metadata)
    {
        RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSource source = IsBuiltinSpellPath(metadata.FilePath)
            ? RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSource.Builtin
            : RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSource.Workspace;

        return new RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary(
            metadata.Name,
            string.IsNullOrEmpty(metadata.Description) ? null : metadata.Description,
            source,
            metadata.Tags ?? Array.Empty<string>(),
            Version: null,
            InputSchema: null,
            OutputSchema: null,
            DeclaredTools: metadata.Tools is { Length: > 0 } tools ? tools : null,
            Dependencies: null);
    }

    private static bool IsBuiltinSpellPath(string filePath)
    {
        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(ArcanumPaths.GlobalSpellsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        return WorkspacePathPolicy.IsPathUnderWorkspace(globalRoot, fullPath);
    }

    private static IReadOnlyList<SpellMetadata> MergeSpellMetadata(
        IReadOnlyList<SpellMetadata> globalSpells,
        IReadOnlyList<SpellMetadata> localSpells)
    {
        if (globalSpells.Count == 0)
        {
            return localSpells;
        }

        if (localSpells.Count == 0)
        {
            return globalSpells;
        }

        var localNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SpellMetadata localSpell in localSpells)
        {
            _ = localNameSet.Add(localSpell.Name);
        }

        var merged = new List<SpellMetadata>(globalSpells.Count + localSpells.Count);

        foreach (SpellMetadata globalSpell in globalSpells)
        {
            if (!localNameSet.Contains(globalSpell.Name))
            {
                merged.Add(globalSpell);
            }
        }

        merged.AddRange(localSpells);

        return merged;
    }

    private static IReadOnlyList<ParsedSpell> MergeSpells(IReadOnlyList<ParsedSpell> globalSpells, IReadOnlyList<ParsedSpell> localSpells)
    {
        if (globalSpells.Count == 0)
        {
            return localSpells;
        }

        if (localSpells.Count == 0)
        {
            return globalSpells;
        }

        var localNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ParsedSpell l in localSpells)
        {
            _ = localNameSet.Add(l.Name);
        }

        var merged = new List<ParsedSpell>(globalSpells.Count + localSpells.Count);

        foreach (ParsedSpell g in globalSpells)
        {
            if (!localNameSet.Contains(g.Name))
            {
                merged.Add(g);
            }
        }

        merged.AddRange(localSpells);

        return merged;
    }

    private static async Task<List<ParsedSpell>> ScanTreeAsync(
        string rootFullPath,
        long maxFileSizeBytes,
        int? maxDeclaredTools,
        CancellationToken cancellationToken)
    {
        var results = new List<ParsedSpell>();

        foreach (string filePath in EnumerateSpellFiles(rootFullPath, cancellationToken))
        {
            ParsedSpell? parsed = await TryParseSpellFileAsync(
                filePath,
                maxFileSizeBytes,
                maxDeclaredTools,
                rootFullPath,
                cancellationToken).ConfigureAwait(false);

            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    private static async Task<List<SpellMetadata>> ScanMetadataTreeAsync(
        string rootFullPath,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        var results = new List<SpellMetadata>();

        foreach (string filePath in EnumerateSpellFiles(rootFullPath, cancellationToken))
        {
            SpellMetadata? parsed = await TryParseSpellMetadataAsync(
                filePath,
                maxFileSizeBytes,
                rootFullPath,
                cancellationToken).ConfigureAwait(false);

            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    /// <summary>
    /// Cancellation-aware, cycle-safe breadth-first walk that yields every in-root <c>SPELL.md</c> path. Each
    /// directory is canonicalized (resolving symlink targets) and tracked in a visited set so a
    /// directory-symlink cycle terminates without imposing a total directory or depth ceiling.
    /// </summary>
    private static IEnumerable<string> EnumerateSpellFiles(string rootFullPath, CancellationToken cancellationToken)
    {
        string canonicalRoot = ResolveCanonicalDirectory(rootFullPath);

        var visitedCanonicalDirs = new HashSet<string>(CanonicalDirectoryComparer);

        _ = visitedCanonicalDirs.Add(canonicalRoot);

        var queue = new Queue<string>();

        queue.Enqueue(rootFullPath);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = queue.Dequeue();

            if (!WorkspacePathPolicy.IsPathUnderWorkspace(rootFullPath, currentDir))
            {
                continue;
            }

            foreach (string filePath in EnumerateDirectoryEntriesSafely(
                         currentDir,
                         directories: false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(Path.GetFileName(filePath), "SPELL.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!WorkspacePathPolicy.IsPathUnderWorkspace(rootFullPath, filePath))
                {
                    continue;
                }

                yield return filePath;
            }

            foreach (string subDir in EnumerateDirectoryEntriesSafely(
                         currentDir,
                         directories: true))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = Path.GetFileName(subDir);

                if (name.Length == 0 || name[0] == '.')
                {
                    continue;
                }

                if (HeavyDirectoryNames.Contains(name))
                {
                    continue;
                }

                string fullSub = Path.GetFullPath(subDir);

                if (!WorkspacePathPolicy.IsPathUnderWorkspace(rootFullPath, fullSub))
                {
                    continue;
                }

                string canonicalSub = ResolveCanonicalDirectory(fullSub);

                if (!WorkspacePathPolicy.IsPathUnderWorkspace(canonicalRoot, canonicalSub))
                {
                    continue;
                }

                if (!visitedCanonicalDirs.Add(canonicalSub))
                {
                    continue;
                }

                queue.Enqueue(fullSub);
            }
        }
    }

    private static IEnumerable<string> EnumerateSpellFilesDepthFirst(
        string rootFullPath,
        CancellationToken cancellationToken)
    {

        string canonicalRoot = ResolveCanonicalDirectory(rootFullPath);

        HashSet<string> visitedCanonicalDirectories = new(
            CanonicalDirectoryComparer)
        {
            canonicalRoot,
        };

        Stack<SpellDirectoryFrame> pending = new();

        if (!SpellDirectoryFrame.TryOpen(rootFullPath, out SpellDirectoryFrame? rootFrame))
        {

            yield break;

        }

        pending.Push(rootFrame!);

        try
        {

            while (pending.Count > 0)
            {

                cancellationToken.ThrowIfCancellationRequested();

                SpellDirectoryFrame frame = pending.Peek();

                if (!frame.TryTakeNext(out string? entry))
                {

                    pending.Pop().Dispose();

                    continue;

                }

                string currentEntry = entry!;

                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;

                try
                {

                    attributes = File.GetAttributes(currentEntry);

                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or FileNotFoundException)
                {

                    continue;

                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {

                    string name = Path.GetFileName(currentEntry);

                    if (name.Length == 0
                        || name[0] == '.'
                        || HeavyDirectoryNames.Contains(name))
                    {

                        continue;

                    }

                    string fullDirectory;

                    try
                    {

                        fullDirectory = Path.GetFullPath(currentEntry);

                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or ArgumentException)
                    {

                        continue;

                    }

                    if (!WorkspacePathPolicy.IsPathUnderWorkspace(
                            rootFullPath,
                            fullDirectory))
                    {

                        continue;

                    }

                    string canonicalDirectory = ResolveCanonicalDirectory(
                        fullDirectory);

                    if (!WorkspacePathPolicy.IsPathUnderWorkspace(
                            canonicalRoot,
                            canonicalDirectory)
                        || !visitedCanonicalDirectories.Add(canonicalDirectory)
                        || !SpellDirectoryFrame.TryOpen(
                            fullDirectory,
                            out SpellDirectoryFrame? childFrame))
                    {

                        continue;

                    }

                    pending.Push(childFrame!);

                    continue;

                }

                if (string.Equals(
                        Path.GetFileName(currentEntry),
                        "SPELL.md",
                        StringComparison.OrdinalIgnoreCase)
                    && WorkspacePathPolicy.IsPathUnderWorkspace(
                        rootFullPath,
                        currentEntry))
                {

                    yield return currentEntry;

                }

            }

        }
        finally
        {

            while (pending.TryPop(out SpellDirectoryFrame? frame))
            {

                frame!.Dispose();

            }

        }

    }

    private sealed class SpellDirectoryFrame(
        IEnumerator<string> entries) : IDisposable
    {

        internal static bool TryOpen(
            string directory,
            out SpellDirectoryFrame? frame)
        {

            frame = null;

            try
            {

                frame = new SpellDirectoryFrame(
                    Directory
                        .EnumerateFileSystemEntries(directory)
                        .GetEnumerator());

                return true;

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
            {

                return false;

            }

        }

        internal bool TryTakeNext(out string? entry)
        {

            try
            {

                if (entries.MoveNext())
                {

                    entry = entries.Current;

                    return true;

                }

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
            {

            }

            entry = null;

            return false;

        }

        public void Dispose() => entries.Dispose();

    }

    private static IEnumerable<string> EnumerateDirectoryEntriesSafely(
        string directory,
        bool directories)
    {
        IEnumerable<string>? entries = null;

        try
        {
            entries = directories
                ? Directory.EnumerateDirectories(directory)
                : Directory.EnumerateFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (entries is null)
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;

        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (enumerator is null)
        {
            yield break;
        }

        using (enumerator)
        {

            while (true)
            {
                bool hasNext;
                string? entry;

                try
                {
                    hasNext = enumerator.MoveNext();
                    entry = hasNext ? enumerator.Current : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    hasNext = false;
                    entry = null;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return entry!;
            }
        }
    }

    /// <summary>
    /// Resolves a directory to a canonical path, following a symlink to its final target when present
    /// (<see cref="Directory.ResolveLinkTarget(string, bool)"/>), falling back to the lexical full path.
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

    private static async Task<SpellMetadata?> TryParseSpellMetadataAsync(
        string filePath,
        long maxFileSizeBytes,
        string? workspaceRootForRevalidation,
        CancellationToken cancellationToken)
    {
        if (workspaceRootForRevalidation is not null
            && !WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRootForRevalidation, filePath))
        {
            return null;
        }

        if (!TryGetFileLength(filePath, out long fileLength) || ExceedsMaxFileSize(fileLength, maxFileSizeBytes))
        {
            return null;
        }

        string directoryFallbackName = GetSpellDirectoryFallbackName(filePath);

        string? frontmatterBlock = await ReadFrontmatterBlockAsync(
            filePath,
            fileLength,
            maxFileSizeBytes,
            cancellationToken).ConfigureAwait(false);

        if (frontmatterBlock is null)
        {
            return new SpellMetadata(directoryFallbackName, string.Empty, filePath);
        }

        SpellParseResult parsed = SpellFileParser.Parse(frontmatterBlock, directoryFallbackName);

        if (SpellFrontmatterValidator.ValidateParsed(parsed) is not null)
        {

            return null;

        }

        return new SpellMetadata(parsed.Name, parsed.Description, filePath, parsed.Tags, parsed.Tools);
    }

    private static async Task<string?> ReadFrontmatterBlockAsync(
        string filePath,
        long fileLength,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        if (ExceedsMaxFileSize(fileLength, maxFileSizeBytes))
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            using StreamReader reader = new(stream);

            long bytesRead = 0L;

            string? firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (firstLine is null || !firstLine.Trim().Equals("---", StringComparison.Ordinal))
            {
                return null;
            }

            var block = new StringBuilder();

            _ = block.AppendLine(firstLine);

            bytesRead += Encoding.UTF8.GetByteCount(firstLine) + 1;

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                bytesRead += Encoding.UTF8.GetByteCount(line) + 1;

                if (bytesRead > maxFileSizeBytes)
                {
                    return null;
                }

                _ = block.AppendLine(line);

                if (line.Trim().Equals("---", StringComparison.Ordinal))
                {
                    break;
                }
            }

            return block.ToString();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<ParsedSpell?> TryParseSpellFileAsync(
        string filePath,
        long maxFileSizeBytes,
        int? maxDeclaredTools,
        string? workspaceRootForRevalidation,
        CancellationToken cancellationToken)
    {
        if (workspaceRootForRevalidation is not null
            && !WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRootForRevalidation, filePath))
        {
            return null;
        }

        if (!TryGetFileLength(filePath, out long fileLength) || ExceedsMaxFileSize(fileLength, maxFileSizeBytes))
        {
            return null;
        }

        string fullText;

        try
        {
            fullText = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(fullText) > maxFileSizeBytes)
        {
            return null;
        }

        string directoryFallbackName = GetSpellDirectoryFallbackName(filePath);

        SpellParseResult parsed = SpellFileParser.Parse(fullText, directoryFallbackName);

        if (SpellFrontmatterValidator.ValidateParsed(parsed) is not null)
        {

            return null;

        }

        string spellDirectoryPath = string.Empty;

        try
        {
            string? dir = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(dir))
            {
                spellDirectoryPath = Path.GetFullPath(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            spellDirectoryPath = string.Empty;
        }

        IReadOnlyList<string> availableScripts = spellDirectoryPath.Length > 0
            ? DiscoverAvailableScripts(spellDirectoryPath, cancellationToken)
            : Array.Empty<string>();

        SkillMetadata? skillMetadata = null;

        string[] mergedTags = parsed.Tags;

        if (spellDirectoryPath.Length > 0)
        {
            string? sidecarPath = SkillJsonIO.ResolveSidecarPath(spellDirectoryPath);

            if (sidecarPath is not null
                && (workspaceRootForRevalidation is null
                    || WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRootForRevalidation, sidecarPath))
                && TryGetFileLength(sidecarPath, out long sidecarLength)
                && !ExceedsMaxFileSize(sidecarLength, maxFileSizeBytes))
            {
                try
                {
                    string sidecarText = await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false);

                    skillMetadata = JsonSerializer.Deserialize(sidecarText, TheForgeJsonContext.Default.SkillMetadata);

                    if (skillMetadata is not null
                        && SkillJsonBoundsValidator.Validate(
                            skillMetadata,
                            ArcanumSettingClamps.MaxDeclaredTools(
                                maxDeclaredTools ?? ArcanumRuntimeDefaults.Spells.MaxDeclaredTools)) is not null)
                    {

                        skillMetadata = null;

                    }

                    if (skillMetadata is not null)
                    {
                        mergedTags = MergeTags(parsed.Tags, skillMetadata.Tags);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        return new ParsedSpell(
            parsed.Name,
            parsed.Description,
            filePath,
            fullText,
            spellDirectoryPath,
            availableScripts)
        {
            Tags = mergedTags,
            SystemPrompt = parsed.SystemPrompt,
            Template = parsed.Template,
            Model = parsed.Model,
            Provider = parsed.Provider,
            Tools = parsed.Tools,
            RequiredMcpServers = parsed.RequiredMcpServers,
            Body = parsed.Body,
            SkillMetadata = skillMetadata,
        };
    }

    private static string[] MergeTags(string[] frontmatterTags, List<string> skillTags)
    {
        if (skillTags.Count == 0)
        {
            return frontmatterTags;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string tag in frontmatterTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _ = set.Add(tag.Trim());
            }
        }

        foreach (string tag in skillTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                _ = set.Add(tag.Trim());
            }
        }

        string[] result = set.ToArray();

        Array.Sort(result, StringComparer.OrdinalIgnoreCase);

        return result;
    }

    private static IReadOnlyList<string> DiscoverAvailableScripts(string spellDirectoryFullPath, CancellationToken cancellationToken)
    {
        string scriptsDir = Path.Combine(spellDirectoryFullPath, "scripts");

        if (!Directory.Exists(scriptsDir))
        {
            return Array.Empty<string>();
        }

        try
        {
            var names = new List<string>();

            foreach (string path in Directory.EnumerateFiles(scriptsDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                names.Add(Path.GetFileName(path));
            }

            names.Sort(StringComparer.Ordinal);

            return names;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryGetFileLength(string filePath, out long length)
    {
        try
        {
            length = new FileInfo(filePath).Length;

            return true;
        }
        catch (IOException)
        {
            length = 0L;

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            length = 0L;

            return false;
        }
    }

    private static bool ExceedsMaxFileSize(long fileLength, long maxFileSizeBytes) =>
        fileLength > maxFileSizeBytes;

    private static bool ExceedsMaxFileSize(string filePath, long maxFileSizeBytes) =>
        TryGetFileLength(filePath, out long length) && ExceedsMaxFileSize(length, maxFileSizeBytes);

    private static string GetSpellDirectoryFallbackName(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);

        if (string.IsNullOrEmpty(dir))
        {
            return string.Empty;
        }

        string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.GetFileName(trimmed);
    }

}
