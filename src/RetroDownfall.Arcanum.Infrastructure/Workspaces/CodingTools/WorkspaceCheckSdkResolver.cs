using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Security;
using SdkVersion = RetroDownfall.Arcanum.Core.Configuration.WorkspaceCheckSdkVersion;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckSdkSnapshot(
    string Version,
    int MajorVersion,
    string SdkPath,
    string RuntimeVersion,
    string RuntimePath,
    string DotNetRoot,
    FileHandleIdentity SdkIdentity,
    FileHandleIdentity RuntimeIdentity,
    string SdkEntryPointPath,
    FileHandleIdentity SdkEntryPointIdentity,
    string SdkEntryPointSha256);

internal sealed record WorkspaceCheckSdkResolution(
    bool Success,
    string? Code,
    string? Message,
    WorkspaceCheckSdkSnapshot? Snapshot);

internal sealed record WorkspaceCheckSdkRevalidation(
    bool Success,
    string? Code,
    string? Message);

/// <summary>
/// Reads the nearest applicable global.json and selects an SDK from authenticated local
/// installation metadata or operator-approved portable versions without starting a process.
/// </summary>
internal static class WorkspaceCheckSdkResolver
{

    internal static WorkspaceCheckSdkResolution Resolve(
        string workspaceRoot,
        WorkspaceCheckExecutableSnapshot executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(executable);

        try
        {
            return ResolveCore(workspaceRoot, executable);
        }
        catch (JsonException)
        {
            return Failure(
                "invalid_global_json",
                "The workspace global.json is not valid JSON.");
        }
        catch (InvalidDataException ex)
        {
            return Failure("invalid_global_json", ex.Message);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(
                "sdk_unavailable",
                "The workspace SDK selection or pinned dotnet installation could not be read.");
        }
    }

