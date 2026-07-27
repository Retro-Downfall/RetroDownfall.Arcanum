using System.Buffers.Binary;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckExecutableSnapshot(
    string CanonicalPath,
    string DotNetRoot,
    FileHandleIdentity Identity,
    long Length,
    long LastWriteUtcTicks,
    bool ImmutableOwnershipValidated);

internal sealed record WorkspaceCheckExecutableCapture(
    bool Success,
    string? Code,
    string? Message,
    WorkspaceCheckExecutableSnapshot? Snapshot);

internal sealed record WorkspaceCheckExecutableRevalidation(
    bool Success,
    string? Code,
    string? Message);

/// <summary>
/// Runtime executable boundary for workspace checks. Resolution never consults PATH, and every
/// spawn must call <see cref="Revalidate"/> immediately before handing the process to the runner.
/// </summary>
internal sealed class WorkspaceCheckExecutableRuntimePolicy
{

    private readonly string[] _trustedRoots;

    private readonly bool _requireImmutableOwnership;

    private WorkspaceCheckExecutableRuntimePolicy(
        IEnumerable<string> trustedRoots,
        bool requireImmutableOwnership)
    {

        _trustedRoots = trustedRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        _requireImmutableOwnership = requireImmutableOwnership;
    }

    internal static WorkspaceCheckExecutableRuntimePolicy ForCurrentPlatform() =>
        new(
            GetPlatformTrustedRoots(),
            requireImmutableOwnership: true);

    internal static WorkspaceCheckExecutableRuntimePolicy ForTrustedRoots(
        IEnumerable<string> trustedRoots,
        bool requireImmutableOwnership = false) =>
        new(trustedRoots, requireImmutableOwnership);

