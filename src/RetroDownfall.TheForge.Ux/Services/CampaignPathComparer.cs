using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Path comparison for Open Campaign matching. Loopback uses local OS path semantics;
/// remote uses conservative Ordinal lexical handling only — never local <see cref="Path.GetFullPath"/>.
/// </summary>
public static class CampaignPathComparer
{

    public static bool TryNormalize(string? path, bool loopback, out string normalized, out string? error)
    {

        normalized = string.Empty;

        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {

            error = "Path is required.";

            return false;

        }

        string trimmed = path.Trim();

        if (loopback)
        {

            try
            {

                normalized = TrimTrailingSeparators(Path.GetFullPath(trimmed));

                return true;

            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {

                error = ex.Message;

                return false;

            }

        }

        normalized = TrimTrailingSeparatorsLexical(trimmed);

        if (string.IsNullOrEmpty(normalized))
        {

            error = "Path is required.";

            return false;

        }

        return true;

    }

    public static bool PathsEqual(string left, string right, bool loopback)
    {

        if (loopback)
        {

            return string.Equals(left, right, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

        }

        return string.Equals(left, right, StringComparison.Ordinal);

    }

    /// <summary>
    /// Derives a proposed campaign name from the final folder segment when one exists;
    /// returns <see langword="null"/> for roots (e.g. <c>/</c> or <c>C:\</c>).
    /// </summary>
    public static string? ProposeNameFromPath(string normalizedPath, bool loopback)
    {

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {

            return null;

        }

        if (loopback)
        {

            string name = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.IsNullOrWhiteSpace(name) ? null : name;

        }

        string trimmed = TrimTrailingSeparatorsLexical(normalizedPath.Trim());

        if (trimmed is "/" or "\\" || (trimmed.Length == 2 && trimmed[1] == ':'))
        {

            return null;

        }

        int slash = trimmed.LastIndexOfAny(['/', '\\']);

        if (slash < 0)
        {

            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        }

        if (slash == trimmed.Length - 1)
        {

            return null;

        }

        string segment = trimmed[(slash + 1)..];

        return string.IsNullOrWhiteSpace(segment) ? null : segment;

    }

    public static CampaignDto? FindUnambiguousMatch(
        IReadOnlyList<CampaignDto> campaigns,
        string normalizedPath,
        bool loopback)
    {

        CampaignDto? match = null;

        foreach (CampaignDto campaign in campaigns)
        {

            if (!TryNormalize(campaign.Path, loopback, out string campaignPath, out _))
            {

                if (!loopback
                    && PathsEqual(TrimTrailingSeparatorsLexical(campaign.Path.Trim()), normalizedPath, loopback: false))
                {

                    if (match is not null)
                    {

                        return null;

                    }

                    match = campaign;

                }

                continue;

            }

            if (!PathsEqual(campaignPath, normalizedPath, loopback))
            {

                continue;

            }

            if (match is not null)
            {

                return null;

            }

            match = campaign;

        }

        return match;

    }

    private static string TrimTrailingSeparators(string path)
    {

        if (string.IsNullOrEmpty(path))
        {

            return path;

        }

        string root = Path.GetPathRoot(path) ?? string.Empty;

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0 || string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
        {

            return root.Length > 0 ? root : path;

        }

        return trimmed;

    }

    private static string TrimTrailingSeparatorsLexical(string path)
    {

        if (string.IsNullOrEmpty(path))
        {

            return path;

        }

        // Preserve Unix root "/" and Windows drive roots like "C:\" / "C:/".
        if (path is "/" or "\\")
        {

            return "/";

        }

        if (path.Length == 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] is '/' or '\\'))
        {

            return path;

        }

        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
        {

            return path;

        }

        return path.TrimEnd('/', '\\');

    }

}