    private static WorkspaceCheckSdkResolution ResolveCore(
        string workspaceRoot,
        WorkspaceCheckExecutableSnapshot executable)
    {
        string? dotnetRoot = CanonicalDirectory(executable.DotNetRoot);

        string? canonicalWorkspaceRoot = CanonicalDirectory(workspaceRoot);

        if (dotnetRoot is null || canonicalWorkspaceRoot is null)
        {
            return Failure(
                "sdk_unavailable",
                "The pinned dotnet installation or workspace root could not be canonicalized.");
        }

        GlobalJsonSelection selection = ReadGlobalJson(canonicalWorkspaceRoot);

        List<string> sdkRoots = [];

        foreach (string path in selection.SearchPaths ?? ["$host$"])
        {
            string? searchRoot = path == "$host$" ? dotnetRoot : CanonicalDirectory(path);

            if (searchRoot is null
                || !WorkspaceRootPath.IsWithinOrEqual(searchRoot, dotnetRoot))
            {
                return Failure(
                    "invalid_global_json",
                    "Workspace global.json selected an SDK search path outside the pinned trusted dotnet root.");
            }

            if (!PathComparer.Equals(searchRoot, dotnetRoot) && !SupportsAlternateSdkRoots(executable, dotnetRoot))
            {
                return Failure(
                    "invalid_global_json",
                    "Alternate global.json SDK search roots require a trusted .NET 10 or later host.");
            }

            string? sdkRoot = CanonicalDirectory(Path.Combine(searchRoot, "sdk"));

            if (sdkRoot is null)
            {
                continue;
            }

            if (!WorkspaceRootPath.IsWithinOrEqual(sdkRoot, dotnetRoot)
                || (executable.ImmutableOwnershipValidated && !HasImmutableOwnership(sdkRoot, dotnetRoot)))
            {
                return Failure(
                    "runtime_untrusted",
                    "An SDK search directory could not be trusted beneath the pinned dotnet root.");
            }

            sdkRoots.Add(sdkRoot);
        }

        string? selectedPath = null;
        SdkVersion selectedVersion = default;

        foreach (string sdkRoot in sdkRoots)
        {
            if (SelectVersion(
                    selection,
                    exact => File.Exists(Path.Combine(sdkRoot, exact.Original, "dotnet.dll")),
                    EnumerateInstalledSdkVersions(sdkRoot)) is not SdkVersion selected)
            {
                continue;
            }

            selectedPath = CanonicalDirectory(Path.Combine(sdkRoot, selected.Original));

            selectedVersion = selected;

            if (selectedPath is null
                || !PathComparer.Equals(Path.GetFileName(selectedPath), selectedVersion.Original)
                || !WorkspaceRootPath.IsWithinOrEqual(selectedPath, sdkRoot)
                || (executable.ImmutableOwnershipValidated && !HasImmutableOwnership(selectedPath, dotnetRoot)))
            {
                return Failure(
                    "runtime_untrusted",
                    "The selected SDK could not be trusted beneath the pinned SDK search directory.");
            }

            break;
        }

        if (selectedPath is null)
        {
            return Failure(
                "sdk_unavailable",
                "No compatible SDK is installed beneath the allowed SDK search roots of the pinned dotnet installation.");
        }

        string? runtimeVersion = ReadRuntimeVersion(selectedPath);

        if (runtimeVersion is null
            || !Directory.Exists(
                Path.Combine(
                    dotnetRoot,
                    "shared",
                    "Microsoft.NETCore.App",
                    runtimeVersion)))
        {

            runtimeVersion = SelectMatchingRuntimeVersion(
                dotnetRoot,
                selectedVersion);
        }

        if (runtimeVersion is null)
        {

            return Failure(
                "runtime_unavailable",
                $"No trusted runtime is installed for SDK '{selectedVersion.Original}'.");
        }

        string runtimeCandidate = Path.Combine(
            dotnetRoot,
            "shared",
            "Microsoft.NETCore.App",
            runtimeVersion);
        string? runtimePath = CanonicalDirectory(runtimeCandidate);
        string? sdkEntryPoint = CanonicalFile(
            Path.Combine(
                selectedPath,
                "dotnet.dll"));

        if (runtimePath is null
            || sdkEntryPoint is null
            || !WorkspaceRootPath.IsWithinOrEqual(
                runtimePath,
                dotnetRoot)
            || !WorkspaceRootPath.IsWithinOrEqual(
                sdkEntryPoint,
                selectedPath)
            || (executable.ImmutableOwnershipValidated
                && (!HasImmutableOwnership(
                        selectedPath,
                        dotnetRoot)
                    || !HasImmutableOwnership(
                        runtimePath,
                        dotnetRoot)
                    || !HasImmutableOwnership(
                        sdkEntryPoint,
                        dotnetRoot)))
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                selectedPath,
                out FileHandleMetadata sdkMetadata)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                runtimePath,
                out FileHandleMetadata runtimeMetadata)
            || !TryCaptureEntryPoint(
                sdkEntryPoint,
                out FileHandleMetadata entryPointMetadata,
                out string entryPointSha256))
        {

            return Failure(
                "runtime_untrusted",
                "The selected SDK or runtime identity could not be captured beneath the pinned dotnet root.");
        }

        return new WorkspaceCheckSdkResolution(
            true,
            null,
            null,
            new WorkspaceCheckSdkSnapshot(
                selectedVersion.Original,
                selectedVersion.Major,
                selectedPath,
                runtimeVersion,
                runtimePath,
                dotnetRoot,
                sdkMetadata.Identity,
                runtimeMetadata.Identity,
                sdkEntryPoint,
                entryPointMetadata.Identity,
                entryPointSha256));
    }

    internal static WorkspaceCheckSdkRevalidation Revalidate(
        WorkspaceCheckSdkSnapshot snapshot)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        string? sdkPath = CanonicalDirectory(snapshot.SdkPath);
        string? runtimePath = CanonicalDirectory(snapshot.RuntimePath);
        string? sdkEntryPoint = CanonicalFile(
            snapshot.SdkEntryPointPath);

        if (sdkPath is null
            || runtimePath is null
            || sdkEntryPoint is null
            || !PathComparer.Equals(sdkPath, snapshot.SdkPath)
            || !PathComparer.Equals(runtimePath, snapshot.RuntimePath)
            || !PathComparer.Equals(
                sdkEntryPoint,
                snapshot.SdkEntryPointPath)
            || !WorkspaceRootPath.IsWithinOrEqual(
                sdkPath,
                snapshot.DotNetRoot)
            || !WorkspaceRootPath.IsWithinOrEqual(
                runtimePath,
                snapshot.DotNetRoot)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                sdkPath,
                out FileHandleMetadata sdkMetadata)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                runtimePath,
                out FileHandleMetadata runtimeMetadata)
            || !TryCaptureEntryPoint(
                sdkEntryPoint,
                out FileHandleMetadata entryPointMetadata,
                out string entryPointSha256)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.SdkIdentity,
                sdkMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.RuntimeIdentity,
                runtimeMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.SdkEntryPointIdentity,
                entryPointMetadata.Identity)
            || !string.Equals(
                snapshot.SdkEntryPointSha256,
                entryPointSha256,
                StringComparison.Ordinal))
        {

            return new WorkspaceCheckSdkRevalidation(
                false,
                "sdk_changed",
                "The selected SDK or runtime identity changed before process start.");
        }

        return new WorkspaceCheckSdkRevalidation(true, null, null);
    }

    private static bool TryCaptureEntryPoint(
        string path,
        out FileHandleMetadata metadata,
        out string sha256)
    {
        metadata = default;
        sha256 = string.Empty;

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata before)
                || before.Kind != FileSystemObjectKind.RegularFile
                || before.HardLinkCount != 1)
            {
                return false;
            }

            byte[] hash = SHA256.HashData(stream);
            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata after)
                || after.Kind != FileSystemObjectKind.RegularFile
                || after.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    before.Identity,
                    after.Identity))
            {
                return false;
            }

            metadata = after;
            sha256 = Convert.ToHexString(hash);
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
    }

    private static bool SupportsAlternateSdkRoots(WorkspaceCheckExecutableSnapshot executable, string dotnetRoot)
    {
        string? fxrRoot = CanonicalDirectory(Path.Combine(dotnetRoot, "host", "fxr"));

        if (fxrRoot is null
            || !WorkspaceRootPath.IsWithinOrEqual(fxrRoot, dotnetRoot)
            || (executable.ImmutableOwnershipValidated && !HasImmutableOwnership(fxrRoot, dotnetRoot)))
        {
            return false;
        }

        SdkVersion latest = Directory.EnumerateDirectories(fxrRoot)
            .Select(Path.GetFileName)
            .Select(static version => SdkVersion.TryParse(version!, out SdkVersion parsed) ? parsed : default)
            .DefaultIfEmpty()
            .Max();

        if (latest.Major < 10 || (latest.Major == 10 && latest.IsPrerelease))
        {
            return false;
        }

        string libraryName = OperatingSystem.IsWindows() ? "hostfxr.dll"
            : OperatingSystem.IsMacOS() ? "libhostfxr.dylib" : "libhostfxr.so";

        string? versionRoot = CanonicalDirectory(Path.Combine(fxrRoot, latest.Original));

        string? library = versionRoot is null ? null : CanonicalFile(Path.Combine(versionRoot, libraryName));

        return library is not null
            && WorkspaceRootPath.IsWithinOrEqual(library, fxrRoot)
            && (!executable.ImmutableOwnershipValidated || HasImmutableOwnership(library, dotnetRoot));
    }

    internal static (string? Version, string? Code, string? Message) ResolvePortableVersion(
        string canonicalWorkspaceRoot,
        IReadOnlyList<SdkVersion> configuredVersions)
    {
        GlobalJsonSelection selection;

        try
        {
            selection = ReadGlobalJson(canonicalWorkspaceRoot);
        }
        catch (JsonException)
        {
            return (null, "invalid_global_json", "The workspace global.json is not valid JSON.");
        }
        catch (InvalidDataException ex)
        {
            return (null, "invalid_global_json", ex.Message);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, "invalid_global_json", "The workspace global.json could not be safely read.");
        }

        if (selection.SearchPaths is { } paths && paths.Any(static path => path != "$host$"))
        {
            return (
                null, "invalid_global_json",
                "Portable .NET checks require sdk.paths to be omitted or to contain only the virtual '$host$' root.");
        }

        SdkVersion? selected = selection.SearchPaths is { Length: 0 }
            ? null
            : SelectVersion(
                selection,
                exact => configuredVersions.Any(version => version.Original == exact.Original),
                configuredVersions);

        return selected is SdkVersion version
            ? (version.Original, null, null)
            : (null, "sdk_unavailable", "No configured portable .NET SDK image matches the workspace global.json selection.");
    }

    private static IEnumerable<SdkVersion> EnumerateInstalledSdkVersions(string sdkRoot)
    {
        foreach (string path in Directory.EnumerateDirectories(sdkRoot))
        {
            if (SdkVersion.TryParse(Path.GetFileName(path), out SdkVersion version)
                && File.Exists(Path.Combine(path, "dotnet.dll")))
            {
                yield return version;
            }
        }
    }

    private static SdkVersion? SelectVersion(
        GlobalJsonSelection selection,
        Func<SdkVersion, bool> hasExact,
        IEnumerable<SdkVersion> versions)
    {
        string policy = selection.RollForward?.ToLowerInvariant()
            ?? (selection.RequestedVersion.HasValue ? "patch" : "latestmajor");

        SdkVersion? requested = selection.RequestedVersion;

        // The muxer checks the requested directory first under these two policies.
        if (requested is SdkVersion exact && policy is "disable" or "patch")
        {
            if (hasExact(exact))
            {
                return exact;
            }

            if (policy is "disable")
            {
                return null;
            }
        }

        bool allowPrerelease = requested is { IsPrerelease: true }
            || selection.AllowPrerelease is not false;

        bool preferLatest = policy.StartsWith("latest", StringComparison.Ordinal);

        SdkVersion? best = null;

        foreach (SdkVersion candidate in versions)
        {
            if ((!allowPrerelease && candidate.IsPrerelease)
                || (requested is SdkVersion minimum && !MatchesPolicy(candidate, minimum, policy)))
            {
                continue;
            }

            if (best is not SdkVersion previous
                || (preferLatest || candidate.Band == previous.Band
                    ? candidate.CompareTo(previous) > 0
                    : candidate.Band.CompareTo(previous.Band) < 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool MatchesPolicy(SdkVersion candidate, SdkVersion minimum, string policy)
    {
        if (candidate.CompareTo(minimum) < 0)
        {
            return false;
        }

        return policy switch
        {
            "patch" or "latestpatch" => candidate.Band == minimum.Band,
            "feature" or "latestfeature" => candidate.Major == minimum.Major
                && candidate.Minor == minimum.Minor,
            "minor" or "latestminor" => candidate.Major == minimum.Major,
            _ => true,
        };
    }

    private static GlobalJsonSelection ReadGlobalJson(string workspaceRoot)
    {

        string current = workspaceRoot;

        while (true)
        {
            string candidate = Path.Combine(current, "global.json");

            SecureFileOpenStatus openStatus = SecureFileReader.TryOpenRegularFile(
                candidate, expectedIdentity: null, out FileStream? stream, out FileHandleMetadata opened);

            if (openStatus == SecureFileOpenStatus.Success)
            {
                using (stream!)
                {
                    return ParseGlobalJson(
                        candidate,
                        ReadGlobalJsonSnapshot(candidate, stream!, opened.Identity));
                }
            }

            if (openStatus != SecureFileOpenStatus.NotFound)
            {
                throw new InvalidDataException(
                    "The workspace global.json could not be safely opened as a regular file.");
            }

            string? parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent) || PathComparer.Equals(parent, current))
            {
                return new GlobalJsonSelection(null, null, null, null);
            }

            current = parent;
        }
    }

    private static byte[] ReadGlobalJsonSnapshot(string path, FileStream stream, FileHandleIdentity identity)
    {
        const int maxGlobalJsonBytes = 64 * 1024;

        if (stream.Length > maxGlobalJsonBytes)
        {
            throw new InvalidDataException("The workspace global.json exceeds the allowed size.");
        }

        byte[] snapshot = new byte[checked((int)stream.Length)];

        stream.ReadExactly(snapshot);

        if (SecureFileReader.TryOpenRegularFile(
                path, identity, out FileStream? verification, out _) != SecureFileOpenStatus.Success)
        {
            throw new InvalidDataException("The workspace global.json changed while it was being snapshotted.");
        }

        using (verification!)
        {
            byte[] check = new byte[snapshot.Length];

            verification!.ReadExactly(check);

            if (stream.Length != snapshot.Length
                || verification.Length != snapshot.Length
                || !snapshot.AsSpan().SequenceEqual(check))
            {
                throw new InvalidDataException("The workspace global.json changed while it was being snapshotted.");
            }
        }

        return snapshot;
    }

    private static GlobalJsonSelection ParseGlobalJson(string path, byte[] snapshot)
    {
        using MemoryStream bytes = new(snapshot, writable: false);

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workspace global.json must contain a JSON object.");
        }

        if (!TryGetUniqueProperty(document.RootElement, "sdk", out JsonElement sdk)
            || sdk.ValueKind == JsonValueKind.Null)
        {
            return new GlobalJsonSelection(null, null, null, null);
        }

        if (sdk.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workspace global.json SDK selection must be a JSON object.");
        }

        List<string>? searchPaths = null;

        if (TryGetUniqueProperty(sdk, "paths", out JsonElement paths)
            && paths.ValueKind != JsonValueKind.Null)
        {
            if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() > 16)
            {
                throw new InvalidDataException(
                    "Workspace global.json contains invalid SDK search paths.");
            }

            searchPaths = [];

            string globalJsonDirectory = Path.GetDirectoryName(path)!;

            foreach (JsonElement element in paths.EnumerateArray())
            {
                string? value = element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
                {
                    throw new InvalidDataException(
                        "Workspace global.json contains an invalid SDK search path.");
                }

                searchPaths.Add(
                    value == "$host$" ? value : Path.GetFullPath(value, globalJsonDirectory));
            }
        }

        SdkVersion? requested = null;

        if (ReadSdkString(sdk, "version") is string requestedText)
        {
            if (!SdkVersion.TryParse(requestedText, out SdkVersion parsed) || parsed.Patch < 100)
            {
                throw new InvalidDataException(
                    "Workspace global.json contains an invalid SDK version.");
            }

            requested = parsed;
        }

        string? rollForward = ReadSdkString(sdk, "rollForward");

        bool? allowPrerelease = null;

        if (TryGetUniqueProperty(sdk, "allowPrerelease", out JsonElement prerelease)
            && prerelease.ValueKind != JsonValueKind.Null)
        {
            if (prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    "Workspace global.json SDK allowPrerelease must be a boolean.");
            }

            allowPrerelease = prerelease.GetBoolean();
        }

        string[] allowedRollForward =
        [
            "disable",
            "patch",
            "feature",
            "minor",
            "major",
            "latestPatch",
            "latestFeature",
            "latestMinor",
            "latestMajor",
        ];

        if (rollForward is not null
            && !allowedRollForward.Contains(
                rollForward,
                StringComparer.OrdinalIgnoreCase))
        {

            throw new InvalidDataException(
                "Workspace global.json contains an unsupported rollForward policy.");
        }

        if (rollForward is not null && requested is null
            && !string.Equals(rollForward, "latestMajor", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Workspace global.json rollForward policy requires a version.");
        }

        return new GlobalJsonSelection(
            requested,
            rollForward,
            allowPrerelease,
            searchPaths?.ToArray());
    }

    private static string? ReadSdkString(JsonElement sdk, string propertyName)
    {
        if (!TryGetUniqueProperty(sdk, propertyName, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Workspace global.json SDK {propertyName} must be a string.");
        }

        return property.GetString();
    }

    private static bool TryGetUniqueProperty(
        JsonElement source,
        string name,
        out JsonElement value)
    {
        value = default;

        bool found = false;

        foreach (JsonProperty candidate in source.EnumerateObject())
        {
            if (!candidate.NameEquals(name))
            {
                continue;
            }

            if (found)
            {
                throw new InvalidDataException($"Workspace global.json contains multiple '{name}' values.");
            }

            found = true;

            value = candidate.Value;
        }

        return found;
    }

    private static string? ReadRuntimeVersion(string sdkPath)
    {

        string versionFile = Path.Combine(sdkPath, ".version");

        try
        {
            if (SecureFileReader.TryOpenRegularFile(
                    versionFile, expectedIdentity: null, out FileStream? stream, out _) != SecureFileOpenStatus.Success)
            {
                return null;
            }

            using (stream!)
            {
                byte[] bytes = new byte[4096];

                int length = stream!.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);

                if (length == bytes.Length)
                {
                    return null;
                }

                string[] lines = System.Text.Encoding.UTF8.GetString(bytes, 0, length).Split('\n');

                foreach (string line in lines.Reverse())
                {
                    string candidate = line.Trim();

                    if (SdkVersion.TryParse(candidate, out _))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {

            return null;
        }
    }

    private static string? SelectMatchingRuntimeVersion(
        string dotnetRoot,
        SdkVersion sdk)
    {

        string root = Path.Combine(
            dotnetRoot,
            "shared",
            "Microsoft.NETCore.App");

        if (!Directory.Exists(root))
        {

            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .Select(static version =>
                SdkVersion.TryParse(version!, out SdkVersion parsed)
                    ? parsed
                    : (SdkVersion?)null)
            .Where(static version =>
                version is not null
                && !version.Value.IsPrerelease)
            .Select(static version => version!.Value)
            .Where(version =>
                version.Major == sdk.Major
                && version.Minor == sdk.Minor)
            .OrderByDescending(static version => version)
            .Select(static version => version.Original)
            .FirstOrDefault();
    }

    internal static string? CanonicalDirectory(string path)
        => CanonicalDirectory(path, resolutionDepth: 0);

    private static string? CanonicalFile(string path)
    {
        try
        {
            FileInfo file = new(path);

            if (!file.Exists)
            {
                return null;
            }

            FileSystemInfo? target = file.ResolveLinkTarget(
                returnFinalTarget: true);
            string canonical = Path.GetFullPath(
                target?.FullName
                ?? file.FullName);

            return File.Exists(canonical)
                ? canonical
                : null;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static bool HasImmutableOwnership(
        string path,
        string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        string? current = path;

        try
        {
            while (!string.IsNullOrEmpty(current)
                   && WorkspaceRootPath.IsWithinOrEqual(
                       current,
                       root))
            {
                if (!FileHandleIdentityInterop.TryGetUnixOwnerUserId(
                        current,
                        out uint owner)
                    || owner != 0
                    || (File.GetUnixFileMode(current)
                        & (UnixFileMode.GroupWrite
                           | UnixFileMode.OtherWrite)) != 0)
                {
                    return false;
                }

                if (PathComparer.Equals(current, root))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or PlatformNotSupportedException)
        {
            return false;
        }

        return false;
    }

    private static string? CanonicalDirectory(
        string path,
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

            foreach (string component in components)
            {

                string candidate = Path.Combine(current, component);
                DirectoryInfo directory = new(candidate);

                if (!directory.Exists)
                {

                    return null;
                }

                FileSystemInfo? target = directory.ResolveLinkTarget(
                    returnFinalTarget: true);
                current = target is null
                    ? Path.GetFullPath(candidate)
                    : CanonicalDirectory(
                        target.FullName,
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

    private static WorkspaceCheckSdkResolution Failure(
        string code,
        string message) =>
        new(false, code, message, null);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record GlobalJsonSelection(
        SdkVersion? RequestedVersion,
        string? RollForward,
        bool? AllowPrerelease,
        string[]? SearchPaths);
}
