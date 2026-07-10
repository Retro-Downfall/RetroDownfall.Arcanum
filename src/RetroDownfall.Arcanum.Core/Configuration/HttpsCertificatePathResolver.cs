namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves HTTPS certificate/key paths from configuration. Expands a leading tilde
/// (<c>~</c>, <c>~/</c>, or <c>~\</c>) to the current user's profile directory, then resolves the
/// result to a full path. A <c>~foo</c> prefix (tilde immediately followed by other characters) is
/// left untouched — only a bare tilde or a tilde followed by a directory separator is a home
/// reference, matching common shell semantics without attempting per-user home lookups.
/// </summary>
public static class HttpsCertificatePathResolver
{

    public static string? Resolve(string? path)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return path;

        }

        string trimmed = path.Trim();

        string expanded = ExpandTilde(trimmed);

        return Path.GetFullPath(expanded);

    }

    private static string ExpandTilde(string path)
    {

        if (path == "~")
        {

            return HomeDirectory;

        }

        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {

            // Normalize separators so "~/a" and "~\\a" resolve identically on every OS.
            string relative = path[2..].Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            return Path.Combine(HomeDirectory, relative);

        }

        return path;

    }

    private static string HomeDirectory =>
        global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile);

}
