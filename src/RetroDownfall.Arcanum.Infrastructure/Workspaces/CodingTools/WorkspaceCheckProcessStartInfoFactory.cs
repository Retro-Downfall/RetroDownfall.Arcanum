using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckRunDirectories(
    string Root,
    string Artifacts,
    string Output,
    string Intermediate,
    string TestResults,
    string Home,
    string CliHome,
    string HttpCache,
    string Temp,
    IReadOnlyList<string>? SharedIpcRoots = null,
    WorkspaceCheckTrxSource? TestResultsSource = null)
{

    internal static WorkspaceCheckRunDirectories Create() =>
        Create(Path.GetTempPath());

    internal static WorkspaceCheckRunDirectories Create(
        string temporaryDirectory)
    {

        string root = Path.Combine(
            temporaryDirectory,
            $"arcanum-workspace-check-{Guid.NewGuid():N}");
        CreateOwnerOnlyDirectory(root);

        try
        {

            WorkspaceCheckRunDirectories directories = CreateUnder(root);

            if (!OperatingSystem.IsMacOS())
            {

                return directories;
            }

            return directories with
            {
                SharedIpcRoots = MacOsDotNetIpcRoots.Ensure(),
            };
        }
        catch
        {

            // The run never started, so the caller has no handle to clean up: a sub-root, TRX-source,
            // or shared-IPC failure must not strand this owner-only tree in the temp directory.
            TryDeletePartialRoot(root);

            throw;
        }
    }

    private static void TryDeletePartialRoot(
        string root)
    {

        try
        {

            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {
            // Best-effort: the original allocation failure is what the caller must see.
        }
    }

    internal static WorkspaceCheckRunDirectories CreateUnder(string root)
    {

        string canonicalRoot = Path.GetFullPath(root);
        CreateOwnerOnlyDirectory(canonicalRoot);

        WorkspaceCheckRunDirectories directories = new(
            canonicalRoot,
            Path.Combine(canonicalRoot, "artifacts"),
            Path.Combine(canonicalRoot, "artifacts", "bin"),
            Path.Combine(canonicalRoot, "artifacts", "obj"),
            Path.Combine(canonicalRoot, "test-results"),
            Path.Combine(canonicalRoot, "home"),
            Path.Combine(canonicalRoot, "dotnet-cli-home"),
            Path.Combine(canonicalRoot, "nuget-http-cache"),
            Path.Combine(canonicalRoot, "tmp"));

        foreach (string directory in directories.WritableRoots)
        {

            CreateOwnerOnlyDirectory(directory);
        }

        return directories with
        {
            TestResultsSource = WorkspaceCheckTrxSource.Capture(
                canonicalRoot,
                directories.TestResults),
        };
    }

    private static void CreateOwnerOnlyDirectory(
        string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }

        SecureFilePermissions.ApplyOwnerOnlyDirectory(path);
    }

    /// <summary>
    /// Per-run roots the server creates and deletes. These are owner-only and live entirely beneath
    /// <see cref="Root"/>; nothing outside the run is ever included.
    /// </summary>
    internal IReadOnlyList<string> WritableRoots =>
    [
        Artifacts,
        Output,
        Intermediate,
        TestResults,
        Home,
        CliHome,
        HttpCache,
        Temp,
    ];

    /// <summary>
    /// Every root the child is allowed to write: the per-run roots plus any host-shared .NET IPC
    /// roots (<see cref="SharedIpcRoots"/>). Only this list is handed to the filesystem jail.
    /// </summary>
    internal IReadOnlyList<string> SandboxWritableRoots
    {
        get
        {

            if (SharedIpcRoots is not { Count: > 0 })
            {

                return WritableRoots;
            }

            List<string> roots = [.. WritableRoots];

            roots.AddRange(SharedIpcRoots);

            return roots;
        }
    }

    internal void PinSelectedSdk(
        string version,
        string dotNetRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotNetRoot);

        string path = Path.Combine(Root, "global.json");

        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using (System.Text.Json.Utf8JsonWriter writer = new(
                   stream,
                   new System.Text.Json.JsonWriterOptions
                   {
                       Indented = true,
                   }))
        {

            writer.WriteStartObject();
            writer.WriteStartObject("sdk");
            writer.WriteString("version", version);
            writer.WriteString("rollForward", "disable");
            writer.WriteBoolean(
                "allowPrerelease",
                version.Contains('-', StringComparison.Ordinal));
            writer.WriteStartArray("paths");
            writer.WriteStringValue(Path.GetFullPath(dotNetRoot));
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        SecureFilePermissions.ApplyOwnerOnlyFile(path);
    }
}

internal static class WorkspaceCheckProcessStartInfoFactory
{

