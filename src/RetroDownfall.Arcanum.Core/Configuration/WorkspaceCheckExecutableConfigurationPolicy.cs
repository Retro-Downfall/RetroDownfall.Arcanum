using System.Buffers.Binary;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Performs fail-closed, configuration-time checks for a configured workspace-check host.
/// Runtime execution must still verify native executable identity immediately before launch.
/// </summary>
internal sealed class WorkspaceCheckExecutableConfigurationPolicy
{
    private readonly string[] _trustedRoots;

    private WorkspaceCheckExecutableConfigurationPolicy(IEnumerable<string> trustedRoots)
    {
        _trustedRoots = trustedRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Distinct(PathComparer)
            .ToArray();
    }

    public static WorkspaceCheckExecutableConfigurationPolicy ForCurrentPlatform() =>
        new(GetPlatformTrustedRoots());

    internal static WorkspaceCheckExecutableConfigurationPolicy ForTrustedRoots(
        IEnumerable<string> trustedRoots) =>
        new(trustedRoots);

    public WorkspaceCheckExecutableConfigurationResult Validate(
        string configuredPath,
        string? workspace)
    {
        if (!Path.IsPathFullyQualified(configuredPath))
        {
            return Failure(
                "The workspace-check dotnet executable path must be fully qualified; PATH lookup and drive-relative paths are not permitted.");
        }

        if (!TryGetFullPath(configuredPath, out string? lexicalPath) || lexicalPath is null)
        {
            return Failure("The configured workspace-check executable path is invalid.");
        }

        if (!HasNativeDotNetFileName(lexicalPath))
        {
            return Failure("The closed workspace-check executable catalog accepts only a native dotnet host.");
        }

        string? lexicalWorkspace = null;

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            if (!TryGetFullPath(workspace, out lexicalWorkspace) || lexicalWorkspace is null)
            {
                return Failure("The source workspace could not be normalized for executable containment checks.");
            }

            if (IsPathWithinOrEqual(lexicalPath, lexicalWorkspace))
            {
                return Failure(
                    "The workspace-check executable must remain outside the source workspace; the configured path is lexically inside it, including a symlink that points outward.");
            }
        }

        if (!File.Exists(lexicalPath))
        {
            return Failure(
                $"Configured workspace-check executable '{configuredPath}' does not exist or is not a file.");
        }

        if (!TryCanonicalizeExistingPath(lexicalPath, expectDirectory: false, out string? canonicalPath)
            || canonicalPath is null)
        {
            return Failure(
                "Configured workspace-check executable could not be resolved component-by-component to a canonical file.");
        }

        if (!HasNativeDotNetFileName(canonicalPath))
        {
            return Failure(
                "The workspace-check executable must resolve to a native dotnet host, not another executable type.");
        }

        if (lexicalWorkspace is not null)
        {
            if (!TryCanonicalizeExistingPath(
                    lexicalWorkspace,
                    expectDirectory: true,
                    out string? canonicalWorkspace)
                || canonicalWorkspace is null)
            {
                return Failure(
                    "The source workspace could not be resolved component-by-component for executable containment checks.");
            }

            if (IsPathWithinOrEqual(canonicalPath, canonicalWorkspace))
            {
                return Failure(
                    "The workspace-check executable resolves inside the source workspace through one or more symlinked path components.");
            }
        }

        if (!IsUnderTrustedRoot(canonicalPath))
        {
            return Failure(
                "The workspace-check executable must resolve beneath a trusted dotnet installation root.",
                canonicalPath);
        }

        if (!HasPlatformNativeExecutableHeader(canonicalPath))
        {
            return Failure(
                "The configured workspace-check dotnet host does not have the platform native executable format.",
                canonicalPath);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                UnixFileMode mode = File.GetUnixFileMode(canonicalPath);
                UnixFileMode executeBits =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

                if ((mode & executeBits) == 0)
                {
                    return Failure("The configured workspace-check dotnet host is not executable.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failure("The configured workspace-check executable mode could not be validated.");
            }
        }

        return new WorkspaceCheckExecutableConfigurationResult(
            IsValid: true,
            CanonicalPath: canonicalPath,
            RequiresRuntimeIdentityValidation: true,
            Error: null);
    }

    private bool IsUnderTrustedRoot(string canonicalPath)
    {
        foreach (string root in _trustedRoots)
        {
            if (Path.IsPathFullyQualified(root)
                && TryCanonicalizeExistingPath(root, expectDirectory: true, out string? canonicalRoot)
                && canonicalRoot is not null
                && IsPathWithinOrEqual(canonicalPath, canonicalRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCanonicalizeExistingPath(
        string path,
        bool expectDirectory,
        out string? canonicalPath) =>
        TryCanonicalizeExistingPath(path, expectDirectory, resolutionDepth: 0, out canonicalPath);

    private static bool TryCanonicalizeExistingPath(
        string path,
        bool expectDirectory,
        int resolutionDepth,
        out string? canonicalPath)
    {
        canonicalPath = null;

        if (resolutionDepth > 40
            || !TryGetFullPath(path, out string? fullPath)
            || fullPath is null)
        {
            return false;
        }

        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        string relativePath = fullPath[root.Length..];
        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string current = root;

        try
        {
            for (int index = 0; index < components.Length; index++)
            {
                bool isFinal = index == components.Length - 1;
                string candidate = Path.Combine(current, components[index]);
                FileSystemInfo entry = isFinal && !expectDirectory
                    ? new FileInfo(candidate)
                    : new DirectoryInfo(candidate);

                if (!entry.Exists)
                {
                    return false;
                }

                FileSystemInfo? resolved = entry.ResolveLinkTarget(returnFinalTarget: true);

                if (resolved is not null)
                {
                    if (!TryCanonicalizeExistingPath(
                            resolved.FullName,
                            expectDirectory: !isFinal || expectDirectory,
                            resolutionDepth + 1,
                            out string? canonicalTarget)
                        || canonicalTarget is null)
                    {
                        return false;
                    }

                    current = canonicalTarget;
                }
                else
                {
                    current = Path.GetFullPath(candidate);
                }

                if (!isFinal && !Directory.Exists(current))
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

    private static bool TryGetFullPath(string value, out string? fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(value);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = null;
            return false;
        }
    }

    private static bool IsPathWithinOrEqual(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);

        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool HasNativeDotNetFileName(string path)
    {
        string fileName = Path.GetFileName(path);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private static IEnumerable<string> GetPlatformTrustedRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            string? programFiles = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ProgramFiles);
            string? programFilesX86 = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "dotnet");
            }

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "dotnet");
            }

            yield break;
        }

        yield return "/usr/share/dotnet";
        yield return "/usr/local/share/dotnet";
        yield return "/usr/lib/dotnet";
        yield return "/usr/lib64/dotnet";
        yield return "/opt/dotnet";

        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/Cellar/dotnet";
            yield return "/opt/homebrew/Cellar/dotnet-sdk";
            yield return "/usr/local/Cellar/dotnet";
            yield return "/usr/local/Cellar/dotnet-sdk";
        }
    }

    private static WorkspaceCheckExecutableConfigurationResult Failure(
        string error,
        string? canonicalPath = null) =>
        new(
            IsValid: false,
            CanonicalPath: canonicalPath,
            RequiresRuntimeIdentityValidation: false,
            Error: error);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record WorkspaceCheckExecutableConfigurationResult(
    bool IsValid,
    string? CanonicalPath,
    bool RequiresRuntimeIdentityValidation,
    string? Error);
