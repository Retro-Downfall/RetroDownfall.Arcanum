using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckResolvedProfile(
    string ProfileId,
    string ExecutableId,
    WorkspaceCheckKind Kind,
    WorkspaceCheckDiagnosticParserKind Parser,
    string[] Arguments,
    string? TargetRelativePath = null);

internal sealed record WorkspaceCheckProfileResolution(
    bool Success,
    string? Code,
    string? Message,
    WorkspaceCheckResolvedProfile? Profile)
{

    internal static WorkspaceCheckProfileResolution Accepted(
        WorkspaceCheckResolvedProfile profile) =>
        new(true, null, null, profile);

    internal static WorkspaceCheckProfileResolution Rejected(
        string code,
        string message) =>
        new(false, code, message, null);
}

/// <summary>
/// Immutable runtime projection of the closed workspace-check profile catalog. The caller may
/// select only a profile ID and one value for each operator-defined option; every argument token
/// is rendered by this catalog.
/// </summary>
internal sealed class WorkspaceCheckProfileCatalog
{

    private readonly Dictionary<string, CatalogProfile> _profiles;

    private readonly HashSet<string> _rejectedProfileIds;

    private WorkspaceCheckProfileCatalog(
        Dictionary<string, CatalogProfile> profiles,
        HashSet<string> rejectedProfileIds)
    {

        _profiles = profiles;
        _rejectedProfileIds = rejectedProfileIds;
    }

    internal static WorkspaceCheckProfileCatalog Create(WorkspaceCheckSettings settings)
    {

        ArgumentNullException.ThrowIfNull(settings);

        Dictionary<string, CatalogProfile> profiles =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [WorkspaceCheckCatalogDefaults.DotNetBuildProfileId] =
                    BuiltIn(
                        WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                        WorkspaceCheckKind.Build,
                        WorkspaceCheckDiagnosticParserKind.MsBuild,
                        ["build", "--no-restore"],
                        BuildAndTestOptions()),
                [WorkspaceCheckCatalogDefaults.DotNetTestProfileId] =
                    BuiltIn(
                        WorkspaceCheckCatalogDefaults.DotNetTestProfileId,
                        WorkspaceCheckKind.Test,
                        WorkspaceCheckDiagnosticParserKind.VsTest,
                        ["test", "--no-restore"],
                        BuildAndTestOptions()),
                [WorkspaceCheckCatalogDefaults.DotNetLintProfileId] =
                    BuiltIn(
                        WorkspaceCheckCatalogDefaults.DotNetLintProfileId,
                        WorkspaceCheckKind.Lint,
                        WorkspaceCheckDiagnosticParserKind.DotNetFormat,
                        ["format", "--verify-no-changes", "--no-restore"],
                        new Dictionary<string, Dictionary<string, string[]>>(
                            StringComparer.OrdinalIgnoreCase)),
            };
        HashSet<string> rejectedProfileIds =
            new(StringComparer.OrdinalIgnoreCase);

        foreach ((string profileId, WorkspaceCheckProfileSettings profile) in
                 settings.CustomProfiles
                     ?? new Dictionary<string, WorkspaceCheckProfileSettings>())
        {

            if (string.IsNullOrWhiteSpace(profileId)
                || profile is null
                || WorkspaceCheckCatalogDefaults.ReservedProfileIds.Contains(profileId)
                || !string.Equals(
                    profile.ExecutableId,
                    WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                    StringComparison.OrdinalIgnoreCase))
            {

                continue;
            }

            string[] fixedArguments = profile.FixedArguments?.ToArray() ?? [];

            if (fixedArguments.Length == 0)
            {

                continue;
            }

            if (fixedArguments.Any(
                    WorkspaceCheckArgumentPolicy
                        .IsRuntimeReservedToken))
            {

                rejectedProfileIds.Add(profileId);
                continue;
            }

            List<string> enforcedArguments = [.. fixedArguments];

            if (!enforcedArguments.Contains("--no-restore", StringComparer.Ordinal))
            {

                enforcedArguments.Add("--no-restore");
            }

            Dictionary<string, Dictionary<string, string[]>> options =
                new(StringComparer.OrdinalIgnoreCase);
            bool rejectedOptionRendering = false;
            string? targetRelativePath = null;

            if (!string.IsNullOrWhiteSpace(profile.Target))
            {
                if (!WorkspaceRelativePath.TryNormalize(
                        profile.Target,
                        out targetRelativePath)
                    || !WorkspaceCheckTargetResolver
                        .HasSupportedTargetExtension(targetRelativePath))
                {
                    rejectedProfileIds.Add(profileId);
                    continue;
                }
            }

            foreach ((string optionId, WorkspaceCheckProfileOptionSettings option) in
                     profile.Options
                         ?? new Dictionary<string, WorkspaceCheckProfileOptionSettings>())
            {

                if (string.IsNullOrWhiteSpace(optionId) || option is null)
                {

                    continue;
                }

                Dictionary<string, string[]> values =
                    new(StringComparer.OrdinalIgnoreCase);

                foreach ((string valueId, string[] rendering) in
                         option.AllowedValues
                             ?? new Dictionary<string, string[]>())
                {

                    if (!string.IsNullOrWhiteSpace(valueId)
                        && rendering is { Length: > 0 })
                    {

                        if (rendering.Any(
                                WorkspaceCheckArgumentPolicy
                                    .IsRuntimeReservedToken))
                        {

                            rejectedOptionRendering = true;
                            continue;
                        }

                        values[valueId] = rendering.ToArray();
                    }

                }

                if (values.Count > 0)
                {

                    options[optionId] = values;
                }

            }

            if (rejectedOptionRendering)
            {

                rejectedProfileIds.Add(profileId);
                continue;
            }

            profiles[profileId] = new CatalogProfile(
                profileId,
                WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                profile.Kind,
                profile.Parser,
                enforcedArguments.ToArray(),
                options,
                targetRelativePath);
        }

