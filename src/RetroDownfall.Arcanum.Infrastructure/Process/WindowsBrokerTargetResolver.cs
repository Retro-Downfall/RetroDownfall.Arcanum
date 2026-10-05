using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// An executable the Windows broker can hand to <c>CreateProcessW</c> as <c>lpApplicationName</c>.
/// <paramref name="ReadExecuteRoot"/> is the directory the per-run AppContainer must additionally be
/// granted read+execute on to load it, or <c>null</c> when it is already readable.
/// </summary>
internal sealed record WindowsBrokerTarget(
    string Path,
    string? ReadExecuteRoot);

/// <summary>
/// Resolves a tool child's <c>FileName</c> to the absolute path the Windows broker launches. The broker
/// passes a non-null <c>lpApplicationName</c>, which Win32 uses as-is — no PATH search, no default
/// extension — so a bare <c>git</c> must be resolved on the host first, against the child's scrubbed
/// PATH. Pure over PATH, PATHEXT and a file-exists probe, with Windows path rules implemented here
/// rather than borrowed from the running OS, so every host can pin the Windows behaviour.
/// </summary>
/// <remarks>
/// Deliberately narrower than the Win32 search order: only fully qualified PATH entries are searched,
/// never the current, application or workspace directory, so a <c>git.exe</c> planted in a cloned
/// repository cannot shadow the real one, and a path the caller names is used only when it is
/// drive-qualified or plainly relative to the working directory. Batch scripts are refused rather than wrapped in
/// <c>cmd.exe /c</c>: <c>CreateProcessW</c> cannot start them as an application name, and wrapping
/// would re-parse model-supplied arguments through cmd's metacharacter rules.
/// </remarks>
internal static class WindowsBrokerTargetResolver
{
    internal const string CommandNotFoundCode = "sandbox.command_not_found";

    internal const string BatchTargetRefusedCode = "sandbox.batch_target_refused";

    /// <summary>What Windows searches when <c>PATHEXT</c> is absent from the environment.</summary>
    internal const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    internal static Result<WindowsBrokerTarget> Resolve(
        string fileName,
        string? path,
        string? pathExt,
        Func<string, bool> fileExists,
        string? workingDirectory = null,
        string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        string name = fileName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return NotFound(name);
        }

        string[] extensions = ParseExtensions(pathExt);

        if (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
        {
            // Only a drive-qualified path is used as given, and only a plain relative path resolves
            // against the working directory. Root-relative (`\Windows\…`), UNC and device
            // (`\\server\share\…`, `\\?\…`) and drive-relative (`C:tool`) names all name a file outside
            // the working directory: joining them onto it would launch a different file than the one
            // named, and a UNC target would load code from a network share, so they are refused.
            string? candidate = IsFullyQualified(name)
                ? name
                : workingDirectory is not null && IsFullyQualified(workingDirectory) && IsPlainRelative(name)
                    ? Join(workingDirectory, name)
                    : null;

            if (candidate is null)
            {
                return NotFound(name);
            }

            string? found = FirstExisting(candidate, extensions, fileExists);
            return found is null
                ? NotFound(name)
                : Accept(name, found, readExecuteRoot: null);
        }

        foreach (string directory in SearchDirectories(path))
        {
            string? found = FirstExisting(Join(directory, name), extensions, fileExists);
            if (found is not null)
            {
                return Accept(name, found, ProfileReadExecuteRoot(directory, userProfile));
            }
        }

        return NotFound(name);
    }

    private static Result<WindowsBrokerTarget> Accept(
        string requested,
        string resolved,
        string? readExecuteRoot)
    {
        string extension = Extension(resolved);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return Result<WindowsBrokerTarget>.Failure(new Error(
                BatchTargetRefusedCode,
                BatchRefusalMessage(requested, resolved, readExecuteRoot)));
        }

