using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>
/// Locates Compendium via an installed binary, sibling build output, or <c>dotnet run</c> on the
/// solution project, then starts it with <see cref="ProcessStartInfo.ArgumentList"/> (no shell).
/// </summary>
public sealed class CompendiumLauncher : ICompendiumLauncher
{

    public const string AssemblyName = "RetroDownfall.Compendium.Ux";

    public const string ProjectRelativePath = "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj";

    private readonly Func<string>? _baseDirectoryOverride;

    private readonly Func<string, bool>? _fileExistsOverride;

    private readonly Func<ProcessStartInfo, bool>? _startOverride;

    public CompendiumLauncher()
    {
    }

    /// <summary>Test seam for discovery and process start.</summary>
    public CompendiumLauncher(
        Func<string> baseDirectory,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, bool> startProcess)
    {

        _baseDirectoryOverride = baseDirectory;

        _fileExistsOverride = fileExists;

        _startOverride = startProcess;

    }

    public string ConfigPath => Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    public CompendiumLaunchResult TryLaunch()
    {

        string configPath = ConfigPath;

        if (TryFindExecutable(out string? executable) && executable is not null)
        {

            if (TryStart(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {

                return new CompendiumLaunchResult(
                    true,
                    executable,
                    configPath,
                    "Opened Compendium.");

            }

            return new CompendiumLaunchResult(
                false,
                executable,
                configPath,
                $"Found Compendium at {executable} but failed to start it. Edit {configPath} directly or run Compendium from your install.");

        }

        if (TryFindProject(out string? projectPath) && projectPath is not null)
        {

            ProcessStartInfo startInfo = new()
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("run");

            startInfo.ArgumentList.Add("--project");

            startInfo.ArgumentList.Add(projectPath);

            if (TryStart(startInfo))
            {

                return new CompendiumLaunchResult(
                    true,
                    projectPath,
                    configPath,
                    "Started Compendium via dotnet run (development).");

            }

            return new CompendiumLaunchResult(
                false,
                projectPath,
                configPath,
                $"Found Compendium project at {projectPath} but failed to start it. Run: dotnet run --project {projectPath}");

        }

        return new CompendiumLaunchResult(
            false,
            null,
            configPath,
            $"Compendium was not found. Install Compendium or edit {configPath} (Arcanum configuration).");

    }

    private bool TryFindExecutable(out string? path)
    {

        path = null;

        foreach (string candidate in EnumerateExecutableCandidates())
        {

            if (FileExists(candidate))
            {

                path = candidate;

                return true;

            }

        }

        return false;

    }

    private bool TryFindProject(out string? path)
    {

        path = null;

        string? directory = GetBaseDirectory();

        for (int i = 0; i < 8 && !string.IsNullOrEmpty(directory); i++)
        {

            string candidate = Path.Combine(directory, ProjectRelativePath);

            if (FileExists(candidate))
            {

                path = candidate;

                return true;

            }

            directory = Directory.GetParent(directory)?.FullName;

        }

        return false;

    }

    private IEnumerable<string> EnumerateExecutableCandidates()
    {

        string baseDir = GetBaseDirectory();

        string fileName = OperatingSystem.IsWindows() ? $"{AssemblyName}.exe" : AssemblyName;

        yield return Path.Combine(baseDir, fileName);

        yield return Path.Combine(baseDir, "..", "RetroDownfall.Compendium.Ux", fileName);

        if (OperatingSystem.IsMacOS())
        {

            string home = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, "Applications", "Compendium.app", "Contents", "MacOS", AssemblyName);

            yield return Path.Combine("/Applications", "Compendium.app", "Contents", "MacOS", AssemblyName);

        }

        if (OperatingSystem.IsLinux())
        {

            string home = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, ".local", "bin", AssemblyName);

            yield return Path.Combine("/usr", "local", "bin", AssemblyName);

        }

        if (OperatingSystem.IsWindows())
        {

            string local = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(local, "Compendium", fileName);

        }

    }

    private string GetBaseDirectory() =>
        _baseDirectoryOverride?.Invoke() ?? AppContext.BaseDirectory;

    private bool FileExists(string path) =>
        _fileExistsOverride?.Invoke(path) ?? File.Exists(path);

    private bool TryStart(ProcessStartInfo startInfo)
    {

        if (_startOverride is not null)
        {

            return _startOverride(startInfo);

        }

        try
        {

            using Process? process = Process.Start(startInfo);

            return process is not null;

        }
        catch
        {

            return false;

        }

    }

}
