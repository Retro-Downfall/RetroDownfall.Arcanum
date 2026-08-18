using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckRestoreSeedOptions(
    long MaxBytes)
{

    internal static WorkspaceCheckRestoreSeedOptions Default { get; } =
        new(
            MaxBytes: 100L * 1024L * 1024L);
}

internal sealed record WorkspaceCheckRestoreSeedResult(
    bool Success,
    string? Code,
    string? Message,
    int ProjectCount,
    int FileCount,
    long ByteCount,
    WorkspaceCheckRestoreInputManifest? InputManifest);

internal sealed record WorkspaceCheckRestoreInputManifest(
    string Path,
    FileHandleIdentity Identity,
    long Length,
    long LastWriteUtcTicks,
    string Sha256,
    int RecordCount,
    int ProjectCount,
    string InputSetDigest);

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

    private static readonly EnumerationOptions ProjectEnumerationOptions =
        new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip =
                FileAttributes.ReparsePoint
                | FileAttributes.Device,
            ReturnSpecialDirectories = false,
        };

    internal static async Task<WorkspaceCheckRestoreSeedResult> SeedAsync(
        string workspaceRoot,
        string artifactsRoot,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsRoot);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxBytes < 1)
        {

            return Failure(
                "invalid_seed_limits",
                "The restore-artifact Sanctum MaxFileWriteMb policy must be positive.");
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

        RestoreSeedWriteBudget writeBudget = new(options.MaxBytes);

        if (!RestoreInputManifestWriter.TryCreate(
                destinationRoot,
                writeBudget,
                out RestoreInputManifestWriter? manifestWriter))
        {

            return Failure(
                "restore_required",
                "The owner-only restore-input manifest could not be created.");
        }

        RestoreInputManifestWriter activeManifestWriter = manifestWriter!;

        using (activeManifestWriter)
        {

        int projects = 0;
        int files = 0;
        long bytes = 0;

        try
        {

            foreach (string project in Directory.EnumerateFiles(
                         workspace,
                         "*",
                         ProjectEnumerationOptions))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (!ProjectExtensions.Contains(
                        Path.GetExtension(project),
                        StringComparer.OrdinalIgnoreCase)
                    || HasIgnoredDirectory(workspace, project))
                {

                    continue;

                }

                projects = checked(projects + 1);

            string projectName = Path.GetFileNameWithoutExtension(project);

            if (!TryValidateContainedRegularFile(
                    project,
                    workspace,
                    out _))
            {

                return Failure(
                    "restore_required",
                    "A project file could not be identity-validated inside the workspace.");
            }

            string projectDirectory = Path.GetDirectoryName(project)!;
            string sourceObj = Path.Combine(projectDirectory, "obj");
            string projectFileName = Path.GetFileName(project);
            string destination = WorkspaceCheckArtifactsLayout.ProjectIntermediateRoot(
                destinationRoot,
                project);
            ArtifactsProjectClaimResult claim =
                TryClaimArtifactsProjectName(destination);

            if (claim == ArtifactsProjectClaimResult.Collision)
            {

                return Failure(
                    "restore_required",
                    $"Multiple projects map to the same .NET artifacts project name '{projectName}'.");
            }

            if (claim != ArtifactsProjectClaimResult.Success)
            {

                return Failure(
                    "restore_required",
                    $"Project '{projectName}' could not claim its owner-only artifacts destination.");
            }

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
                    activeManifestWriter.TryAppend,
                    cancellationToken,
                    out DateTime newestInput))
            {

                if (writeBudget.Exceeded)
                {

                    return FailureForWritePolicy(writeBudget);
                }

                return Failure(
                    "restore_required",
                    $"Project '{projectName}' restore inputs could not be safely fingerprinted.");
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

                if (!writeBudget.TryReserve(info.Length))
                {

                    return FailureForWritePolicy(writeBudget);
                }

                files = checked(files + 1);
                bytes = checked(bytes + info.Length);

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

            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or OverflowException)
        {

            return Failure(
                "restore_required",
                "Project discovery could not safely inspect the workspace restore artifacts.");
        }

        if (!activeManifestWriter.TryFinalize(
                projects,
                out WorkspaceCheckRestoreInputManifest? manifest))
        {

            return writeBudget.Exceeded
                ? FailureForWritePolicy(writeBudget)
                : Failure(
                    "restore_required",
                    "The owner-only restore-input manifest could not be identity-validated.");
        }

        if (!RevalidateManifest(
                workspace,
                manifest!,
                options,
                cancellationToken))
        {

            return Failure(
                "restore_required",
                "A restore-affecting workspace input changed during artifact seeding.");
        }

        activeManifestWriter.Keep();

        return new WorkspaceCheckRestoreSeedResult(
            true,
            null,
            null,
            projects,
            files,
            bytes,
            manifest!);

        }
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

    private static ArtifactsProjectClaimResult TryClaimArtifactsProjectName(
        string destination)
    {

        const string ClaimFileName = ".arcanum-project-claim";
        string claimPath = Path.Combine(destination, ClaimFileName);

        try
        {

            if (!SecureFilePermissions
                    .TryEnsureOwnerOnlyDirectoryExistsStrict(destination))
            {

                return ArtifactsProjectClaimResult.Failure;
            }

            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = 1,
                Options = FileOptions.WriteThrough,
            };

            if (!OperatingSystem.IsWindows())
            {

                options.UnixCreateMode =
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite;
            }

            using FileStream claim = new(claimPath, options);

            if (!SecureFilePermissions.TryApplyOwnerOnlyFileStrict(claimPath)
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    claim.SafeFileHandle,
                    out FileHandleMetadata opened)
                || opened.Kind != FileSystemObjectKind.RegularFile
                || opened.HardLinkCount != 1
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    claimPath,
                    out FileHandleMetadata pathMetadata)
                || pathMetadata != opened)
            {

                return ArtifactsProjectClaimResult.Failure;
            }

            return ArtifactsProjectClaimResult.Success;
        }
        catch (IOException)
        {

            return File.Exists(claimPath)
                ? ArtifactsProjectClaimResult.Collision
                : ArtifactsProjectClaimResult.Failure;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return ArtifactsProjectClaimResult.Failure;
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

    private static string ComputeSha256(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream));

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
        Func<WorkspaceCheckRestoreInputFingerprint, bool> capture,
        CancellationToken cancellationToken,
        out DateTime newest)
    {
        ArgumentNullException.ThrowIfNull(capture);

        DateTime newestSeen = DateTime.MinValue;

        bool CapturePath(string path)
        {

            if (!TryCaptureFingerprint(
                    path,
                    workspace,
                    out WorkspaceCheckRestoreInputFingerprint? fingerprint)
                || fingerprint is null
                || !capture(fingerprint))
            {

                return false;
            }

            DateTime modified = new(fingerprint.LastWriteUtcTicks, DateTimeKind.Utc);
            newestSeen = modified > newestSeen
                ? modified
                : newestSeen;
            return true;
        }

        HashSet<string> active = new(PathComparer);
        Dictionary<string, string> projectProperties =
            CreateBuiltInProperties(
                project,
                projectDirectory,
                project);

        if (!TryVisitRestoreInput(
                workspace,
                project,
                project,
                projectProperties,
                CapturePath,
                active,
                new HashSet<string>(PathComparer),
                depth: 0,
                cancellationToken))
        {

            newest = default;
            return false;
        }

        string current = projectDirectory;

        while (WorkspaceRootPath.IsWithinOrEqual(current, workspace))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string inputName in AncestorInputs)
            {
                string input = Path.Combine(current, inputName);

                if (File.Exists(input))
                {
                    if (input.EndsWith(
                            ".props",
                            StringComparison.OrdinalIgnoreCase)
                        || input.EndsWith(
                            ".targets",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, string> properties =
                            CreateBuiltInProperties(
                                project,
                                projectDirectory,
                                input);

                        if (!TryVisitRestoreInput(
                                workspace,
                                project,
                                input,
                                properties,
                                CapturePath,
                                active,
                                new HashSet<string>(PathComparer),
                                depth: 0,
                                cancellationToken))
                        {

                            newest = default;
                            return false;
                        }
                    }
                    else if (!CapturePath(input))
                    {

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
                if (!CapturePath(lockPath))
                {

                    newest = default;
                    return false;
                }
            }
        }

        newest = newestSeen;
        return newest != DateTime.MinValue;
    }

    /// <summary>
    /// Walks one restore input and everything it imports. <paramref name="active"/> is the cycle stack;
    /// <paramref name="completed"/> memoises the files already folded into <paramref name="properties"/>.
    /// The memo is per property context, not global: visiting a file has the side effect of folding its
    /// PropertyGroup values into the dictionary it was handed, so a file already walked under a *different*
    /// context must still be walked under this one or a later $(Foo)/x.props import would fail to expand.
    /// Imports share the caller's dictionary and therefore its memo; a ProjectReference starts a fresh
    /// dictionary and gets a fresh memo alongside it. Without the memo a file reachable through k import
    /// edges was re-opened, re-parsed, re-hashed and re-appended to the manifest 2^k times.
    /// </summary>
    private static bool TryVisitRestoreInput(
        string workspace,
        string rootProject,
        string path,
        Dictionary<string, string> properties,
        Func<string, bool> capture,
        HashSet<string> active,
        HashSet<string> completed,
        int depth,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Every frame holds a live FileStream plus an XmlReader, and the write budget does not bound
            // recursion: a long linear import chain of tiny files stays far under the byte cap while
            // recursing deep enough to raise an uncatchable StackOverflowException and take the host down.
            if (depth > MaxRestoreInputImportDepth)
            {
                return false;
            }

            string? canonical = CanonicalizeExistingFile(path);

            if (canonical is null
                || !WorkspaceRootPath.IsWithinOrEqual(
                    canonical,
                    workspace))
            {
                return false;
            }

            if (completed.Contains(canonical))
            {
                return true;
            }

            if (!active.Add(canonical))
            {
                return false;
            }

            if (!capture(canonical))
            {
                return false;
            }

            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
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
            string currentDirectory =
                Path.GetDirectoryName(canonical)!;
            SetBuiltInProperties(
                properties,
                rootProject,
                currentDirectory,
                canonical);
            int propertyGroupDepth = -1;
            int propertyDepth = -1;
            string? propertyName = null;
            StringBuilder? propertyValue = null;
            bool propertyHasElements = false;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType is
                    XmlNodeType.Text
                    or XmlNodeType.CDATA
                    or XmlNodeType.Whitespace
                    or XmlNodeType.SignificantWhitespace)
                {

                    if (propertyDepth >= 0
                        && !propertyHasElements
                        && reader.Depth == propertyDepth + 1)
                    {

                        propertyValue!.Append(reader.Value);
                    }

                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement)
                {

                    if (reader.Depth == propertyDepth)
                    {

                        if (!propertyHasElements
                            && propertyName is not null
                            && TryExpandStaticValue(
                                propertyValue?.ToString()
                                    ?? string.Empty,
                                properties,
                                out string expandedProperty))
                        {

                            properties[propertyName] =
                                expandedProperty;
                        }

                        propertyDepth = -1;
                        propertyName = null;
                        propertyValue = null;
                        propertyHasElements = false;
                    }

                    if (reader.Depth == propertyGroupDepth
                        && reader.LocalName == "PropertyGroup")
                    {

                        propertyGroupDepth = -1;
                    }

                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {

                    continue;
                }

                if (propertyDepth >= 0
                    && reader.Depth > propertyDepth)
                {

                    propertyHasElements = true;
                }

                if (reader.LocalName == "PropertyGroup")
                {

                    propertyGroupDepth = reader.Depth;
                    continue;
                }

                if (propertyGroupDepth >= 0
                    && propertyDepth < 0
                    && reader.Depth == propertyGroupDepth + 1
                    && reader.GetAttribute("Condition") is null)
                {

                    propertyDepth = reader.Depth;
                    propertyName = reader.LocalName;
                    propertyValue = new StringBuilder();
                    propertyHasElements = false;

                    if (reader.IsEmptyElement)
                    {

                        properties[propertyName] = string.Empty;
                        propertyDepth = -1;
                        propertyName = null;
                        propertyValue = null;
                    }

                    continue;
                }

                bool projectReference =
                    reader.LocalName
                    == "ProjectReference";
                string? expression =
                    reader.LocalName switch
                    {
                        "Import" =>
                            reader.GetAttribute("Project"),
                        "ProjectReference" =>
                            reader.GetAttribute("Include"),
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
                        projectReference
                            ? child
                            : rootProject,
                        child,
                        childProperties,
                        capture,
                        active,
                        projectReference
                            ? new HashSet<string>(PathComparer)
                            : completed,
                        depth + 1,
                        cancellationToken))
                {
                    return false;
                }

                SetBuiltInProperties(
                    properties,
                    rootProject,
                    currentDirectory,
                    canonical);
            }

            active.Remove(canonical);
            _ = completed.Add(canonical);
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

    private static bool TryCaptureFingerprint(
        string path,
        string workspace,
        out WorkspaceCheckRestoreInputFingerprint? fingerprint)
    {

        fingerprint = null;
        string? canonical = CanonicalizeExistingFile(path);

        if (canonical is null
            || !TryValidateContainedRegularFile(
                canonical,
                workspace,
                out FileHandleMetadata metadata))
        {

            return false;
        }

        FileInfo info = new(canonical);
        long length = info.Length;
        long lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        string? hash = ComputeSha256(
            canonical,
            metadata.Identity,
            length);

        if (hash is null
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                canonical,
                out FileHandleMetadata after)
            || !FileHandleIdentity.IdentitiesMatch(
                metadata.Identity,
                after.Identity))
        {

            return false;
        }

        info.Refresh();

        if (info.Length != length
            || info.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
        {

            return false;
        }

        fingerprint = new WorkspaceCheckRestoreInputFingerprint(
            canonical,
            metadata.Identity,
            length,
            lastWriteUtcTicks,
            hash);
        return true;
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
        WorkspaceCheckRestoreInputManifest seededManifest,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(seededManifest);
        ArgumentNullException.ThrowIfNull(options);

        string? workspace =
            CanonicalizeExistingDirectory(workspaceRoot);

        if (workspace is null
            || options.MaxBytes < 1
            || !TryRevalidateStoredManifest(
                workspace,
                seededManifest,
                options,
                cancellationToken)
            || !TryCaptureCurrentManifest(
                workspace,
                cancellationToken,
                out int projectCount,
                out int recordCount,
                out string? inputSetDigest))
        {
            return false;
        }

        return projectCount == seededManifest.ProjectCount
            && recordCount == seededManifest.RecordCount
            && string.Equals(
                inputSetDigest,
                seededManifest.InputSetDigest,
                StringComparison.Ordinal);
    }

    private static bool TryCaptureCurrentManifest(
        string workspace,
        CancellationToken cancellationToken,
        out int projectCount,
        out int recordCount,
        out string? inputSetDigest)
    {
        projectCount = 0;
        recordCount = 0;
        inputSetDigest = null;
        ManifestDigestAccumulator accumulator = new();
        int capturedRecords = 0;

        try
        {
            foreach (string project in Directory.EnumerateFiles(
                         workspace,
                         "*",
                         ProjectEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ProjectExtensions.Contains(
                        Path.GetExtension(project),
                        StringComparer.OrdinalIgnoreCase)
                    || HasIgnoredDirectory(workspace, project))
                {
                    continue;
                }

                projectCount = checked(projectCount + 1);

                if (!TryCaptureRestoreInputs(
                        workspace,
                        project,
                        Path.GetDirectoryName(project)!,
                        fingerprint =>
                        {

                            accumulator.Add(fingerprint);
                            capturedRecords = checked(capturedRecords + 1);
                            return true;
                        },
                        cancellationToken,
                        out _))
                {

                    return false;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or OverflowException)
        {

            return false;
        }

        recordCount = capturedRecords;
        inputSetDigest = accumulator.GetDigest();
        return true;
    }

    private static bool TryRevalidateStoredManifest(
        string workspace,
        WorkspaceCheckRestoreInputManifest manifest,
        WorkspaceCheckRestoreSeedOptions options,
        CancellationToken cancellationToken)
    {

        try
        {

            if (manifest.Length < 0
                || manifest.Length > options.MaxBytes
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    manifest.Path,
                    out FileHandleMetadata before)
                || before.Kind != FileSystemObjectKind.RegularFile
                || before.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    manifest.Identity,
                    before.Identity))
            {

                return false;
            }

            using FileStream stream = new(
                manifest.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length != manifest.Length
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata opened)
                || opened.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    manifest.Identity,
                    opened.Identity)
                || !string.Equals(
                    ComputeSha256(stream),
                    manifest.Sha256,
                    StringComparison.Ordinal))
            {

                return false;
            }

            stream.Position = 0;
            ManifestDigestAccumulator accumulator = new();
            int records = 0;

            using (StreamReader reader = new(
                       stream,
                       new UTF8Encoding(
                           encoderShouldEmitUTF8Identifier: false,
                           throwOnInvalidBytes: true),
                       detectEncodingFromByteOrderMarks: false,
                       bufferSize: 16 * 1024,
                       leaveOpen: true))
            {

                while (reader.ReadLine() is { } line)
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryParseManifestRecord(
                            line,
                            out WorkspaceCheckRestoreInputFingerprint? input)
                        || input is null
                        || !TryRevalidateInput(
                            workspace,
                            input))
                    {

                        return false;
                    }

                    accumulator.Add(input);
                    records = checked(records + 1);
                }

            }

            stream.Position = 0;

            if (records != manifest.RecordCount
                || !string.Equals(
                    accumulator.GetDigest(),
                    manifest.InputSetDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    ComputeSha256(stream),
                    manifest.Sha256,
                    StringComparison.Ordinal)
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    manifest.Path,
                    out FileHandleMetadata after)
                || after.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    manifest.Identity,
                    after.Identity))
            {

                return false;
            }

            FileInfo info = new(manifest.Path);

            return info.Length == manifest.Length
                && info.LastWriteTimeUtc.Ticks
                    == manifest.LastWriteUtcTicks;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or JsonException
                or DecoderFallbackException
                or InvalidOperationException
                or OverflowException)
        {

            return false;
        }
    }

    private static bool TryRevalidateInput(
        string workspace,
        WorkspaceCheckRestoreInputFingerprint input)
    {

        string? canonical = CanonicalizeExistingFile(input.Path);

        if (canonical is null
            || !PathComparer.Equals(canonical, input.Path)
            || !TryValidateContainedRegularFile(
                canonical,
                workspace,
                out FileHandleMetadata current)
            || !FileHandleIdentity.IdentitiesMatch(
                input.Identity,
                current.Identity))
        {

            return false;
        }

        FileInfo info = new(canonical);

        if (info.Length != input.Length
            || info.LastWriteTimeUtc.Ticks
                != input.LastWriteUtcTicks
            || !string.Equals(
                ComputeSha256(
                    canonical,
                    input.Identity,
                    input.Length),
                input.Sha256,
                StringComparison.Ordinal)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                canonical,
                out FileHandleMetadata after)
            || !FileHandleIdentity.IdentitiesMatch(
                input.Identity,
                after.Identity))
        {

            return false;
        }

        info.Refresh();
        return info.Length == input.Length
            && info.LastWriteTimeUtc.Ticks
                == input.LastWriteUtcTicks;
    }

    private static bool TryParseManifestRecord(
        string line,
        out WorkspaceCheckRestoreInputFingerprint? fingerprint)
    {

        fingerprint = null;
        using JsonDocument document = JsonDocument.Parse(
            line,
            new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("path", out JsonElement pathElement)
            || pathElement.GetString() is not { Length: > 0 } path
            || !root.TryGetProperty("volumeId", out JsonElement volumeElement)
            || !volumeElement.TryGetUInt64(out ulong volumeId)
            || !root.TryGetProperty("fileId", out JsonElement fileElement)
            || !fileElement.TryGetUInt64(out ulong fileId)
            || !root.TryGetProperty("length", out JsonElement lengthElement)
            || !lengthElement.TryGetInt64(out long length)
            || length < 0
            || !root.TryGetProperty("lastWriteUtcTicks", out JsonElement timeElement)
            || !timeElement.TryGetInt64(out long lastWriteUtcTicks)
            || !root.TryGetProperty("sha256", out JsonElement hashElement)
            || hashElement.GetString() is not { Length: 64 } sha256)
        {

            return false;
        }

        fingerprint = new WorkspaceCheckRestoreInputFingerprint(
            path,
            new FileHandleIdentity(volumeId, fileId),
            length,
            lastWriteUtcTicks,
            sha256);
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
        new(false, code, message, 0, 0, 0, null);

    private static WorkspaceCheckRestoreSeedResult FailureForWritePolicy(
        RestoreSeedWriteBudget budget) =>
        Failure(
            "seed_cap_exceeded",
            $"Physical resource protection: restore-artifact seeding would write {budget.AttemptedBytes} bytes, exceeding the explicit Sanctum MaxFileWriteMb policy of {budget.LimitBytes} bytes. The check was not started; rerun after reducing pre-existing restore artifacts or explicitly raising Sanctum MaxFileWriteMb.");

    private static byte[] SerializeManifestRecord(
        WorkspaceCheckRestoreInputFingerprint fingerprint)
    {

        ArrayBufferWriter<byte> buffer = new(512);

        using (Utf8JsonWriter writer = new(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false,
                   }))
        {

            writer.WriteStartObject();
            writer.WriteString("path", fingerprint.Path);
            writer.WriteNumber(
                "volumeId",
                fingerprint.Identity.VolumeId);
            writer.WriteNumber(
                "fileId",
                fingerprint.Identity.FileId);
            writer.WriteNumber("length", fingerprint.Length);
            writer.WriteNumber(
                "lastWriteUtcTicks",
                fingerprint.LastWriteUtcTicks);
            writer.WriteString("sha256", fingerprint.Sha256);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Import-nesting ceiling for the restore-input walk. Real MSBuild graphs nest an order of magnitude
    /// below this; the bound exists so a pathological chain fails the seed instead of overflowing the stack.
    /// </summary>
    private const int MaxRestoreInputImportDepth = 128;

    private sealed record SeedFile(
        string Source,
        string Name,
        FileHandleIdentity Identity,
        long Length,
        long LastWriteUtcTicks,
        string Sha256);

    private enum ArtifactsProjectClaimResult
    {
        Success,
        Collision,
        Failure,
    }

    private sealed class RestoreSeedWriteBudget(long limitBytes)
    {

        private long _reservedBytes;

        internal long LimitBytes { get; } = limitBytes;

        internal long AttemptedBytes { get; private set; }

        internal bool Exceeded { get; private set; }

        internal bool TryReserve(long byteCount)
        {

            if (byteCount < 0)
            {

                Exceeded = true;
                AttemptedBytes = long.MaxValue;
                return false;
            }

            AttemptedBytes = _reservedBytes > long.MaxValue - byteCount
                ? long.MaxValue
                : _reservedBytes + byteCount;

            if (AttemptedBytes > LimitBytes)
            {

                Exceeded = true;
                return false;
            }

            _reservedBytes = AttemptedBytes;
            return true;
        }
    }

    private sealed class ManifestDigestAccumulator
    {

        private readonly byte[] _xor = new byte[32];

        private readonly byte[] _sum = new byte[32];

        internal void Add(
            WorkspaceCheckRestoreInputFingerprint fingerprint) =>
            AddSerialized(SerializeManifestRecord(fingerprint));

        internal void AddSerialized(ReadOnlySpan<byte> serialized)
        {

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(serialized, hash);
            int carry = 0;

            for (int index = hash.Length - 1; index >= 0; index--)
            {

                _xor[index] ^= hash[index];
                int value = _sum[index] + hash[index] + carry;
                _sum[index] = unchecked((byte)value);
                carry = value >> 8;
            }
        }

        internal string GetDigest() =>
            Convert.ToHexString(_xor)
            + Convert.ToHexString(_sum);
    }

    private sealed class RestoreInputManifestWriter : IDisposable
    {

        private const string ManifestFileName =
            ".arcanum-restore-inputs.jsonl";

        private readonly string _path;

        private readonly FileHandleIdentity _identity;

        private readonly RestoreSeedWriteBudget _budget;

        private readonly ManifestDigestAccumulator _accumulator = new();

        private FileStream? _stream;

        private int _recordCount;

        private bool _keep;

        private RestoreInputManifestWriter(
            string path,
            FileHandleIdentity identity,
            RestoreSeedWriteBudget budget,
            FileStream stream)
        {

            _path = path;
            _identity = identity;
            _budget = budget;
            _stream = stream;
        }

        internal static bool TryCreate(
            string destinationRoot,
            RestoreSeedWriteBudget budget,
            out RestoreInputManifestWriter? writer)
        {

            writer = null;
            string path = Path.Combine(
                destinationRoot,
                ManifestFileName);
            FileStream? stream = null;
            FileHandleIdentity? createdIdentity = null;

            try
            {

                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    BufferSize = 16 * 1024,
                    Options = FileOptions.SequentialScan,
                };

                if (!OperatingSystem.IsWindows())
                {

                    options.UnixCreateMode =
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite;
                }

                stream = new FileStream(path, options);

                if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                        stream.SafeFileHandle,
                        out FileHandleMetadata opened))
                {

                    return false;
                }

                createdIdentity = opened.Identity;

                if (opened.Kind != FileSystemObjectKind.RegularFile
                    || opened.HardLinkCount != 1
                    || !SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path)
                    || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        path,
                        out FileHandleMetadata pathMetadata)
                    || pathMetadata != opened)
                {

                    return false;
                }

                writer = new RestoreInputManifestWriter(
                    path,
                    opened.Identity,
                    budget,
                    stream);
                stream = null;
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {

                return false;
            }
            finally
            {

                stream?.Dispose();

                if (writer is null
                    && createdIdentity is FileHandleIdentity identity
                    && FileHandleIdentityInterop
                        .TryGetPathMetadataNoFollow(
                            path,
                            out FileHandleMetadata metadata)
                    && metadata.Kind == FileSystemObjectKind.RegularFile
                    && metadata.HardLinkCount == 1
                    && FileHandleIdentity.IdentitiesMatch(
                        identity,
                        metadata.Identity))
                {

                    try
                    {

                        File.Delete(path);
                    }
                    catch (Exception ex) when (
                        ex is IOException
                            or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        internal bool TryAppend(
            WorkspaceCheckRestoreInputFingerprint fingerprint)
        {

            if (_stream is null)
            {

                return false;
            }

            try
            {

                byte[] serialized = SerializeManifestRecord(fingerprint);
                long recordBytes = checked(serialized.LongLength + 1L);

                if (!_budget.TryReserve(recordBytes))
                {

                    return false;
                }

                _stream.Write(serialized);
                _stream.WriteByte((byte)'\n');
                _accumulator.AddSerialized(serialized);
                _recordCount = checked(_recordCount + 1);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or OverflowException)
            {

                return false;
            }
        }

        internal bool TryFinalize(
            int projectCount,
            out WorkspaceCheckRestoreInputManifest? manifest)
        {

            manifest = null;
            FileStream? stream = _stream;

            if (stream is null)
            {

                return false;
            }

            try
            {

                stream.Flush(flushToDisk: true);
                stream.Dispose();
                _stream = null;

                if (!SecureFilePermissions.TryApplyOwnerOnlyFileStrict(_path)
                    || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                        _path,
                        out FileHandleMetadata metadata)
                    || metadata.Kind != FileSystemObjectKind.RegularFile
                    || metadata.HardLinkCount != 1
                    || !FileHandleIdentity.IdentitiesMatch(
                        _identity,
                        metadata.Identity))
                {

                    return false;
                }

                FileInfo info = new(_path);
                string? sha256 = ComputeSha256(
                    _path,
                    _identity,
                    info.Length);

                if (sha256 is null)
                {

                    return false;
                }

                info.Refresh();
                manifest = new WorkspaceCheckRestoreInputManifest(
                    _path,
                    _identity,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    sha256,
                    _recordCount,
                    projectCount,
                    _accumulator.GetDigest());
                return true;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
            {

                return false;
            }
        }

        internal void Keep() => _keep = true;

        public void Dispose()
        {

            _stream?.Dispose();
            _stream = null;

            if (_keep
                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                    _path,
                    out FileHandleMetadata metadata)
                || metadata.Kind != FileSystemObjectKind.RegularFile
                || metadata.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    _identity,
                    metadata.Identity))
            {

                return;
            }

            try
            {

                File.Delete(_path);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException)
            {
            }
        }
    }

}
