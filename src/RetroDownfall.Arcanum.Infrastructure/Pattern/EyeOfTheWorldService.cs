using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Pattern;

public sealed class EyeOfTheWorldService : IEyeOfTheWorld
{

    public EyeOfTheWorldService()
    {
    }

    private int MaxTocLines =>
        ArcanumSettingClamps.MaxTableOfContentsLines(
            ArcanumRuntimeDefaults.Perception.MaxTableOfContentsLines);

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
    /// Path comparison for the visited canonical-directory set that terminates symlink cycles.
    /// </summary>
    private static readonly StringComparer CanonicalDirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new PatternSnapshot(
                DomainType.Unknown,
                string.Empty,
                ["Path: (empty or invalid)"]);
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

        if (!Directory.Exists(root))
        {
            return new PatternSnapshot(
                DomainType.Unknown,
                root,
                [$"Path: directory not found ({root})"]);
        }

        return await Task.Run(() =>
        {
            ScanResult scan = ScanWorkspace(root, cancellationToken);

            DomainType domain = ClassifyDomain(scan);

            string[] threads = domain == DomainType.Unknown
                ? BuildUnknownToc(scan, cancellationToken)
                : BuildSignatureToc(scan, domain, cancellationToken);

            return new PatternSnapshot(domain, root, threads);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Breadth-first walk that prunes <see cref="IgnoredDirectorySegments"/> and symlink-escaping
    /// subdirectories <b>before</b> descending (same shape as
    /// <c>WorkspaceIndexingService.EnumerateCandidateFiles</c>), terminates symlink cycles via a
    /// canonical visited set, ignores inaccessible directories, and continues until the contained
    /// workspace has been visited or the caller cancels. Result projections remain bounded.
    /// </summary>
    private ScanResult ScanWorkspace(string root, CancellationToken cancellationToken)
    {
        ScanResult result = new();

        string canonicalRoot = ResolveCanonicalDirectory(root);

        HashSet<string> visitedCanonicalDirs = new(CanonicalDirectoryComparer) { canonicalRoot };

        Queue<string> pendingDirectories = new();

        pendingDirectories.Enqueue(root);

        try
        {
            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string directory = pendingDirectories.Dequeue();

                IEnumerable<string> entries;

                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Inaccessible directory — skip and keep walking (IgnoreInaccessible parity).
                    continue;
                }

                foreach (string fullPath in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

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
                            // Pruned before recursion — contents are never visited.
                            continue;
                        }

                        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, fullPath, out _))
                        {
                            // Escaping symlinked directory — never descended into.
                            continue;
                        }

                        string canonicalSub = ResolveCanonicalDirectory(fullPath);

                        if (!visitedCanonicalDirs.Add(canonicalSub))
                        {
                            // Symlink cycle — already visited this canonical directory.
                            continue;
                        }

                        pendingDirectories.Enqueue(fullPath);
                    }
                    else
                    {
                        string rel = Path.GetRelativePath(root, fullPath);

                        if (rel is "." or "..")
                        {
                            continue;
                        }

                        string ext = Path.GetExtension(fullPath);

                        string fileName = Path.GetFileName(fullPath);

                        int depth = CountPathSegments(rel);

                        AccumulateFileTimes(result, rel, fullPath);

                        AccumulateSignature(scan: result, rel, ext, fileName, depth);

                        AccumulateDomainCounts(scan: result, ext);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore per-file / permission issues at enumeration level; partial scan still useful.
        }

