using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal static class InstallationResetWorkspaceResolver
{

    public static Result<InstallationResetWorkspaceResolution> Resolve(
        string invocationDirectory,
        IReadOnlyList<Campaign> campaigns)
    {

        string current;

        try
        {

            current = Path.GetFullPath(invocationDirectory);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return Failure("The current directory could not be canonicalized.");

        }

        List<(Campaign Campaign, string Root)> matches = [];

        foreach (Campaign campaign in campaigns)
        {

            string root;

            try
            {

                root = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(campaign.Path));

            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {

                return Failure(
                    "A registered Campaign path could not be canonicalized.");

            }

            if (Contains(root, current))
            {

                matches.Add((campaign, root));

            }

        }

        if (matches.Count == 0)
        {

            return Failure(
                "No registered Campaign contains the current directory.");

        }

        int longest = matches.Max(static candidate => candidate.Root.Length);

        (Campaign Campaign, string Root)[] mostSpecific =
            [.. matches.Where(candidate => candidate.Root.Length == longest)];

        if (mostSpecific
            .Select(static item => item.Campaign.Id)
            .Distinct()
            .Count() != 1)
        {

            return Failure("The current directory has an ambiguous Campaign owner.");

        }

        (Campaign Campaign, string Root) selected = mostSpecific[0];

        string[] excludedRoots =
        [
            .. campaigns
                .Where(campaign => campaign.Id != selected.Campaign.Id)
                .Select(static campaign => TryCanonicalize(campaign.Path))
                .Where(static root => root is not null)
                .Select(static root => root!)
                .Where(root => Contains(selected.Root, root))
                .Distinct(PathComparer)
                .Order(PathComparer),
        ];

        return Result<InstallationResetWorkspaceResolution>.Success(
            new InstallationResetWorkspaceResolution(
                new DataRetentionWorkspaceBinding(
                    selected.Campaign.Id,
                    selected.Root),
                excludedRoots));

    }

    private static bool Contains(string root, string candidate)
    {

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(root, candidate, comparison)
            || candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                comparison);

    }

    private static string? TryCanonicalize(string path)
    {

        try
        {

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return null;

        }

    }

    private static Result<InstallationResetWorkspaceResolution> Failure(string message) =>
        Result<InstallationResetWorkspaceResolution>.Failure(new Error(
            ErrorCodes.Data.InventoryUnavailable,
            message));

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

}