    internal WorkspaceCheckExecutableCapture Capture(
        string executablePath,
        string workspaceRoot)
    {

        if (!Path.IsPathFullyQualified(executablePath)
            || !Path.IsPathFullyQualified(workspaceRoot))
        {

            return Failure("invalid_executable", "The executable and workspace paths must be absolute.");
        }

        if (!TryCanonicalizeExistingPath(
                executablePath,
                expectDirectory: false,
                out string? canonicalExecutable)
            || canonicalExecutable is null)
        {

            return Failure(
                "executable_unavailable",
                "The pinned dotnet executable is missing or could not be canonicalized.");
        }

        if (!TryCanonicalizeExistingPath(
                workspaceRoot,
                expectDirectory: true,
                out string? canonicalWorkspace)
            || canonicalWorkspace is null)
        {

            return Failure(
                "invalid_workspace",
                "The workspace root could not be canonicalized.");
        }

        if (WorkspaceRootPath.IsWithinOrEqual(
                Path.GetFullPath(executablePath),
                canonicalWorkspace)
            || WorkspaceRootPath.IsWithinOrEqual(
                canonicalExecutable,
                canonicalWorkspace))
        {

            return Failure(
                "untrusted_executable",
                "The workspace-check executable must remain outside the source workspace.");
        }

        if (!HasNativeDotNetFileName(canonicalExecutable)
            || !HasPlatformNativeExecutableHeader(canonicalExecutable)
            || !HasExecutableMode(canonicalExecutable))
        {

            return Failure(
                "untrusted_executable",
                "The pinned workspace-check executable is not a native executable dotnet host.");
        }

        if (!TryGetMatchingTrustedRoot(
                canonicalExecutable,
                out string? trustedRoot)
            || trustedRoot is null
            || (_requireImmutableOwnership
                && !HasImmutableOwnership(
                    canonicalExecutable,
                    trustedRoot)))
        {

            return Failure(
                "untrusted_executable",
                "The pinned workspace-check executable is outside the canonical trusted installation roots.");
        }

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                canonicalExecutable,
                out FileHandleMetadata metadata))
        {

            return Failure(
                "identity_unavailable",
                "The pinned workspace-check executable identity could not be captured.");
        }

        FileInfo info = new(canonicalExecutable);
        string dotnetRoot = FindDotNetRoot(info.DirectoryName!);

        return new WorkspaceCheckExecutableCapture(
            true,
            null,
            null,
            new WorkspaceCheckExecutableSnapshot(
                canonicalExecutable,
                dotnetRoot,
                metadata.Identity,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                _requireImmutableOwnership));
    }

    internal WorkspaceCheckExecutableRevalidation Revalidate(
        WorkspaceCheckExecutableSnapshot snapshot,
        string workspaceRoot)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        WorkspaceCheckExecutableCapture current = Capture(
            snapshot.CanonicalPath,
            workspaceRoot);

        if (!current.Success || current.Snapshot is null)
        {

            return new WorkspaceCheckExecutableRevalidation(
                false,
                current.Code ?? "executable_changed",
                current.Message ?? "The pinned executable no longer passes runtime validation.");
        }

        WorkspaceCheckExecutableSnapshot actual = current.Snapshot;

        if (!PathComparer.Equals(snapshot.CanonicalPath, actual.CanonicalPath)
            || !FileHandleIdentity.IdentitiesMatch(snapshot.Identity, actual.Identity)
            || snapshot.Length != actual.Length
            || snapshot.LastWriteUtcTicks != actual.LastWriteUtcTicks)
        {

            return new WorkspaceCheckExecutableRevalidation(
                false,
                "executable_changed",
                "The pinned executable identity changed after capability validation.");
        }

        return new WorkspaceCheckExecutableRevalidation(true, null, null);
    }

    internal WorkspaceCheckExecutableCapture ResolveConfiguredOrInstalled(
        string? configuredPath,
        string workspaceRoot)
    {

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {

            return Capture(configuredPath.Trim(), workspaceRoot);
        }

        foreach (string candidate in GetInstalledDotNetCandidates())
        {

            WorkspaceCheckExecutableCapture captured = Capture(
                candidate,
                workspaceRoot);

            if (captured.Success)
            {

                return captured;
            }

        }

        return Failure(
            "executable_unavailable",
            "No native dotnet host was found beneath the canonical trusted installation roots.");
    }

    private bool TryGetMatchingTrustedRoot(
        string canonicalExecutable,
        out string? matchingRoot)
    {

        matchingRoot = null;

        foreach (string root in _trustedRoots)
        {

            if (TryCanonicalizeExistingPath(
                    root,
                    expectDirectory: true,
                    out string? canonicalRoot)
                && canonicalRoot is not null
                && WorkspaceRootPath.IsWithinOrEqual(
                    canonicalExecutable,
                    canonicalRoot))
            {

                matchingRoot = canonicalRoot;
                return true;
            }

        }

        return false;
    }

    private static bool HasImmutableOwnership(
        string canonicalExecutable,
        string canonicalRoot)
    {

        if (OperatingSystem.IsWindows())
        {

            return false;
        }

        string? current = canonicalExecutable;

        while (!string.IsNullOrEmpty(current)
               && WorkspaceRootPath.IsWithinOrEqual(
                   current,
                   canonicalRoot))
        {

            if (!FileHandleIdentityInterop.TryGetUnixOwnerUserId(
                    current,
                    out uint owner)
                || owner != 0
                || HasGroupOrOtherWrite(current))
            {

                return false;
            }

            if (PathComparer.Equals(current, canonicalRoot))
            {

                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static bool HasGroupOrOtherWrite(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return true;
        }

        try
        {

            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode
                    & (UnixFileMode.GroupWrite
                       | UnixFileMode.OtherWrite))
                != 0;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {

            return true;
        }
    }

    private static string FindDotNetRoot(string executableDirectory)
    {

        DirectoryInfo? current = new(executableDirectory);

        for (int depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {

            if (Directory.Exists(Path.Combine(current.FullName, "sdk"))
                && Directory.Exists(Path.Combine(current.FullName, "shared")))
            {

                return current.FullName;
            }

        }

        return executableDirectory;
    }

    private static bool TryCanonicalizeExistingPath(
        string path,
        bool expectDirectory,
        out string? canonicalPath) =>
        TryCanonicalizeExistingPath(
            path,
            expectDirectory,
            resolutionDepth: 0,
            out canonicalPath);

    private static bool TryCanonicalizeExistingPath(
        string path,
        bool expectDirectory,
        int resolutionDepth,
        out string? canonicalPath)
    {

        canonicalPath = null;

        if (resolutionDepth > 40)
        {

            return false;
        }

        string fullPath;

        try
        {

            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;
        }

        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
        {

            return false;
        }

        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;

        try
        {

            for (int index = 0; index < components.Length; index++)
            {

                bool final = index == components.Length - 1;
                string candidate = Path.Combine(current, components[index]);
                FileSystemInfo entry = final && !expectDirectory
                    ? new FileInfo(candidate)
                    : new DirectoryInfo(candidate);

                if (!entry.Exists)
                {

                    return false;
                }

                FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: true);

                if (target is not null)
                {

                    if (!TryCanonicalizeExistingPath(
                            target.FullName,
                            expectDirectory: !final || expectDirectory,
                            resolutionDepth + 1,
                            out string? resolved)
                        || resolved is null)
                    {

                        return false;
                    }

                    current = resolved;
                }
                else
                {

                    current = Path.GetFullPath(candidate);
                }

                if (!final && !Directory.Exists(current))
                {

                    return false;
                }

            }

            if (expectDirectory ? !Directory.Exists(current) : !File.Exists(current))
            {

                return false;
            }

            canonicalPath = Path.TrimEndingDirectorySeparator(current);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;
        }
    }

    private static bool HasNativeDotNetFileName(string path)
    {

        string fileName = Path.GetFileName(path);

        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExecutableMode(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            return true;
        }

        try
        {

            UnixFileMode execute =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(path) & execute) != 0;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {

            return false;
        }
    }

    private static bool HasPlatformNativeExecutableHeader(string path)
    {

        Span<byte> header = stackalloc byte[4];

        try
        {

            using FileStream stream = File.OpenRead(path);

            if (stream.Read(header) != header.Length)
            {

                return false;
            }

        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {

            return false;
        }

        if (OperatingSystem.IsWindows())
        {

            return header[0] == (byte)'M' && header[1] == (byte)'Z';
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (OperatingSystem.IsLinux())
        {

            return magic == 0x7F454C46;
        }

        if (OperatingSystem.IsMacOS())
        {

            return magic is
                0xFEEDFACE or 0xCEFAEDFE
                or 0xFEEDFACF or 0xCFFAEDFE
                or 0xCAFEBABE or 0xBEBAFECA
                or 0xCAFEBABF or 0xBFBAFECA;
        }

        return false;
    }

    private static IEnumerable<string> GetInstalledDotNetCandidates()
    {

        if (OperatingSystem.IsWindows())
        {

            string programFiles = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ProgramFiles);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {

                yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
            }

            yield break;
        }

        yield return "/usr/local/share/dotnet/dotnet";
        yield return "/usr/share/dotnet/dotnet";
        yield return "/usr/lib/dotnet/dotnet";
        yield return "/usr/lib64/dotnet/dotnet";
        yield return "/opt/dotnet/dotnet";
        yield return "/opt/homebrew/bin/dotnet";
        yield return "/usr/local/bin/dotnet";
    }

    private static IEnumerable<string> GetPlatformTrustedRoots()
    {

        if (OperatingSystem.IsWindows())
        {

            string programFiles = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ProgramFiles);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {

                yield return Path.Combine(programFiles, "dotnet");
            }

            yield break;
        }

        yield return "/usr/local/share/dotnet";
        yield return "/usr/share/dotnet";
        yield return "/usr/lib/dotnet";
        yield return "/usr/lib64/dotnet";
        yield return "/opt/dotnet";
    }

    private static WorkspaceCheckExecutableCapture Failure(
        string code,
        string message) =>
        new(false, code, message, null);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
