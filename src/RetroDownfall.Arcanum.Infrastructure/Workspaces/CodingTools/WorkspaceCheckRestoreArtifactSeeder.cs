using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckRestoreSeedOptions(
    int MaxProjects,
    int MaxFiles,
    long MaxBytes)
{

    internal static WorkspaceCheckRestoreSeedOptions Default { get; } =
        new(
            MaxProjects: 128,
            MaxFiles: 640,
            MaxBytes: 64L * 1024L * 1024L);
}

internal sealed record WorkspaceCheckRestoreSeedResult(
    bool Success,
    string? Code,
    string? Message,
    int ProjectCount,
    int FileCount,
    long ByteCount,
    IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
        InputFingerprints);

internal sealed record WorkspaceCheckRestoreInputFingerprint(
    string Path,
    FileHandleIdentity Identity,
    long Length,
    long LastWriteUtcTicks,
    string Sha256);

internal static class WorkspaceCheckArtifactsLayout
{

    /// <summary>
    /// .NET 10 <c>--artifacts-path</c> evaluates MSBuildProjectExtensionsPath as
    /// <c>{artifacts}/obj/{ArtifactsProjectName}/</c>.
    /// </summary>
    internal static string ProjectIntermediateRoot(
        string artifactsRoot,
        string projectPath) =>
        Path.Combine(
            artifactsRoot,
            "obj",
            Path.GetFileNameWithoutExtension(projectPath));
}

/// <summary>
/// Seeds only pre-existing, validated NuGet restore products into the .NET 10 artifacts layout.
/// The operation never restores, downloads packages, or writes to the source workspace.
/// </summary>
internal static class WorkspaceCheckRestoreArtifactSeeder
{

    private static readonly string[] ProjectExtensions =
    [
        ".csproj",
        ".fsproj",
        ".vbproj",
    ];