        return result;
    }

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

    private void AccumulateFileTimes(ScanResult result, string rel, string fullPath)
    {
        try
        {
            DateTime lw = File.GetLastWriteTimeUtc(fullPath);

            DateTime created = File.GetCreationTimeUtc(fullPath);

            FileRec candidate = new(rel, lw, created);

            if (result.AllFiles.Count < MaxTocLines)
            {
                result.AllFiles.Add(candidate);

                return;
            }

            int oldestIndex = 0;

            for (int index = 1; index < result.AllFiles.Count; index++)
            {
                if (CompareRecency(result.AllFiles[index], result.AllFiles[oldestIndex]) < 0)
                {
                    oldestIndex = index;
                }
            }

            if (CompareRecency(candidate, result.AllFiles[oldestIndex]) > 0)
            {
                result.AllFiles[oldestIndex] = candidate;
            }
        }
        catch
        {
            // Skip files we cannot stat.
        }
    }

    private void AccumulateSignature(ScanResult scan, string rel, string ext, string fileName, int depth)
    {
        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.Solutions, rel);

            return;
        }

        if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.Solutions, rel);

            return;
        }

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.Projects, rel);

            return;
        }

        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.Packages, rel);

            return;
        }

        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.Dockerfiles, rel);

            return;
        }

        if (fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.OtherMarkers, rel);

            return;
        }

        if (fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.OtherMarkers, rel);

            return;
        }

        if (fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.OtherMarkers, rel);

            return;
        }

        if (fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
        {
            AddBoundedPath(scan.OtherMarkers, rel);

            return;
        }

        if (depth <= 2 && IsOfficeExtension(ext))
        {
            AddBoundedPath(scan.AdminNear, rel);

            return;
        }

        if (depth <= 2 && (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            AddBoundedPath(scan.NotesNear, rel);
        }
    }

    private void AddBoundedPath(List<string> paths, string candidate)
    {
        if (paths.Count < MaxTocLines)
        {
            paths.Add(candidate);

            return;
        }

        int greatestIndex = 0;

        for (int index = 1; index < paths.Count; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Compare(paths[index], paths[greatestIndex]) > 0)
            {
                greatestIndex = index;
            }
        }

        if (StringComparer.OrdinalIgnoreCase.Compare(candidate, paths[greatestIndex]) < 0)
        {
            paths[greatestIndex] = candidate;
        }
    }

    private static int CompareRecency(FileRec left, FileRec right)
    {
        int modified = left.LastWriteUtc.CompareTo(right.LastWriteUtc);

        if (modified != 0)
        {
            return modified;
        }

        int created = left.CreationUtc.CompareTo(right.CreationUtc);

        return created != 0
            ? created
            : StringComparer.OrdinalIgnoreCase.Compare(right.RelativePath, left.RelativePath);
    }

    private static void AccumulateDomainCounts(ScanResult scan, string ext)
    {
        if (IsOfficeExtension(ext))
        {
            scan.OfficeFileCount++;
            return;
        }

        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            scan.ProseFileCount++;
            return;
        }

        if (IsDevSourceExtension(ext))
        {
            scan.DevSourceFileCount++;
        }
    }

    private static bool IsOfficeExtension(string ext) =>
        ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".xls", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase);
    private static bool IsDevSourceExtension(string ext) =>
        ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".py", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".java", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".go", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".rs", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".php", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".cxx", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".cc", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".c", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".h", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".fs", StringComparison.OrdinalIgnoreCase);
    private static DomainType ClassifyDomain(ScanResult scan)
    {
        bool hasSoftwareArtifact = scan.Solutions.Count > 0
            || scan.Projects.Count > 0
            || scan.Packages.Count > 0
            || scan.Dockerfiles.Count > 0
            || scan.OtherMarkers.Count > 0;
        if (hasSoftwareArtifact)
        {
            return DomainType.SoftwareEngineering;
        }

        if (scan.DevSourceFileCount >= 25)
        {
            return DomainType.SoftwareEngineering;
        }

        if (scan.OfficeFileCount >= 3 && scan.OfficeFileCount >= scan.ProseFileCount)
        {
            return DomainType.Administration;
        }

        if (scan.ProseFileCount >= 4 && scan.ProseFileCount > scan.OfficeFileCount)
        {
            return DomainType.Research;
        }

        return DomainType.Unknown;
    }

    private string[] BuildSignatureToc(ScanResult scan, DomainType domain, CancellationToken cancellationToken)
    {
        List<string> lines = [];

        void AddBucket(List<string> paths, string labelPrefix)
        {
            foreach (string rel in paths.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                lines.Add($"{labelPrefix}{rel}");
            }
        }

        AddBucket(scan.Solutions, "Solution: ");
        AddBucket(scan.Projects, "Project: ");
        AddBucket(scan.Packages, "Package: ");
        AddBucket(scan.Dockerfiles, "Dockerfile: ");
        AddBucket(scan.OtherMarkers, "Manifest: ");

        if (domain == DomainType.Administration || domain == DomainType.Research)
        {
            AddBucket(scan.AdminNear, "Document: ");
        }

        if (domain == DomainType.Research)
        {
            AddBucket(scan.NotesNear, "Note: ");
        }

        if (domain == DomainType.SoftwareEngineering && lines.Count < MaxTocLines)
        {
            AddBucket(scan.AdminNear, "Document: ");
            AddBucket(scan.NotesNear, "Note: ");
        }

        List<string> deduped = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key = line[(line.IndexOf(':') + 1)..].TrimStart();

            if (!seen.Add(key))
            {
                continue;
            }

            deduped.Add(line);

            if (deduped.Count >= MaxTocLines)
            {
                break;
            }
        }

        return [.. deduped];
    }

    private string[] BuildUnknownToc(ScanResult scan, CancellationToken cancellationToken)
    {
        if (scan.AllFiles.Count == 0)
        {
            return ["File: (no files enumerated)"];
        }

        List<FileRec> sorted = [.. scan.AllFiles];

        sorted.Sort(static (a, b) =>
        {
            int c = b.LastWriteUtc.CompareTo(a.LastWriteUtc);

            if (c != 0)
            {
                return c;
            }

            c = b.CreationUtc.CompareTo(a.CreationUtc);

            return c != 0
                ? c
                : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });

        List<string> lines = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileRec rec in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(rec.RelativePath))
            {
                continue;
            }

            lines.Add($"File: {rec.RelativePath}");

            if (lines.Count >= MaxTocLines)
            {
                break;
            }
        }

        return [.. lines];
    }

    private static int CountPathSegments(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return 0;
        }

        ReadOnlySpan<char> span = relativePath.AsSpan();
        int count = 1;
        foreach (char c in span)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class ScanResult
    {
        public List<string> Solutions { get; } = [];
        public List<string> Projects { get; } = [];
        public List<string> Packages { get; } = [];
        public List<string> Dockerfiles { get; } = [];
        public List<string> OtherMarkers { get; } = [];
        public List<string> AdminNear { get; } = [];
        public List<string> NotesNear { get; } = [];
        public List<FileRec> AllFiles { get; } = [];
        public int OfficeFileCount { get; set; }
        public int ProseFileCount { get; set; }
        public int DevSourceFileCount { get; set; }
    }

    private readonly record struct FileRec(string RelativePath, DateTime LastWriteUtc, DateTime CreationUtc);
}
