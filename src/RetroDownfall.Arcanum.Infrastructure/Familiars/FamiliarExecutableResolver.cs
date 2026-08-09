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

        foreach (string directory in SearchDirectories())
        {

            foreach (string name in CandidateNames(candidate))
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

    private static IEnumerable<string> CandidateNames(string command)
    {

        if (!OperatingSystem.IsWindows())
        {
            yield return command;

            yield break;
        }

        yield return command;

        string? pathExt = System.Environment.GetEnvironmentVariable("PATHEXT");

        foreach (string extension in (pathExt ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {

            yield return command + extension.Trim();

        }

    }

}