    private static readonly string[] AncestorInputs =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.Config",
        "nuget.config",
        "global.json",
    ];

    internal static async Task<WorkspaceCheckRestoreSeedResult> SeedAsync(
        string workspaceRoot,
        string artifactsRoot,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxProjects < 1
            || options.MaxFiles < 1
            || options.MaxBytes < 1)
        {

            return Failure(
                "invalid_seed_limits",
                "Restore-artifact seed limits must be positive.");
        }

        string? workspace = CanonicalizeExistingDirectory(workspaceRoot);
        string? destinationRoot = CanonicalizeExistingDirectory(artifactsRoot);

        if (workspace is null
            || destinationRoot is null
            || WorkspaceRootPath.IsWithinOrEqual(
                destinationRoot,
                workspace))
        {

            return Failure(
                "invalid_seed_root",
                "Restore artifacts require an existing output root outside the source workspace.");
        }

        List<string> projects;

        try
        {

            projects = [];
            foreach (string path in Directory.EnumerateFiles(
                         workspace,
                         "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = false,
                             AttributesToSkip =
                                 FileAttributes.ReparsePoint
                                 | FileAttributes.Device,
                             ReturnSpecialDirectories = false,
                         }))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (!ProjectExtensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase)
                    || HasIgnoredDirectory(workspace, path))
                {

                    continue;

                }

                projects.Add(path);

                if (projects.Count > options.MaxProjects)
                {

                    return Failure(
                        "seed_cap_exceeded",
                        $"Restore-artifact seeding found more than {options.MaxProjects} projects.");

                }

            }

            projects.Sort(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {

            return Failure(
                "restore_required",
                "Project discovery could not safely inspect the workspace restore artifacts.");
        }

        HashSet<string> artifactProjectNames =
            new(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        int files = 0;
        long bytes = 0;
        Dictionary<string, WorkspaceCheckRestoreInputFingerprint>
            allRestoreInputs = new(PathComparer);
        long restoreInputBytes = 0;

        foreach (string project in projects)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string projectName = Path.GetFileNameWithoutExtension(project);

            if (!artifactProjectNames.Add(projectName))
            {

                return Failure(
                    "restore_required",
                    $"Multiple projects map to the .NET artifacts project name '{projectName}'.");
            }

            if (!TryValidateContainedRegularFile(
                    project,
                    workspace,
                    out FileHandleMetadata projectMetadata))
            {

                return Failure(
                    "restore_required",
                    "A project file could not be identity-validated inside the workspace.");
            }

            string projectDirectory = Path.GetDirectoryName(project)!;
            string sourceObj = Path.Combine(projectDirectory, "obj");
            string projectFileName = Path.GetFileName(project);
            string[] artifactNames =
            [
                "project.assets.json",
                projectFileName + ".nuget.g.props",
                projectFileName + ".nuget.g.targets",
                projectFileName + ".nuget.dgspec.json",
                "project.nuget.cache",
            ];
            if (!TryCaptureRestoreInputs(
                    workspace,
                    project,
                    projectDirectory,
                    cancellationToken,
                    out IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
                        restoreInputs,
                    out DateTime newestInput))
            {

                return Failure(
                    "restore_required",
                    $"Project '{projectName}' restore inputs could not be safely fingerprinted.");
            }

            foreach (WorkspaceCheckRestoreInputFingerprint input
                     in restoreInputs)
            {
                if (allRestoreInputs.TryGetValue(
                        input.Path,
                        out WorkspaceCheckRestoreInputFingerprint?
                            existing)
                    && existing != input)
                {
                    return Failure(
                        "restore_required",
                        $"Project '{projectName}' restore inputs produced inconsistent fingerprints.");
                }

                if (!allRestoreInputs.ContainsKey(input.Path))
                {
                    restoreInputBytes = checked(
                        restoreInputBytes + input.Length);

                    if (allRestoreInputs.Count >= 256
                        || restoreInputBytes
                        > 16L * 1024L * 1024L)
                    {
                        return Failure(
                            "seed_cap_exceeded",
                            "Restore-input fingerprinting exceeded its global file or byte cap.");
                    }
                }

                allRestoreInputs[input.Path] = input;
            }

            List<SeedFile> seedFiles = [];

            foreach (string artifactName in artifactNames)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string source = Path.Combine(sourceObj, artifactName);

                if (!TryValidateContainedRegularFile(
                        source,
                        workspace,
                        out FileHandleMetadata metadata))
                {

                    return Failure(
                        "restore_required",
                        $"Project '{projectName}' is missing validated pre-existing restore artifacts.");
                }

                FileInfo info = new(source);

                files++;
                bytes = checked(bytes + info.Length);

                if (files > options.MaxFiles || bytes > options.MaxBytes)
                {

                    return Failure(
                        "seed_cap_exceeded",
                        "Restore-artifact seeding exceeded its file or byte cap.");
                }

                if (info.LastWriteTimeUtc < newestInput
                    || !ValidateArtifactContents(
                        source,
                        artifactName,
                        info.Length))
                {

                    return Failure(
                        "restore_required",
                        $"Project '{projectName}' restore artifacts are stale or invalid.");
                }

                string? contentHash = ComputeSha256(
                    source,
                    metadata.Identity,
                    info.Length);

                if (contentHash is null)
                {

                    return Failure(
                        "restore_required",
                        $"Project '{projectName}' restore artifacts changed during validation.");
                }

                seedFiles.Add(
                    new SeedFile(
                        source,
                        artifactName,
                        metadata.Identity,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        contentHash));
            }

            string destination = WorkspaceCheckArtifactsLayout.ProjectIntermediateRoot(
                destinationRoot,
                project);
            Directory.CreateDirectory(destination);

            foreach (SeedFile seed in seedFiles)
            {

                cancellationToken.ThrowIfCancellationRequested();

                string target = Path.Combine(destination, seed.Name);

                if (!await CopyIdentityCheckedAsync(
                        seed,
                        target,
                        cancellationToken).ConfigureAwait(false))
                {

                    return Failure(
                        "restore_required",
                        $"Project '{projectName}' restore artifacts changed during seeding.");
                }

            }

            if (!RevalidateInputs(restoreInputs))
            {

                return Failure(
                    "restore_required",
                    $"Project '{projectName}' changed during restore-artifact seeding.");
            }

        }

        return new WorkspaceCheckRestoreSeedResult(
            true,
            null,
            null,
            projects.Count,
            files,
            bytes,
            allRestoreInputs.Values.ToArray());
    }

    private static async Task<bool> CopyIdentityCheckedAsync(
        SeedFile seed,
        string destination,
        CancellationToken cancellationToken)
    {

        try
        {

            using FileStream source = new(
                seed.Source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    source.SafeFileHandle,
                    out FileHandleMetadata opened)
                || !FileHandleIdentity.IdentitiesMatch(
                    seed.Identity,
                    opened.Identity)
                || source.Length != seed.Length)
            {

                return false;
            }

            await using FileStream target = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using IncrementalHash copiedHash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long copiedBytes = 0;

            while (true)
            {

                int read = await source.ReadAsync(
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {

                    break;
                }

                copiedBytes = checked(copiedBytes + read);

                if (copiedBytes > seed.Length)
                {

                    return false;
                }

                copiedHash.AppendData(buffer, 0, read);
                await target.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);

            return copiedBytes == seed.Length
                && string.Equals(
                    Convert.ToHexString(
                        copiedHash.GetHashAndReset()),
                    seed.Sha256,
                    StringComparison.Ordinal)
                && FileHandleIdentityInterop.TryGetPathMetadata(
                    seed.Source,
                    out FileHandleMetadata after)
                && FileHandleIdentity.IdentitiesMatch(
                    seed.Identity,
                    after.Identity)
                && new FileInfo(seed.Source).Length == seed.Length
                && new FileInfo(seed.Source).LastWriteTimeUtc.Ticks
                    == seed.LastWriteUtcTicks;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {

            return false;
        }
    }

    private static string? ComputeSha256(
        string path,
        FileHandleIdentity identity,
        long expectedLength)
    {

        try
        {

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length != expectedLength
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata opened)
                || !FileHandleIdentity.IdentitiesMatch(
                    identity,
                    opened.Identity))
            {

                return null;
            }

            return Convert.ToHexString(
                SHA256.HashData(stream));
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {

            return null;
        }
    }

    private static bool TryValidateContainedRegularFile(
        string path,
        string workspace,
        out FileHandleMetadata metadata)
    {

        metadata = default;

        try
        {

            string? full = CanonicalizeExistingFile(path);
            FileInfo info = new(full ?? string.Empty);

            return full is not null
                && info.Exists
                && (info.Attributes & FileAttributes.ReparsePoint) == 0
                && WorkspaceRootPath.IsWithinOrEqual(full, workspace)
                && FileHandleIdentityInterop.TryGetPathMetadata(
                    full,
                    out metadata)
                && metadata.HardLinkCount == 1;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return false;
        }
    }

    private static bool TryCaptureRestoreInputs(
        string workspace,
        string project,
        string projectDirectory,
        CancellationToken cancellationToken,
        out IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
            snapshots,
        out DateTime newest)
    {
        const int MaxRestoreInputs = 64;
        const long maxInputBytes = 8L * 1024L * 1024L;
        List<string> paths = [project];

        if (!TryEnumerateProjectRestoreInputs(
                workspace,
                project,
                projectDirectory,
                maxInputBytes,
                cancellationToken,
                out IReadOnlyList<string> projectInputs))
        {
            snapshots = [];
            newest = default;
            return false;
        }

        paths.AddRange(projectInputs);
        List<string> ancestorXmlInputs = [];
        string current = projectDirectory;

        while (WorkspaceRootPath.IsWithinOrEqual(current, workspace))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string inputName in AncestorInputs)
            {
                string input = Path.Combine(current, inputName);

                if (File.Exists(input))
                {
                    paths.Add(input);

                    if (input.EndsWith(
                            ".props",
                            StringComparison.OrdinalIgnoreCase)
                        || input.EndsWith(
                            ".targets",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        ancestorXmlInputs.Add(input);
                    }

                    if (paths.Count > MaxRestoreInputs)
                    {
                        snapshots = [];
                        newest = default;
                        return false;
                    }
                }
            }

            if (PathComparer.Equals(current, workspace))
            {
                break;
            }

            string? parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            current = parent;
        }

        List<string> ancestorNestedInputs = [];
        HashSet<string> ancestorActive =
            new(PathComparer);
        long ancestorParsedBytes = 0;

        foreach (string ancestor in ancestorXmlInputs)
        {
            Dictionary<string, string> properties =
                CreateBuiltInProperties(
                    project,
                    projectDirectory,
                    ancestor);

            if (!TryVisitRestoreInput(
                    workspace,
                    ancestor,
                    project,
                    ancestor,
                    properties,
                    ancestorNestedInputs,
                    ancestorActive,
                    ref ancestorParsedBytes,
                    maxInputBytes,
                    cancellationToken))
            {
                snapshots = [];
                newest = default;
                return false;
            }
        }

        paths.AddRange(ancestorNestedInputs);

        string projectName = Path.GetFileNameWithoutExtension(project);
        foreach (string lockName in new[]
                 {
                     "packages.lock.json",
                     $"packages.{projectName}.lock.json",
                 })
        {
            string lockPath = Path.Combine(
                projectDirectory,
                lockName);

            if (File.Exists(lockPath))
            {
                paths.Add(lockPath);
            }
        }

        List<WorkspaceCheckRestoreInputFingerprint> captured = [];
        long bytes = 0;
        newest = DateTime.MinValue;

        foreach (string path in paths.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryValidateContainedRegularFile(
                    path,
                    workspace,
                    out FileHandleMetadata metadata))
            {
                snapshots = [];
                return false;
            }

            FileInfo info = new(path);
            bytes = checked(bytes + info.Length);

            if (captured.Count >= MaxRestoreInputs
                || bytes > maxInputBytes)
            {
                snapshots = [];
                return false;
            }

            string? hash = ComputeSha256(
                path,
                metadata.Identity,
                info.Length);

            if (hash is null)
            {
                snapshots = [];
                return false;
            }

            newest = info.LastWriteTimeUtc > newest
                ? info.LastWriteTimeUtc
                : newest;
            captured.Add(
                new WorkspaceCheckRestoreInputFingerprint(
                    path,
                    metadata.Identity,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    hash));
        }

        snapshots = captured;
        return true;
    }

    private static bool TryEnumerateProjectRestoreInputs(
        string workspace,
        string project,
        string projectDirectory,
        long maxBytes,
        CancellationToken cancellationToken,
        out IReadOnlyList<string> inputs)
    {
        List<string> found = [];
        HashSet<string> active = new(PathComparer);
        long parsedBytes = 0;
        Dictionary<string, string> properties =
            CreateBuiltInProperties(
                project,
                projectDirectory,
                project);

        bool success = TryVisitRestoreInput(
            workspace,
            project,
            project,
            project,
            properties,
            found,
            active,
            ref parsedBytes,
            maxBytes,
            cancellationToken);
        inputs = success
            ? found
            : [];
        return success;
    }

    private static bool TryVisitRestoreInput(
        string workspace,
        string topLevelProject,
        string rootProject,
        string path,
        Dictionary<string, string> properties,
        List<string> found,
        HashSet<string> active,
        ref long parsedBytes,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? canonical = CanonicalizeExistingFile(path);

            if (canonical is null
                || !WorkspaceRootPath.IsWithinOrEqual(
                    canonical,
                    workspace)
                || !active.Add(canonical)
                || found.Count >= 64)
            {
                return false;
            }

            FileInfo info = new(canonical);
            parsedBytes = checked(parsedBytes + info.Length);

            if (parsedBytes > maxBytes)
            {
                return false;
            }

            if (!PathComparer.Equals(
                    canonical,
                    topLevelProject))
            {
                found.Add(canonical);
            }

            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Min(
                    maxBytes,
                    info.Length + 1),
            };
            using FileStream stream = new(
                canonical,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            using XmlReader reader = XmlReader.Create(
                stream,
                settings);
            XDocument document = XDocument.Load(
                reader,
                LoadOptions.None);
            string currentDirectory =
                Path.GetDirectoryName(canonical)!;
            SetBuiltInProperties(
                properties,
                rootProject,
                currentDirectory,
                canonical);

            foreach (XElement element in document.Descendants())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (element.Parent?.Name.LocalName
                        == "PropertyGroup"
                    && !element.HasElements
                    && element.Attribute("Condition") is null)
                {
                    if (TryExpandStaticValue(
                            element.Value,
                            properties,
                            out string propertyValue))
                    {
                        properties[element.Name.LocalName] =
                            propertyValue;
                    }

                    continue;
                }

                bool projectReference =
                    element.Name.LocalName
                    == "ProjectReference";
                string? expression =
                    element.Name.LocalName switch
                    {
                        "Import" =>
                            element.Attribute("Project")?.Value,
                        "ProjectReference" =>
                            element.Attribute("Include")?.Value,
                        _ => null,
                    };

                if (string.IsNullOrWhiteSpace(expression))
                {
                    continue;
                }

                if (!TryExpandStaticValue(
                        expression,
                        properties,
                        out string expanded)
                    || expanded.IndexOfAny(
                        ['*', '?', ';']) >= 0)
                {
                    return false;
                }

                string child = Path.GetFullPath(
                    expanded,
                    currentDirectory);
                Dictionary<string, string> childProperties =
                    projectReference
                        ? CreateBuiltInProperties(
                            child,
                            Path.GetDirectoryName(child)!,
                            child)
                        : properties;

                if (!TryVisitRestoreInput(
                        workspace,
                        topLevelProject,
                        projectReference
                            ? child
                            : rootProject,
                        child,
                        childProperties,
                        found,
                        active,
                        ref parsedBytes,
                        maxBytes,
                        cancellationToken))
                {
                    return false;
                }
            }

            active.Remove(canonical);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or XmlException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private static Dictionary<string, string>
        CreateBuiltInProperties(
            string rootProject,
            string rootDirectory,
            string currentFile)
    {
        Dictionary<string, string> properties =
            new(StringComparer.OrdinalIgnoreCase);
        SetBuiltInProperties(
            properties,
            rootProject,
            Path.GetDirectoryName(currentFile)
                ?? rootDirectory,
            currentFile);
        return properties;
    }

    private static void SetBuiltInProperties(
        Dictionary<string, string> properties,
        string rootProject,
        string currentDirectory,
        string currentFile)
    {
        properties["MSBuildProjectDirectory"] =
            Path.GetDirectoryName(rootProject)!;
        properties["MSBuildProjectFullPath"] =
            rootProject;
        properties["MSBuildProjectFile"] =
            Path.GetFileName(rootProject);
        properties["MSBuildProjectName"] =
            Path.GetFileNameWithoutExtension(rootProject);
        properties["MSBuildProjectExtension"] =
            Path.GetExtension(rootProject);
        properties["MSBuildThisFileDirectory"] =
            Path.EndsInDirectorySeparator(currentDirectory)
                ? currentDirectory
                : currentDirectory
                  + Path.DirectorySeparatorChar;
        properties["MSBuildThisFileFullPath"] =
            currentFile;
    }

    private static bool TryExpandStaticValue(
        string expression,
        IReadOnlyDictionary<string, string> properties,
        out string expanded)
    {
        expanded = expression;

        for (int iteration = 0; iteration < 16; iteration++)
        {
            int start = expanded.IndexOf(
                "$(",
                StringComparison.Ordinal);

            if (start < 0)
            {
                return true;
            }

            int end = expanded.IndexOf(
                ')',
                start + 2);

            if (end < 0)
            {
                return false;
            }

            string name = expanded[
                (start + 2)..end];

            if (name.Length == 0
                || name.Contains(
                    "::",
                    StringComparison.Ordinal)
                || !properties.TryGetValue(
                    name,
                    out string? value))
            {
                return false;
            }

            expanded = string.Concat(
                expanded.AsSpan(0, start),
                value,
                expanded.AsSpan(end + 1));
        }

        return !expanded.Contains(
            "$(",
            StringComparison.Ordinal);
    }

    internal static bool RevalidateManifest(
        string workspaceRoot,
        IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
            seededManifest,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(seededManifest);
        ArgumentNullException.ThrowIfNull(options);

        string? workspace =
            CanonicalizeExistingDirectory(workspaceRoot);

        if (workspace is null
            || options.MaxProjects < 1
            || options.MaxFiles < 1
            || options.MaxBytes < 1
            || !TryCaptureCurrentManifest(
                workspace,
                options,
                cancellationToken,
                out IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
                    currentManifest)
            || currentManifest.Count != seededManifest.Count)
        {
            return false;
        }

        Dictionary<string, WorkspaceCheckRestoreInputFingerprint>
            currentByPath = currentManifest.ToDictionary(
                input => input.Path,
                PathComparer);

        return seededManifest.All(
            seeded =>
                currentByPath.TryGetValue(
                    seeded.Path,
                    out WorkspaceCheckRestoreInputFingerprint?
                        current)
                && current == seeded);
    }

    private static bool TryCaptureCurrentManifest(
        string workspace,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken,
        out IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
            manifest)
    {
        List<string> projects = [];

        try
        {
            foreach (string path in Directory.EnumerateFiles(
                         workspace,
                         "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = false,
                             AttributesToSkip =
                                 FileAttributes.ReparsePoint
                                 | FileAttributes.Device,
                             ReturnSpecialDirectories = false,
                         }))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ProjectExtensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase)
                    || HasIgnoredDirectory(workspace, path))
                {
                    continue;
                }

                projects.Add(path);

                if (projects.Count > options.MaxProjects)
                {
                    manifest = [];
                    return false;
                }
            }

            projects.Sort(PathComparer);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            manifest = [];
            return false;
        }

        Dictionary<string, WorkspaceCheckRestoreInputFingerprint>
            captured = new(PathComparer);
        long bytes = 0;

        foreach (string project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryCaptureRestoreInputs(
                    workspace,
                    project,
                    Path.GetDirectoryName(project)!,
                    cancellationToken,
                    out IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
                        projectInputs,
                    out _))
            {
                manifest = [];
                return false;
            }

            foreach (WorkspaceCheckRestoreInputFingerprint input
                     in projectInputs)
            {
                if (captured.TryGetValue(
                        input.Path,
                        out WorkspaceCheckRestoreInputFingerprint?
                            existing))
                {
                    if (existing != input)
                    {
                        manifest = [];
                        return false;
                    }

                    continue;
                }

                bytes = checked(bytes + input.Length);

                if (captured.Count >= 256
                    || bytes > 16L * 1024L * 1024L)
                {
                    manifest = [];
                    return false;
                }

                captured.Add(input.Path, input);
            }
        }

        manifest = captured.Values.ToArray();
        return true;
    }

    internal static bool RevalidateInputs(
        IReadOnlyList<WorkspaceCheckRestoreInputFingerprint>
            snapshots)
    {
        foreach (WorkspaceCheckRestoreInputFingerprint input
                 in snapshots)
        {
            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    input.Path,
                    out FileHandleMetadata current)
                || !FileHandleIdentity.IdentitiesMatch(
                    input.Identity,
                    current.Identity))
            {
                return false;
            }

            FileInfo info = new(input.Path);

            if (info.Length != input.Length
                || info.LastWriteTimeUtc.Ticks
                    != input.LastWriteUtcTicks
                || !string.Equals(
                    ComputeSha256(
                        input.Path,
                        input.Identity,
                        input.Length),
                    input.Sha256,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateArtifactContents(
        string path,
        string artifactName,
        long maxBytes)
    {

        try
        {

            if (artifactName.EndsWith(
                    ".props",
                    StringComparison.OrdinalIgnoreCase)
                || artifactName.EndsWith(
                    ".targets",
                    StringComparison.OrdinalIgnoreCase))
            {

                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
                using XmlReader reader = XmlReader.Create(
                    stream,
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        MaxCharactersInDocument = maxBytes,
                        XmlResolver = null,
                    });
                _ = XDocument.Load(reader, LoadOptions.None);
            }
            else
            {

                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
                using JsonDocument document = JsonDocument.Parse(
                    stream,
                    new JsonDocumentOptions { MaxDepth = 64 });
                _ = document.RootElement.ValueKind;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or System.Xml.XmlException)
        {

            return false;
        }
    }

    private static bool HasIgnoredDirectory(
        string workspace,
        string path)
    {

        string relative = Path.GetRelativePath(workspace, path);

        return relative
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(static part =>
                part is ".git" or "bin" or "obj"
                or "node_modules");
    }

    private static string? CanonicalizeExistingDirectory(string path) =>
        CanonicalizeExistingPath(path, expectDirectory: true, resolutionDepth: 0);

    private static string? CanonicalizeExistingFile(string path) =>
        CanonicalizeExistingPath(path, expectDirectory: false, resolutionDepth: 0);

    private static string? CanonicalizeExistingPath(
        string path,
        bool expectDirectory,
        int resolutionDepth)
    {

        if (resolutionDepth > 40)
        {

            return null;
        }

        try
        {

            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(root))
            {

                return null;
            }

            string current = root;
            string[] components = fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            for (int index = 0; index < components.Length; index++)
            {

                bool final = index == components.Length - 1;
                string candidate = Path.Combine(current, components[index]);
                FileSystemInfo entry = final && !expectDirectory
                    ? new FileInfo(candidate)
                    : new DirectoryInfo(candidate);

                if (!entry.Exists)
                {

                    return null;
                }

                FileSystemInfo? target = entry.ResolveLinkTarget(
                    returnFinalTarget: true);
                current = target is null
                    ? Path.GetFullPath(candidate)
                    : CanonicalizeExistingPath(
                        target.FullName,
                        expectDirectory: !final || expectDirectory,
                        resolutionDepth + 1)
                      ?? string.Empty;

                if (current.Length == 0)
                {

                    return null;
                }

            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return null;
        }
    }

    private static WorkspaceCheckRestoreSeedResult Failure(
        string code,
        string message) =>
        new(false, code, message, 0, 0, 0, []);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record SeedFile(
        string Source,
        string Name,
        FileHandleIdentity Identity,
        long Length,
        long LastWriteUtcTicks,
        string Sha256);

}