        return new WorkspaceCheckProfileCatalog(
            profiles,
            rejectedProfileIds);
    }

    internal WorkspaceCheckProfileResolution Resolve(
        string profileId,
        IReadOnlyDictionary<string, string>? options)
    {

        if (!string.IsNullOrWhiteSpace(profileId)
            && _rejectedProfileIds.Contains(profileId))
        {

            return WorkspaceCheckProfileResolution.Rejected(
                "invalid_profile",
                "The operator profile attempts to override workspace-check restore or output-root policy.");
        }

        if (string.IsNullOrWhiteSpace(profileId)
            || !_profiles.TryGetValue(profileId, out CatalogProfile? profile))
        {

            return WorkspaceCheckProfileResolution.Rejected(
                "unknown_profile",
                "The requested workspace-check profile is not in the operator-owned catalog.");
        }

        List<string> rendered = [.. profile.FixedArguments];

        if (options is not null)
        {

            foreach ((string optionId, string valueId) in options
                         .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {

                if (!profile.Options.TryGetValue(optionId, out Dictionary<string, string[]>? values))
                {

                    return WorkspaceCheckProfileResolution.Rejected(
                        "invalid_option",
                        $"Option '{optionId}' is not available for profile '{profile.ProfileId}'.");
                }

                if (string.IsNullOrWhiteSpace(valueId)
                    || !values.TryGetValue(valueId, out string[]? tokens))
                {

                    return WorkspaceCheckProfileResolution.Rejected(
                        "invalid_option_value",
                        $"Value '{valueId}' is not allowlisted for option '{optionId}'.");
                }

                rendered.AddRange(tokens);
            }

        }

        return WorkspaceCheckProfileResolution.Accepted(
            new WorkspaceCheckResolvedProfile(
                profile.ProfileId,
                profile.ExecutableId,
                profile.Kind,
                profile.Parser,
                rendered.ToArray(),
                profile.TargetRelativePath));
    }

    internal IReadOnlyCollection<string> ProfileIds => _profiles.Keys;

    internal IEnumerable<(
        string ProfileId,
        string OptionId,
        IReadOnlyCollection<string> ValueIds)> EnumerateOptionSchemas()
    {

        foreach (CatalogProfile profile in _profiles.Values
                     .OrderBy(
                         static profile => profile.ProfileId,
                         StringComparer.OrdinalIgnoreCase))
        {

            foreach ((string optionId, Dictionary<string, string[]> values)
                     in profile.Options.OrderBy(
                         static pair => pair.Key,
                         StringComparer.OrdinalIgnoreCase))
            {

                yield return (
                    profile.ProfileId,
                    optionId,
                    values.Keys
                        .OrderBy(
                            static value => value,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }
        }
    }

    private static CatalogProfile BuiltIn(
        string id,
        WorkspaceCheckKind kind,
        WorkspaceCheckDiagnosticParserKind parser,
        string[] fixedArguments,
        Dictionary<string, Dictionary<string, string[]>> options) =>
        new(
            id,
            WorkspaceCheckCatalogDefaults.DotNetExecutableId,
            kind,
            parser,
            fixedArguments,
            options,
            TargetRelativePath: null);

    private static Dictionary<string, Dictionary<string, string[]>>
        BuildAndTestOptions() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["configuration"] = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["debug"] = ["--configuration", "Debug"],
                ["release"] = ["--configuration", "Release"],
            },
            ["verbosity"] = new Dictionary<string, string[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["quiet"] = ["--verbosity", "quiet"],
                ["minimal"] = ["--verbosity", "minimal"],
                ["normal"] = ["--verbosity", "normal"],
                ["detailed"] = ["--verbosity", "detailed"],
                ["diagnostic"] = ["--verbosity", "diagnostic"],
            },
        };

    private sealed record CatalogProfile(
        string ProfileId,
        string ExecutableId,
        WorkspaceCheckKind Kind,
        WorkspaceCheckDiagnosticParserKind Parser,
        string[] FixedArguments,
        Dictionary<string, Dictionary<string, string[]>> Options,
        string? TargetRelativePath);
}
