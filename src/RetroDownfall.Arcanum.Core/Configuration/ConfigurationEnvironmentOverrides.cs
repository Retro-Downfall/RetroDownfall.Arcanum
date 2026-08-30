using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ConfigurationEnvironmentOverride(
    string Path,
    string VariableName,
    bool IsPresent,
    bool IsEffective,
    string? Error);

public sealed record ConfigurationEnvironmentSnapshot(
    ArcanumSettings EffectiveSettings,
    ImmutableArray<ConfigurationEnvironmentOverride> Overrides)
{

    public ConfigurationEnvironmentOverride? Find(string path) =>
        Overrides.LastOrDefault(candidate =>
            string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));

    public ConfigurationEnvironmentOverride? FindEffective(string path) =>
        Overrides.LastOrDefault(candidate =>
            candidate.IsEffective
            && string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));

}

/// <summary>
/// Applies supported environment overrides to a cloned effective snapshot and records only the
/// variable name and path. Raw values are intentionally excluded from provenance.
/// </summary>
public static class ConfigurationEnvironmentResolver
{

    private const string GeneralPrefix = "ARCANUM_Arcanum__";

    public static ConfigurationEnvironmentSnapshot Resolve(ArcanumSettings persisted) =>
        Resolve(persisted, ReadProcessEnvironment());

    public static ConfigurationEnvironmentSnapshot Resolve(
        ArcanumSettings persisted,
        IReadOnlyDictionary<string, string?> environment)
    {

        ArgumentNullException.ThrowIfNull(persisted);

        ArgumentNullException.ThrowIfNull(environment);

        ArcanumSettings effective = ConfigurationPathAccessor.Clone(persisted);

        List<ConfigurationEnvironmentOverride> overrides = [];

        IEnumerable<KeyValuePair<string, string?>> general = environment
            .Where(static entry =>
                entry.Key.StartsWith(GeneralPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase);

        foreach ((string variableName, string? rawValue) in general)
        {

            string path = NormalizePath(
                variableName[GeneralPrefix.Length..].Replace(
                    "__",
                    ".",
                    StringComparison.Ordinal));

            effective = Apply(
                effective,
                path,
                variableName,
                rawValue,
                overrides);

        }

        effective = ApplyEditionSpecial(
            effective,
            environment,
            overrides);

        effective = ApplySpecial(
            effective,
            "host.listenAny",
            "ARCANUM_HOST_ANY",
            environment,
            overrides);

        return new ConfigurationEnvironmentSnapshot(effective, [.. overrides]);

    }

    private static ArcanumSettings ApplySpecial(
        ArcanumSettings settings,
        string path,
        string variableName,
        IReadOnlyDictionary<string, string?> environment,
        List<ConfigurationEnvironmentOverride> overrides)
    {

        KeyValuePair<string, string?> entry = environment.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, variableName, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrEmpty(entry.Key)
            ? settings
            : Apply(settings, path, entry.Key, entry.Value, overrides);

    }

    private static ArcanumSettings ApplyEditionSpecial(
        ArcanumSettings settings,
        IReadOnlyDictionary<string, string?> environment,
        List<ConfigurationEnvironmentOverride> overrides)
    {

        const string variableName = "ARCANUM_EDITION";

        KeyValuePair<string, string?> entry = environment.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, variableName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(entry.Key))
        {

            return settings;

        }

        string? value = entry.Value;

        if (string.Equals(value?.Trim(), "dev", StringComparison.OrdinalIgnoreCase))
        {

            value = nameof(ArcanumEdition.Development);

        }

        return Apply(settings, "edition", entry.Key, value, overrides);

    }

    private static ArcanumSettings Apply(
        ArcanumSettings settings,
        string path,
        string variableName,
        string? rawValue,
        List<ConfigurationEnvironmentOverride> overrides)
    {

        string? removedPath = FindRemovedWardApprovalPath(path);

        if (removedPath is not null)
        {

            throw new InvalidOperationException(
                $"Environment override '{variableName}' targets removed configuration path "
                    + $"'{removedPath}'. {ConfigurationValidator.ObsoleteWardApprovalConfigurationMessage}");

        }

        if (rawValue is null)
        {

            overrides.Add(new ConfigurationEnvironmentOverride(
                path,
                variableName,
                IsPresent: true,
                IsEffective: false,
                Error: "The environment variable has no value."));

            return settings;

        }

        ConfigurationPathUpdate update = ConfigurationPathAccessor.Set(
            settings,
            path,
            rawValue);

        overrides.Add(new ConfigurationEnvironmentOverride(
            path,
            variableName,
            IsPresent: true,
            IsEffective: update.IsSuccess,
            Error: update.Error));

        return update.IsSuccess ? update.Settings! : settings;

    }

    private static string? FindRemovedWardApprovalPath(string path)
    {

        if (string.Equals(path, "security.ward.enabled", StringComparison.OrdinalIgnoreCase))
        {

            return "security.ward.enabled";

        }

        if (string.Equals(
                path,
                "security.ward.autoDenyInUnattendedMode",
                StringComparison.OrdinalIgnoreCase))
        {

            return "security.ward.autoDenyInUnattendedMode";

        }

        return path.Equals("security.ward.autoApprove", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("security.ward.autoApprove.", StringComparison.OrdinalIgnoreCase)
                ? "security.ward.autoApprove"
                : null;

    }

    private static IReadOnlyDictionary<string, string?> ReadProcessEnvironment()
    {

        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in
                 System.Environment.GetEnvironmentVariables())
        {

            if (entry.Key is string name)
            {

                environment[name] = entry.Value as string;

            }

        }

        return environment;

    }

    private static string NormalizePath(string path) =>
        string.Join(
            '.',
            path.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(static segment =>
                segment.Length == 0
                    ? segment
                    : char.ToLowerInvariant(segment[0]) + segment[1..]));

}
