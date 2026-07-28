using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckSdkSnapshot(
    string Version,
    string SdkPath,
    string RuntimeVersion,
    string RuntimePath,
    string DotNetRoot,
    FileHandleIdentity SdkIdentity,
    FileHandleIdentity RuntimeIdentity,
    string SdkEntryPointPath,
    FileHandleIdentity SdkEntryPointIdentity);

internal sealed record WorkspaceCheckSdkResolution(
    bool Success,
    string? Code,
    string? Message,
    WorkspaceCheckSdkSnapshot? Snapshot);

internal sealed record WorkspaceCheckSdkRevalidation(
    bool Success,
    string? Code,
    string? Message);

internal sealed record WorkspaceCheckMuxerSdkSelection(
    string Version,
    string BasePath);

/// <summary>
/// Resolves the SDK selected by the workspace's root global.json from the pinned dotnet
/// installation only. Workspace-provided SDK search paths and uninstalled selections fail closed.
/// </summary>
internal static class WorkspaceCheckSdkResolver
{

    internal static WorkspaceCheckSdkResolution Resolve(
        string workspaceRoot,
        WorkspaceCheckExecutableSnapshot executable,
        Func<string, WorkspaceCheckExecutableSnapshot,
            WorkspaceCheckMuxerSdkSelection?>? muxerResolver = null,
        string? resolverRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(executable);

        bool ownsResolverRoot =
            string.IsNullOrWhiteSpace(resolverRoot);
        string effectiveResolverRoot = ownsResolverRoot
            ? Path.Combine(
                Path.GetTempPath(),
                $"arcanum-sdk-resolution-{Guid.NewGuid():N}")
            : Path.GetFullPath(resolverRoot!);

        try
        {
            CreateOwnerOnlyDirectory(effectiveResolverRoot);
            string? canonicalResolverRoot =
                CanonicalDirectory(effectiveResolverRoot);
            string? canonicalWorkspaceRoot =
                CanonicalDirectory(workspaceRoot);

            if (canonicalResolverRoot is null
                || canonicalWorkspaceRoot is null
                || WorkspaceRootPath.IsWithinOrEqual(
                    canonicalResolverRoot,
                    canonicalWorkspaceRoot))
            {
                return Failure(
                    "sdk_unavailable",
                    "The server-owned SDK resolver root must be canonical and outside the workspace.");
            }

            return ResolveCore(
                workspaceRoot,
                executable,
                muxerResolver,
                canonicalResolverRoot);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return Failure(
                "sdk_unavailable",
                "A server-owned SDK resolver root could not be prepared.");
        }
        finally
        {
            if (ownsResolverRoot)
            {
                TryDeleteDirectory(effectiveResolverRoot);
            }
        }
    }

