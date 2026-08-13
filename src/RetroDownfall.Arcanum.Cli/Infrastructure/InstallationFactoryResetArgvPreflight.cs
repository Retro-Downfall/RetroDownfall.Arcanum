using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal sealed record InstallationFactoryResetPreflightResult(
    bool IsFactoryReset,
    bool IsValid,
    InstallationResetScope? Scope,
    bool BypassExecution,
    bool DryRun,
    bool Apply,
    bool Force,
    bool Yes,
    string? Error);

internal static class InstallationFactoryResetArgvPreflight
{

    private static readonly string[] HelpOrVersionFlags =
        ["--help", "-h", "-?", "/?", "--version"];

    public static InstallationFactoryResetPreflightResult Parse(string[] args)
    {

        ArgumentNullException.ThrowIfNull(args);

        int dataIndex = FindRootCommandIndex(args);

        if (dataIndex < 0
            || dataIndex + 1 >= args.Length
            || !string.Equals(args[dataIndex + 1], "factory-reset", StringComparison.Ordinal))
        {

            return new InstallationFactoryResetPreflightResult(
                false,
                true,
                null,
                false,
                false,
                false,
                false,
                false,
                null);

        }

        bool bypass = args.Any(argument =>
            HelpOrVersionFlags.Contains(argument, StringComparer.Ordinal));

        int scopes = Count(args, "--workspace")
            + Count(args, "--global")
            + Count(args, "--all");

        InstallationResetScope? scope = scopes == 1
            ? Count(args, "--workspace") == 1
                ? InstallationResetScope.Workspace
                : Count(args, "--global") == 1
                    ? InstallationResetScope.Global
                    : InstallationResetScope.All
            : null;

        int dryRunCount = Count(args, "--dry-run");

        int applyCount = Count(args, "--apply");

        int forceCount = Count(args, "--force");

        int yesCount = Count(args, "--yes");

        bool dryRun = dryRunCount == 1;

        bool apply = applyCount == 1;

        bool force = forceCount == 1;

        bool yes = yesCount == 1;

        string? error = bypass
            ? null
            : Validate(scopes, dryRunCount, applyCount, forceCount, yesCount);

        return new InstallationFactoryResetPreflightResult(
            true,
            error is null,
            scope,
            bypass,
            dryRun,
            apply,
            force,
            yes,
            error);

    }

    internal static string? FindRootCommand(string[] args)
    {

        ArgumentNullException.ThrowIfNull(args);

        int index = FindRootCommandIndex(args);

        return index < 0 ? null : args[index];

    }

    private static string? Validate(
        int scopes,
        int dryRunCount,
        int applyCount,
        int forceCount,
        int yesCount)
    {

        if (scopes != 1)
        {

            return "Select exactly one reset scope: --workspace, --global, or --all.";

        }

        if (dryRunCount + applyCount != 1
            || dryRunCount > 1
            || applyCount > 1)
        {

            return "Select exactly one reset mode: --dry-run or --apply.";

        }

        if (forceCount > 1
            || yesCount > 1)
        {

            return "Reset acknowledgement options may be supplied only once.";

        }

        if (dryRunCount == 1
            && forceCount > 0)
        {

            return "--force is valid only with --apply.";

        }

        if ((forceCount == 1) != (yesCount == 1))
        {

            return "Noninteractive reset acknowledgement requires --yes and --force together.";

        }

        return null;

    }

    private static int Count(string[] args, string option) =>
        args.Count(argument => string.Equals(argument, option, StringComparison.Ordinal));

    private static int FindRootCommandIndex(string[] args)
    {

        for (int index = 0; index < args.Length; index++)
        {

            string argument = args[index];

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {

                return -1;

            }

            if (string.Equals(argument, "--output-format", StringComparison.Ordinal))
            {

                index++;

                continue;

            }

            if (argument.StartsWith("--output-format=", StringComparison.Ordinal)
                || argument is "--json" or "--plain" or "--yes" or "--no-context"
                    or "--print" or "-p" or "--verbose" or "-v"
                    or "--help" or "-h" or "-?" or "/?" or "--version")
            {

                continue;

            }

            return index;

        }

        return -1;

    }

}