        return Result<WindowsBrokerTarget>.Success(new WindowsBrokerTarget(resolved, readExecuteRoot));
    }

    /// <summary>
    /// The <c>cmd.exe /c</c> route starts cmd from System32, which every AppContainer can read, but it
    /// grants nothing else: cmd's own search for the script runs inside the sandbox. Only a bare name
    /// the host resolved on a user-profile PATH directory gets that directory granted, so a shim there
    /// (npm, scoop) cannot be reached through cmd at all, and the message says so rather than
    /// suggesting a route that fails.
    /// </summary>
    private static string BatchRefusalMessage(
        string requested,
        string resolved,
        string? profileDirectory)
    {
        string refused =
            $"'{requested}' resolves to the batch script '{resolved}', which the Windows sandbox cannot start directly; the command was not started.";

        return profileDirectory is not null
            ? refused
                + $" Running it as `cmd.exe /c {requested}` will not work either: that route does not grant the sandbox access to the script's directory '{profileDirectory}' inside the user profile, so cmd cannot read the script there."
                + " Run the program the script wraps directly instead."
            : refused
                + $" Run it through the command interpreter explicitly (for example `cmd.exe /c {requested}`) if that is intended;"
                + " that route does not grant the sandbox access to the script's directory, so it only works for a script the sandbox can already read, such as one inside the workspace.";
    }

    private static Result<WindowsBrokerTarget> NotFound(string requested) =>
        Result<WindowsBrokerTarget>.Failure(new Error(
            CommandNotFoundCode,
            $"Command '{requested}' was not found on the PATH available to sandboxed commands; the command was not started."));

    /// <summary>
    /// <paramref name="candidate"/> itself when it already carries an executable extension, otherwise
    /// the candidate with each PATHEXT extension appended, in order. An extensionless file is never
    /// returned: <c>CreateProcessW</c> cannot start the shell scripts npm and others leave beside
    /// their <c>.cmd</c> shims.
    /// </summary>
    private static string? FirstExisting(
        string candidate,
        string[] extensions,
        Func<string, bool> fileExists)
    {
        string extension = Extension(candidate);
        if (extension.Length > 0
            && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return fileExists(candidate) ? candidate : null;
        }

        foreach (string append in extensions)
        {
            string withExtension = candidate + append;
            if (fileExists(withExtension))
            {
                return withExtension;
            }
        }

        return null;
    }

    /// <summary>
    /// The directory to grant read+execute when the executable was found on a PATH entry strictly
    /// inside the user profile (cargo, scoop, pyenv, npm prefixes), which no AppContainer can read by
    /// default. Never the profile itself, and never for a path the caller named directly.
    /// </summary>
    private static string? ProfileReadExecuteRoot(string directory, string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || !IsFullyQualified(userProfile))
        {
            return null;
        }

        string profile = TrimSeparators(Normalize(userProfile));
        string candidate = TrimSeparators(Normalize(directory));

        return candidate.Length > profile.Length + 1
            && candidate.StartsWith(profile + '\\', StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
    }

    private static IEnumerable<string> SearchDirectories(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (string entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim().Trim('"').Trim();

            // A relative entry (".", "bin") would resolve against whatever directory the lookup
            // happened in — the workspace — so it is never searched.
            if (IsFullyQualified(directory))
            {
                yield return directory;
            }
        }
    }

    private static string[] ParseExtensions(string? pathExt)
    {
        string[] extensions =
        [
            .. (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExtensions : pathExt)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static extension => extension.Length > 1 && extension[0] == '.'),
        ];

        return extensions.Length > 0
            ? extensions
            : DefaultPathExtensions.Split(';');
    }

    /// <summary>
    /// A relative path Win32 resolves against the current directory: no drive designator and no
    /// leading separator, so neither root-relative, UNC, device nor drive-relative.
    /// </summary>
    private static bool IsPlainRelative(string value) =>
        value.Length > 0
        && !value.Contains(':')
        && value[0] != '\\'
        && value[0] != '/';

    /// <summary>A drive-qualified Windows path (<c>C:\…</c> or <c>C:/…</c>), whatever OS is running.</summary>
    private static bool IsFullyQualified(string value) =>
        value.Length >= 3
        && char.IsAsciiLetter(value[0])
        && value[1] == ':'
        && (value[2] == '\\' || value[2] == '/');

    private static string Join(string directory, string name) =>
        TrimSeparators(directory) + '\\' + name.TrimStart('\\', '/');

    private static string Normalize(string value) => value.Replace('/', '\\');

    private static string TrimSeparators(string value)
    {
        string trimmed = value.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + '\\' : trimmed;
    }

    private static string Extension(string value)
    {
        int separator = value.LastIndexOfAny(['\\', '/']);
        int dot = value.LastIndexOf('.');
        return dot > separator && dot < value.Length - 1 ? value[dot..] : string.Empty;
    }
}
