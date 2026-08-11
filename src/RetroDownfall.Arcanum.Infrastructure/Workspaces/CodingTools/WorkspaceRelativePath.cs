using System.Diagnostics.CodeAnalysis;
using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspacePathAliasPlatform
{
    Linux,
    MacOS,
    Windows,
}

/// <summary>
/// Canonical model-visible workspace paths. Values are relative, slash-separated,
/// free of parent traversal, and comparable using the host filesystem's path semantics.
/// </summary>
internal static class WorkspaceRelativePath
{

    private static readonly WorkspacePathAliasPlatform CurrentPlatform =
        OperatingSystem.IsWindows()
            ? WorkspacePathAliasPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? WorkspacePathAliasPlatform.MacOS
                : WorkspacePathAliasPlatform.Linux;

    internal static StringComparison Comparison =>
        CurrentPlatform is WorkspacePathAliasPlatform.Windows or WorkspacePathAliasPlatform.MacOS
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    // Unified-diff parsing is intentionally independent of the host OS because the workspace may
    // live on a volume with different case/normalization semantics. Use the most conservative
    // supported alias model so a patch cannot address the same eventual file through case or
    // trailing-dot/space variants.
    internal static StringComparer Comparer { get; } =
        new CanonicalAliasComparer(WorkspacePathAliasPlatform.Windows);

    internal static IComparer<string> DeterministicComparer { get; } =
        System.Collections.Generic.Comparer<string>.Create(
            static (left, right) =>
            {

                int pathComparison = Comparer.Compare(left, right);

                return pathComparison != 0
                    ? pathComparison
                    : StringComparer.Ordinal.Compare(left, right);

            });

    internal static string GetCanonicalAliasForTests(
        string candidate,
        WorkspacePathAliasPlatform platform)
    {

        if (!TryNormalize(candidate, out string? normalized))
        {

            throw new ArgumentException(
                "The path is not a normalized workspace-relative path.",
                nameof(candidate));

        }

        return GetCanonicalAlias(normalized, platform);

    }

    internal static bool TryNormalize(
        string? candidate,
        [NotNullWhen(true)] out string? normalized)
    {

        normalized = null;

        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Contains('\0', StringComparison.Ordinal))
        {

            return false;

        }

        string slashPath = candidate.Replace('\\', '/');

        if (slashPath.StartsWith("/", StringComparison.Ordinal)
            || IsDriveQualified(slashPath))
        {

            return false;

        }

        List<string> segments = [];

        foreach (string segment in slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {

            if (segment == ".")
            {

                continue;

            }

            if (segment == ".."
                || segment.Contains(':', StringComparison.Ordinal))
            {

                return false;

            }

            segments.Add(segment);

        }

        if (segments.Count == 0)
        {

            return false;

        }

        normalized = string.Join('/', segments);

        return true;

    }

    internal static string[] NormalizeDistinctOrdered(
        IReadOnlyList<string>? paths) =>
        paths?
            .Select(static path =>
                TryNormalize(path, out string? normalized)
                    ? normalized
                    : null)
            .OfType<string>()
            .Distinct(Comparer)
            .OrderBy(
                static path => path,
                DeterministicComparer)
            .ToArray()
        ?? [];

    internal static bool TryResolve(
        string workspaceRoot,
        string candidate,
        [NotNullWhen(true)] out string? absolutePath,
        [NotNullWhen(true)] out string? normalizedRelativePath)
    {

        absolutePath = null;

        normalizedRelativePath = null;

        if (!TryNormalize(candidate, out string? normalized))
        {

            return false;

        }

        try
        {

            string root = Path.GetFullPath(workspaceRoot);

            string platformRelative = normalized.Replace(
                '/',
                Path.DirectorySeparatorChar);

            string resolved = Path.GetFullPath(Path.Combine(root, platformRelative));

            if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, resolved))
            {

                return false;

            }

            absolutePath = resolved;

            normalizedRelativePath = normalized;

            return true;

        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

    }

    internal static string FromAbsolute(string workspaceRoot, string absolutePath)
    {

        string root = Path.GetFullPath(workspaceRoot);

        string candidate = Path.GetFullPath(absolutePath);

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
        {

            throw new ArgumentException(
                "The absolute path is outside the workspace.",
                nameof(absolutePath));

        }

        string relative = Path.GetRelativePath(root, candidate);

        if (!TryNormalize(relative, out string? normalized))
        {

            throw new ArgumentException(
                "The path does not identify a workspace-relative file or directory.",
                nameof(absolutePath));

        }

        return normalized;

    }

    private static bool IsDriveQualified(string path) =>
        path.Length >= 2
        && char.IsAsciiLetter(path[0])
        && path[1] == ':';

    private static string GetCanonicalAlias(
        string normalized,
        WorkspacePathAliasPlatform platform)
    {

        if (platform == WorkspacePathAliasPlatform.Linux)
        {

            return normalized;

        }

        string[] segments = normalized.Split('/');

        for (int index = 0; index < segments.Length; index++)
        {

            string segment = segments[index].Normalize(NormalizationForm.FormC);

            if (platform == WorkspacePathAliasPlatform.Windows)
            {

                segment = segment.TrimEnd(' ', '.');

            }

            segments[index] = segment.ToUpperInvariant();

        }

        return string.Join('/', segments);

    }

    private sealed class CanonicalAliasComparer(WorkspacePathAliasPlatform platform)
        : StringComparer
    {

        public override int Compare(string? x, string? y) =>
            StringComparer.Ordinal.Compare(
                x is null ? null : GetCanonicalAlias(x, platform),
                y is null ? null : GetCanonicalAlias(y, platform));

        public override bool Equals(string? x, string? y) =>
            Compare(x, y) == 0;

        public override int GetHashCode(string obj)
        {

            ArgumentNullException.ThrowIfNull(obj);

            return StringComparer.Ordinal.GetHashCode(GetCanonicalAlias(obj, platform));

        }

    }

}
