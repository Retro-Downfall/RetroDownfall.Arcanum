namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// Answers "is this Familiar installed?" without spawning anything.
/// </summary>
/// <remarks>
/// A Familiar is a user-installed sibling binary, found the same way the operator's own shell finds
/// it, so resolution walks <c>PATH</c> (plus <c>PATHEXT</c> on Windows) rather than consulting a
/// hard-coded install location. Cheap enough for the background health probe to call on its
/// interval, and precise enough for the status probe to tell "not installed" apart from
/// "installed but not signed in" before it pays for a process.
/// </remarks>
public static class FamiliarExecutableResolver
{

    /// <summary>What Windows searches when <c>PATHEXT</c> is absent from the environment.</summary>
    private const string DefaultWindowsPathExtensions = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// Resolves <paramref name="command"/> to an existing file. An operator override containing a
    /// directory separator is taken literally; a bare name is searched on PATH.
    /// </summary>
    public static bool TryResolve(string? command, out string? resolvedPath)
    {

        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        string candidate = command.Trim();

        if (candidate.Contains(Path.DirectorySeparatorChar)
            || candidate.Contains(Path.AltDirectorySeparatorChar))
        {

            string full = Path.GetFullPath(candidate);

            if (File.Exists(full))
            {
                resolvedPath = full;

                return true;
            }

            return false;

        }

        bool windows = OperatingSystem.IsWindows();

        string? pathExt = windows
            ? System.Environment.GetEnvironmentVariable("PATHEXT")
            : null;

        foreach (string directory in SearchDirectories())
        {

            foreach (string name in CandidateNames(candidate, windows, pathExt))
            {

                string full;

                try
                {

                    full = Path.Combine(directory, name);

                }
                catch (ArgumentException)
                {

                    // A malformed PATH entry is the operator's, not Arcanum's, to fix.
                    continue;

                }

                if (File.Exists(full))
                {
                    resolvedPath = full;

                    return true;
                }

            }

        }

        return false;

    }

    private static IEnumerable<string> SearchDirectories()
    {

        string? path = System.Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {

            string trimmed = entry.Trim();

            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }

        }

    }

    /// <summary>
    /// The file names to try inside one directory, in preference order. The platform and
    /// <c>PATHEXT</c> are arguments rather than ambient reads so the Windows order is pinned by a
    /// test on any host.
    /// </summary>
    internal static IEnumerable<string> CandidateNames(string command, bool windows, string? pathExt)
    {

        if (!windows)
        {
            yield return command;

            yield break;
        }

        foreach (string extension in (pathExt ?? DefaultWindowsPathExtensions)
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {

            yield return command + extension.Trim();

        }

        // Last, not first. npm's cmd-shim writes an extensionless sh script beside `claude.cmd` in
        // the same prefix directory, and CreateProcess cannot start it with UseShellExecute = false.
        // Preferring it would resolve as installed and then fail every turn — precisely the "ready
        // for something that can never run" this resolver exists to prevent. It stays a candidate
        // only for the operator who really did drop a bare executable on PATH.
        yield return command;

    }

}
