using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Infrastructure.Workspace;

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

    internal static async Task<IReadOnlyList<ParsedSpell>> ScanAsync(
        string? workspaceRoot,
        CancellationToken cancellationToken,
        long maxFileSizeBytes)
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
                () => ScanTreeAsync(globalRoot, maxFileSizeBytes, cancellationToken),
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
                    () => ScanTreeAsync(localRoot, maxFileSizeBytes, cancellationToken),
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

        IReadOnlyList<SpellMetadata> merged = MergeSpellMetadata(globalSpells, localSpells);

        if (ttlSeconds > 0)
        {
            MetadataScanCache.Set(cacheKey, new MetadataScanCacheEntry(merged, DateTimeOffset.UtcNow));
        }

        return merged;
    }

    internal static async Task<ParsedSpell?> LoadFullAsync(
        string filePath,
        CancellationToken cancellationToken,
        long maxFileSizeBytes)
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
            return await TryParseSpellFileAsync(fullPath, maxFileSizeBytes, cancellationToken).ConfigureAwait(false);
        }

        string cacheKey = $"{fullPath}|{mtimeTicks}";

        if (FullSpellCache.TryGetValue(cacheKey, out ParsedSpell? cached))
        {
            return cached;
        }

        ParsedSpell? parsed = await TryParseSpellFileAsync(fullPath, maxFileSizeBytes, cancellationToken).ConfigureAwait(false);

        if (parsed is not null)
        {
            FullSpellCache.Set(cacheKey, parsed);
        }

        return parsed;
    }

    internal static async Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary>> ScanSummariesAsync(
        string? workspaceRoot,
        CancellationToken cancellationToken,
        long maxFileSizeBytes)
    {
        IReadOnlyList<SpellMetadata> metadata = await ScanMetadataAsync(workspaceRoot, cancellationToken, maxFileSizeBytes).ConfigureAwait(false);

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

        return IsPathUnderWorkspaceRoot(globalRoot, fullPath);
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
        CancellationToken cancellationToken)
    {
        var results = new List<ParsedSpell>();

        var queue = new Queue<string>();

        queue.Enqueue(rootFullPath);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = queue.Dequeue();

            if (!IsPathUnderWorkspaceRoot(rootFullPath, currentDir))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.Equals(Path.GetFileName(filePath), "SPELL.md", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsPathUnderWorkspaceRoot(rootFullPath, filePath))
                    {
                        continue;
                    }

                    ParsedSpell? parsed = await TryParseSpellFileAsync(filePath, maxFileSizeBytes, cancellationToken).ConfigureAwait(false);

                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }

                foreach (string subDir in Directory.EnumerateDirectories(currentDir).OrderBy(p => p, StringComparer.Ordinal))
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

                    if (!IsPathUnderWorkspaceRoot(rootFullPath, fullSub))
                    {
                        continue;
                    }

                    queue.Enqueue(fullSub);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
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

        var queue = new Queue<string>();

        queue.Enqueue(rootFullPath);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = queue.Dequeue();

            if (!IsPathUnderWorkspaceRoot(rootFullPath, currentDir))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.Equals(Path.GetFileName(filePath), "SPELL.md", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsPathUnderWorkspaceRoot(rootFullPath, filePath))
                    {
                        continue;
                    }

                    SpellMetadata? parsed = await TryParseSpellMetadataAsync(filePath, maxFileSizeBytes, cancellationToken).ConfigureAwait(false);

                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }

                foreach (string subDir in Directory.EnumerateDirectories(currentDir).OrderBy(p => p, StringComparer.Ordinal))
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

                    if (!IsPathUnderWorkspaceRoot(rootFullPath, fullSub))
                    {
                        continue;
                    }

                    queue.Enqueue(fullSub);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return results;
    }

    private static bool IsPathUnderWorkspaceRoot(string workspaceRootFull, string candidateFull)
    {
        char sep = Path.DirectorySeparatorChar;

        string normalizedRoot = workspaceRootFull.TrimEnd(sep);

        string prefix = normalizedRoot + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidateFull.Equals(normalizedRoot, cmp) || candidateFull.StartsWith(prefix, cmp);
    }

    private static async Task<SpellMetadata?> TryParseSpellMetadataAsync(
        string filePath,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
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
        CancellationToken cancellationToken)
    {
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
            string skillJsonPath = Path.Combine(spellDirectoryPath, "SKILL.json");

            if (File.Exists(skillJsonPath)
                && TryGetFileLength(skillJsonPath, out long skillJsonLength)
                && !ExceedsMaxFileSize(skillJsonLength, maxFileSizeBytes))
            {
                try
                {
                    string skillJsonText = await File.ReadAllTextAsync(skillJsonPath, cancellationToken).ConfigureAwait(false);

                    skillMetadata = JsonSerializer.Deserialize(skillJsonText, TheForgeJsonContext.Default.SkillMetadata);

                    if (skillMetadata is not null
                        && SkillJsonBoundsValidator.Validate(
                            skillMetadata,
                            ArcanumSettingClamps.MaxDependencies(new SpellSettings().MaxDependencies),
                            ArcanumSettingClamps.MaxDeclaredTools(new SpellSettings().MaxDeclaredTools)) is not null)
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
