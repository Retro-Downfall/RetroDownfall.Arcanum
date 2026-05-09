using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Pattern;

public sealed class EyeOfTheWorldService(IOptions<ArcanumSettings> settings) : IEyeOfTheWorld
{

    private readonly int _maxEnumerationSteps = ArcanumSettingClamps.MaxEnumerationSteps(
        settings.Value.Perception.MaxEnumerationSteps);

    private readonly int _maxTocLines = ArcanumSettingClamps.MaxTableOfContentsLines(
        settings.Value.Perception.MaxTableOfContentsLines);

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

    public async Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new PatternSnapshot(
                DomainType.Unknown,
                string.Empty,
                ["Path: (empty or invalid)"]);
        }

        string root = Path.GetFullPath(directoryPath);

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

    private ScanResult ScanWorkspace(string root, CancellationToken cancellationToken)
    {
        ScanResult result = new();
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        try
        {
            foreach (string fullPath in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.EnumerationSteps >= _maxEnumerationSteps)
                {
                    result.EnumerationTruncated = true;
                    break;
                }
                result.EnumerationSteps++;
                if (IsUnderIgnoredPath(fullPath, root))
                {
                    continue;
                }

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

    private static void AccumulateFileTimes(ScanResult result, string rel, string fullPath)
    {
        try
        {
            DateTime lw = File.GetLastWriteTimeUtc(fullPath);
            DateTime created = File.GetCreationTimeUtc(fullPath);
            result.AllFiles.Add(new FileRec(rel, lw, created));
        }
        catch
        {
            // Skip files we cannot stat.
        }
    }

    private static void AccumulateSignature(ScanResult scan, string rel, string ext, string fileName, int depth)
    {
        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            scan.Solutions.Add(rel);
            return;
        }

        if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            scan.Solutions.Add(rel);
            return;
        }

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
        {
            scan.Projects.Add(rel);
            return;
        }

        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            scan.Packages.Add(rel);
            return;
        }

        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            scan.Dockerfiles.Add(rel);
            return;
        }

        if (fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
        {
            scan.OtherMarkers.Add(rel);
            return;
        }

        if (fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            scan.OtherMarkers.Add(rel);
            return;
        }

        if (fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            scan.OtherMarkers.Add(rel);
            return;
        }

        if (fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
        {
            scan.OtherMarkers.Add(rel);
            return;
        }

        if (depth <= 2 && IsOfficeExtension(ext))
        {
            scan.AdminNear.Add(rel);
            return;
        }

        if (depth <= 2 && (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            scan.NotesNear.Add(rel);
        }
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

        if (domain == DomainType.SoftwareEngineering && lines.Count < _maxTocLines)
        {
            AddBucket(scan.AdminNear, "Document: ");
            AddBucket(scan.NotesNear, "Note: ");
        }

        List<string> deduped = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int lineBudget = scan.EnumerationTruncated ? _maxTocLines - 1 : _maxTocLines;

        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key = line[(line.IndexOf(':') + 1)..].TrimStart();

            if (!seen.Add(key))
            {
                continue;
            }

            deduped.Add(line);

            if (deduped.Count >= lineBudget)
            {
                break;
            }
        }

        if (scan.EnumerationTruncated)
        {
            deduped.Add($"Scan: truncated after {_maxEnumerationSteps} files");
        }

        return [.. deduped];
    }

    private string[] BuildUnknownToc(ScanResult scan, CancellationToken cancellationToken)
    {
        if (scan.AllFiles.Count == 0)
        {
            return scan.EnumerationTruncated
                ? [$"Scan: truncated after {_maxEnumerationSteps} files"]
                : ["File: (no files enumerated)"];
        }

        List<FileRec> sorted = [.. scan.AllFiles];

        sorted.Sort(static (a, b) =>
        {
            int c = b.LastWriteUtc.CompareTo(a.LastWriteUtc);
            return c != 0 ? c : b.CreationUtc.CompareTo(a.CreationUtc);
        });

        List<string> lines = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int fileBudget = scan.EnumerationTruncated ? _maxTocLines - 1 : _maxTocLines;

        foreach (FileRec rec in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seen.Add(rec.RelativePath))
            {
                continue;
            }

            lines.Add($"File: {rec.RelativePath}");

            if (lines.Count >= fileBudget)
            {
                break;
            }
        }

        if (scan.EnumerationTruncated)
        {
            lines.Add($"Scan: truncated after {_maxEnumerationSteps} files");
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

    private static bool IsUnderIgnoredPath(string fullPath, string root)
    {
        string rel = Path.GetRelativePath(root, fullPath);
        foreach (string part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IgnoredDirectorySegments.Contains(part))
            {
                return true;
            }
        }

        return false;
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
        public int EnumerationSteps { get; set; }
        public bool EnumerationTruncated { get; set; }
    }

    private readonly record struct FileRec(string RelativePath, DateTime LastWriteUtc, DateTime CreationUtc);
}