    internal static ProcessStartInfo Create(
        string executablePath,
        string sdkEntryPointPath,
        string workspaceRoot,
        WorkspaceCheckResolvedProfile profile,
        WorkspaceCheckRunDirectories directories,
        WorkspaceCheckEnvironmentPaths environment,
        string? workspaceTarget = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkEntryPointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(environment);

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = directories.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(sdkEntryPointPath);

        for (int index = 0; index < profile.Arguments.Length; index++)
        {

            startInfo.ArgumentList.Add(profile.Arguments[index]);

            if (index == 0
                && !string.IsNullOrWhiteSpace(workspaceTarget))
            {

                startInfo.ArgumentList.Add(workspaceTarget);
            }
        }

        // .NET 10's --artifacts-path sets BaseOutputPath, BaseIntermediateOutputPath, and
        // MSBuildProjectExtensionsPath to project-isolated subtrees beneath this trusted root.
        if (profile.Kind is WorkspaceCheckKind.Build or WorkspaceCheckKind.Test)
        {

            startInfo.ArgumentList.Add("--artifacts-path");
            startInfo.ArgumentList.Add(directories.Artifacts);
        }

        if (profile.Kind == WorkspaceCheckKind.Test)
        {

            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(directories.TestResults);
            startInfo.ArgumentList.Add("--logger");
            startInfo.ArgumentList.Add("trx");
        }

        WorkspaceCheckEnvironmentBuilder.Apply(startInfo, environment);
        startInfo.Environment["ArtifactsPath"] =
            directories.Artifacts;
        startInfo.Environment["UseArtifactsOutput"] = "true";
        startInfo.Environment["VSTEST_RESULTS_DIRECTORY"] =
            directories.TestResults;

        if (profile.Kind == WorkspaceCheckKind.Lint)
        {

            startInfo.ArgumentList.Add("--binarylog");
            startInfo.ArgumentList.Add(
                Path.Combine(
                    directories.Artifacts,
                    "format.binlog"));
            startInfo.ArgumentList.Add("--report");
            startInfo.ArgumentList.Add(directories.TestResults);
        }

        return startInfo;
    }
}

internal sealed record WorkspaceCheckTargetResolution(
    bool Success,
    string? Code,
    string? Message,
    string? TargetPath);

internal static class WorkspaceCheckTargetResolver
{

    internal static WorkspaceCheckTargetResolution Resolve(
        string workspaceRoot)
    {

        string root = Path.GetFullPath(workspaceRoot);
        string[] solutions = Directory
            .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(static path =>
                string.Equals(
                    Path.GetExtension(path),
                    ".sln",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetExtension(path),
                    ".slnx",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        if (solutions.Length == 1)
        {

            return Accepted(solutions[0]);
        }

        if (solutions.Length > 1)
        {

            return Rejected(
                "ambiguous_workspace_target",
                "workspace_check found multiple root solution files; configure one closed operator profile for a specific target.");
        }

        string[] projects = Directory
            .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(static path =>
            {
                string extension = Path.GetExtension(path);

                return string.Equals(
                        extension,
                        ".csproj",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        extension,
                        ".fsproj",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        extension,
                        ".vbproj",
                        StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        return projects.Length switch
        {
            1 => Accepted(projects[0]),
            0 => Rejected(
                "workspace_target_missing",
                "workspace_check found no root solution or project file."),
            _ => Rejected(
                "ambiguous_workspace_target",
                "workspace_check found multiple root project files; configure one closed operator profile for a specific target."),
        };
    }

    internal static WorkspaceCheckTargetResolution Resolve(
        string workspaceRoot,
        string configuredTarget)
    {
        if (!WorkspaceRelativePath.TryResolve(
                workspaceRoot,
                configuredTarget,
                out string? lexicalTarget,
                out string? normalizedTarget)
            || !HasSupportedTargetExtension(normalizedTarget))
        {
            return Rejected(
                "invalid_workspace_target",
                "The configured workspace-check target must be a supported workspace-relative solution or project path.");
        }

        try
        {
            string? canonicalRoot = CanonicalizeExistingPath(
                workspaceRoot,
                expectDirectory: true);
            string? canonicalTarget = CanonicalizeExistingPath(
                lexicalTarget,
                expectDirectory: false);

            if (canonicalRoot is null
                || canonicalTarget is null
                || !File.Exists(canonicalTarget)
                || !WorkspaceRootPath.IsWithinOrEqual(
                    canonicalTarget,
                    canonicalRoot))
            {
                return Rejected(
                    "invalid_workspace_target",
                    "The configured workspace-check target is missing or escapes the source workspace.");
            }

            return Accepted(canonicalTarget);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Rejected(
                "invalid_workspace_target",
                "The configured workspace-check target could not be resolved safely.");
        }
    }

    internal static bool HasSupportedTargetExtension(string path)
    {
        string extension = Path.GetExtension(path);

        return string.Equals(
                extension,
                ".sln",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                extension,
                ".slnx",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                extension,
                ".csproj",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                extension,
                ".fsproj",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                extension,
                ".vbproj",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalizeExistingPath(
        string path,
        bool expectDirectory)
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
            string candidate = Path.Combine(
                current,
                components[index]);
            FileSystemInfo entry = final && !expectDirectory
                ? new FileInfo(candidate)
                : new DirectoryInfo(candidate);

            if (!entry.Exists)
            {
                return null;
            }

            FileSystemInfo? resolved = entry.ResolveLinkTarget(
                returnFinalTarget: true);
            current = Path.GetFullPath(
                resolved?.FullName ?? candidate);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static WorkspaceCheckTargetResolution Accepted(
        string path) =>
        new(true, null, null, Path.GetFullPath(path));

    private static WorkspaceCheckTargetResolution Rejected(
        string code,
        string message) =>
        new(false, code, message, null);
}