    private static WorkspaceCheckSdkResolution ResolveCore(
        string workspaceRoot,
        WorkspaceCheckExecutableSnapshot executable,
        Func<string, WorkspaceCheckExecutableSnapshot,
            WorkspaceCheckMuxerSdkSelection?>? muxerResolver,
        string resolverRoot)
    {
        string dotnetRoot = CanonicalDirectory(executable.DotNetRoot) ?? string.Empty;

        if (dotnetRoot.Length == 0)
        {

            return Failure(
                "sdk_unavailable",
                "The pinned dotnet installation root could not be canonicalized.");
        }

        GlobalJsonSelection selection;

        try
        {

            selection = ReadGlobalJson(workspaceRoot);
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

        List<string> sdkRoots = [];
        List<string> sanitizedSearchRoots = [];

        if (selection.SearchPaths.Length == 0)
        {

            sdkRoots.Add(Path.Combine(dotnetRoot, "sdk"));
            sanitizedSearchRoots.Add(dotnetRoot);
        }
        else
        {

            foreach (string configuredSearchPath in selection.SearchPaths)
            {

                string candidateRoot = configuredSearchPath == "$host$"
                    ? dotnetRoot
                    : configuredSearchPath;
                string? canonicalSearchRoot =
                    CanonicalDirectory(candidateRoot);

                if (canonicalSearchRoot is null
                    || !WorkspaceRootPath.IsWithinOrEqual(
                        canonicalSearchRoot,
                        dotnetRoot))
                {

                    return Failure(
                        "invalid_global_json",
                        "Workspace global.json selected an SDK search path outside the pinned trusted dotnet root.");

                }

                string sdkSearchRoot =
                    string.Equals(
                        Path.GetFileName(canonicalSearchRoot),
                        "sdk",
                        StringComparison.OrdinalIgnoreCase)
                        ? canonicalSearchRoot
                        : Path.Combine(canonicalSearchRoot, "sdk");
                sdkRoots.Add(sdkSearchRoot);
                sanitizedSearchRoots.Add(
                    canonicalSearchRoot);
            }

        }

        if (!WriteSanitizedGlobalJson(
                resolverRoot,
                selection,
                sanitizedSearchRoots))
        {
            return Failure(
                "invalid_global_json",
                "The validated global.json snapshot could not be materialized in the server-owned resolver root.");
        }

        WorkspaceCheckMuxerSdkSelection? muxerSelection =
            (muxerResolver ?? ProbeAuthoritativeSdk)(
                resolverRoot,
                executable);
        string? selectedPath = muxerSelection is null
            ? null
            : CanonicalDirectory(muxerSelection.BasePath);

        if (muxerSelection is null
            || selectedPath is null
            || !SdkVersion.TryParse(
                muxerSelection.Version,
                out SdkVersion selectedVersion)
            || !PathComparer.Equals(
                Path.GetFileName(selectedPath),
                muxerSelection.Version)
            || !WorkspaceRootPath.IsWithinOrEqual(
                selectedPath,
                dotnetRoot)
            || !sdkRoots.Any(root =>
                WorkspaceRootPath.IsWithinOrEqual(
                    selectedPath,
                    Path.GetFullPath(root))))
        {

            return Failure(
                "sdk_unavailable",
                "The trusted dotnet muxer did not select a canonical SDK beneath the allowed SDK search roots.");
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
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                sdkEntryPoint,
                out FileHandleMetadata entryPointMetadata))
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
                selectedPath,
                runtimeVersion,
                runtimePath,
                dotnetRoot,
                sdkMetadata.Identity,
                runtimeMetadata.Identity,
                sdkEntryPoint,
                entryPointMetadata.Identity));
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
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                sdkEntryPoint,
                out FileHandleMetadata entryPointMetadata)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.SdkIdentity,
                sdkMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.RuntimeIdentity,
                runtimeMetadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.SdkEntryPointIdentity,
                entryPointMetadata.Identity))
        {

            return new WorkspaceCheckSdkRevalidation(
                false,
                "sdk_changed",
                "The selected SDK or runtime identity changed before process start.");
        }

        return new WorkspaceCheckSdkRevalidation(true, null, null);
    }

    private static bool WriteSanitizedGlobalJson(
        string resolverRoot,
        GlobalJsonSelection selection,
        IReadOnlyList<string> searchRoots)
    {
        try
        {
            string path = Path.Combine(
                resolverRoot,
                "global.json");
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using (Utf8JsonWriter writer = new(
                       stream,
                       new JsonWriterOptions
                       {
                           Indented = true,
                       }))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("sdk");

                if (selection.RequestedVersion is not null)
                {
                    writer.WriteString(
                        "version",
                        selection.RequestedVersion);
                }

                if (selection.RollForward is not null)
                {
                    writer.WriteString(
                        "rollForward",
                        selection.RollForward);
                }

                if (selection.AllowPrerelease is bool allowPrerelease)
                {
                    writer.WriteBoolean(
                        "allowPrerelease",
                        allowPrerelease);
                }

                writer.WriteStartArray("paths");

                foreach (string root in searchRoots)
                {
                    writer.WriteStringValue(root);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            SecureFilePermissions.ApplyOwnerOnlyFile(path);
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

    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);
        SecureFilePermissions.ApplyOwnerOnlyDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static WorkspaceCheckMuxerSdkSelection?
        ProbeAuthoritativeSdk(
            string workspaceRoot,
            WorkspaceCheckExecutableSnapshot executable)
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-sdk-probe-{Guid.NewGuid():N}");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(tempRoot);
            }
            else
            {
                Directory.CreateDirectory(
                    tempRoot,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            System.Diagnostics.ProcessStartInfo startInfo = new()
            {
                FileName = executable.CanonicalPath,
                WorkingDirectory = Path.GetFullPath(workspaceRoot),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--info");
            startInfo.Environment.Clear();
            startInfo.Environment["DOTNET_ROOT"] =
                executable.DotNetRoot;
            startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment[
                "DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment[
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] =
                "en-US";
            startInfo.Environment["HOME"] = tempRoot;
            startInfo.Environment["DOTNET_CLI_HOME"] = tempRoot;
            startInfo.Environment["TMPDIR"] = tempRoot;
            startInfo.Environment["TMP"] = tempRoot;
            startInfo.Environment["TEMP"] = tempRoot;

            CappedChildProcessRunResult result =
                CappedChildProcessRunner.RunAsync(
                        startInfo,
                        ChildProcessEnvironmentProfile.WorkspaceCheck,
                        totalOutputCapBytes: 64 * 1024,
                        timeout: TimeSpan.FromSeconds(5),
                        resourceLimits: null,
                        resourceLimiter: null,
                        CancellationToken.None,
                        preStartValidation: () =>
                        {
                            string? current = CanonicalFile(
                                executable.CanonicalPath);

                            if (current is null
                                || !FileHandleIdentityInterop
                                    .TryGetPathMetadata(
                                        current,
                                        out FileHandleMetadata metadata)
                                || !FileHandleIdentity.IdentitiesMatch(
                                    executable.Identity,
                                    metadata.Identity))
                            {
                                return new CappedChildProcessPreStartValidationResult(
                                    false,
                                    "The dotnet muxer changed before SDK resolution.",
                                    "executable_changed");
                            }

                            return new CappedChildProcessPreStartValidationResult(
                                true,
                                null);
                        })
                    .GetAwaiter()
                    .GetResult();

            if (result.Outcome
                    != CappedChildProcessOutcome.Completed
                || result.ExitCode != 0)
            {
                return null;
            }

            string? basePath = result.Stdout.Text
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .Where(static line =>
                    line.StartsWith(
                        "Base Path:",
                        StringComparison.OrdinalIgnoreCase))
                .Select(static line =>
                    line["Base Path:".Length..].Trim())
                .FirstOrDefault();
            string? canonicalBase = string.IsNullOrWhiteSpace(basePath)
                ? null
                : CanonicalDirectory(basePath);

            return canonicalBase is null
                ? null
                : new WorkspaceCheckMuxerSdkSelection(
                    Path.GetFileName(canonicalBase),
                    canonicalBase);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(
                        tempRoot,
                        recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private static GlobalJsonSelection ReadGlobalJson(string workspaceRoot)
    {

        string current = Path.GetFullPath(workspaceRoot);
        string? path = null;

        while (true)
        {

            string candidate = Path.Combine(current, "global.json");

            if (File.Exists(candidate))
            {

                path = candidate;
                break;
            }

            string? parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent)
                || PathComparer.Equals(parent, current))
            {

                break;
            }

            current = parent;
        }

        if (path is null)
        {

            return new GlobalJsonSelection(
                RequestedVersion: null,
                RollForward: null,
                AllowPrerelease: null,
                SearchPaths: []);
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length > 64 * 1024
            || !FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata opened))
        {
            throw new InvalidDataException(
                "The workspace global.json could not be safely snapshotted.");
        }

        using MemoryStream snapshot = new(
            checked((int)stream.Length));
        stream.CopyTo(snapshot);

        using FileStream verification = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        byte[] snapshotBytes = snapshot.ToArray();

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                verification.SafeFileHandle,
                out FileHandleMetadata after)
            || !FileHandleIdentity.IdentitiesMatch(
                opened.Identity,
                after.Identity)
            || snapshot.Length != stream.Length
            || verification.Length != snapshot.Length
            || !SHA256.HashData(snapshotBytes)
                .AsSpan()
                .SequenceEqual(
                    SHA256.HashData(verification)))
        {
            throw new InvalidDataException(
                "The workspace global.json changed while it was being snapshotted.");
        }

        snapshot.Position = 0;
        using JsonDocument document = JsonDocument.Parse(
            snapshot,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });

        if (!document.RootElement.TryGetProperty("sdk", out JsonElement sdk)
            || sdk.ValueKind != JsonValueKind.Object)
        {

            return new GlobalJsonSelection(null, null, null, []);
        }

        List<string> searchPaths = [];

        if (sdk.TryGetProperty("paths", out JsonElement paths))
        {

            if (paths.ValueKind != JsonValueKind.Array
                || paths.GetArrayLength() > 16)
            {

                throw new InvalidDataException(
                    "Workspace global.json contains invalid SDK search paths.");
            }

            string globalJsonDirectory = Path.GetDirectoryName(path)!;

            foreach (JsonElement element in paths.EnumerateArray())
            {

                string? value = element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(value))
                {

                    throw new InvalidDataException(
                        "Workspace global.json contains an invalid SDK search path.");
                }

                searchPaths.Add(
                    string.Equals(value, "$host$", StringComparison.Ordinal)
                        ? "$host$"
                        : Path.GetFullPath(value, globalJsonDirectory));
            }
        }

        string? requested = sdk.TryGetProperty(
                "version",
                out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;
        string? rollForward = sdk.TryGetProperty(
                "rollForward",
                out JsonElement rollForwardElement)
            && rollForwardElement.ValueKind == JsonValueKind.String
                ? rollForwardElement.GetString()
                : null;
        bool? allowPrerelease = sdk.TryGetProperty(
                "allowPrerelease",
                out JsonElement prereleaseElement)
            && prereleaseElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? prereleaseElement.GetBoolean()
                : null;

        if (requested is not null && !SdkVersion.TryParse(requested, out _))
        {

            throw new InvalidDataException(
                "Workspace global.json contains an invalid SDK version.");
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

        return new GlobalJsonSelection(
            requested,
            rollForward,
            allowPrerelease,
            searchPaths.ToArray());
    }

    private static string? ReadRuntimeVersion(string sdkPath)
    {

        string versionFile = Path.Combine(sdkPath, ".version");

        if (!File.Exists(versionFile))
        {

            return null;
        }

        try
        {

            string[] lines = File.ReadAllLines(versionFile);

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

    private static string? CanonicalDirectory(string path)
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
        string? RequestedVersion,
        string? RollForward,
        bool? AllowPrerelease,
        string[] SearchPaths);

    private readonly record struct SdkVersion(
        int Major,
        int Minor,
        int Patch,
        string? Prerelease,
        string Original) : IComparable<SdkVersion>
    {

        internal bool IsPrerelease => Prerelease is not null;

        public int CompareTo(SdkVersion other)
        {

            int value = Major.CompareTo(other.Major);

            if (value != 0)
            {

                return value;
            }

            value = Minor.CompareTo(other.Minor);

            if (value != 0)
            {

                return value;
            }

            value = Patch.CompareTo(other.Patch);

            if (value != 0)
            {

                return value;
            }

            if (Prerelease is null && other.Prerelease is not null)
            {

                return 1;
            }

            if (Prerelease is not null && other.Prerelease is null)
            {

                return -1;
            }

            return string.Compare(
                Prerelease,
                other.Prerelease,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryParse(
            string value,
            out SdkVersion version)
        {

            version = default;

            if (string.IsNullOrWhiteSpace(value))
            {

                return false;
            }

            string[] suffix = value.Split('-', 2);
            string[] parts = suffix[0].Split('.');

            if (parts.Length != 3
                || !int.TryParse(
                    parts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int major)
                || !int.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int minor)
                || !int.TryParse(
                    parts[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int patch)
                || major < 0
                || minor < 0
                || patch < 0)
            {

                return false;
            }

            version = new SdkVersion(
                major,
                minor,
                patch,
                suffix.Length == 2 ? suffix[1] : null,
                value);
            return true;
        }
    }
}
