using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{

    public const int MaxConfigurationBytes = 10 * 1024 * 1024;

    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {

        string configPath = ArcanumPaths.GrimoireDirectory;

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        ValidateArcanumConfigurationFile(jsonPath);

        builder.AddJsonFile(jsonPath, optional: true, reloadOnChange: false);

        ArcanumSettings persisted = LoadPersistedArcanumSettingsFile(jsonPath);

        ConfigurationEnvironmentSnapshot environment =
            ConfigurationEnvironmentResolver.Resolve(persisted);

        Dictionary<string, string?> projected = new(StringComparer.OrdinalIgnoreCase);

        foreach (ConfigurationEnvironmentOverride item in environment.Overrides)
        {

            if (!item.IsEffective)
            {

                continue;

            }

            string canonical = ConfigurationPathAccessor.GetCanonicalValue(
                environment.EffectiveSettings,
                item.Path);

            ProjectOverride(
                projected,
                $"Arcanum:{item.Path.Replace('.', ':')}",
                canonical,
                TryReadPersistedCanonicalValue(persisted, item.Path));

        }

        if (projected.Count > 0)
        {

            builder.AddInMemoryCollection(projected);

        }

        return builder;

    }

    public static void ValidateArcanumConfigurationFile(string jsonPath)
    {
        _ = LoadPersistedArcanumSettingsFile(jsonPath);
    }

    public static ArcanumSettings LoadArcanumSettings(
        Func<ArcanumSettings>? fallbackFactory = null) =>
        LoadArcanumSettingsFile(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
            fallbackFactory);

    public static ArcanumSettings LoadPersistedArcanumSettings(
        Func<ArcanumSettings>? fallbackFactory = null) =>
        LoadPersistedArcanumSettingsFile(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
            fallbackFactory);

    public static void CopySettings(
        ArcanumSettings source,
        ArcanumSettings destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Edition = source.Edition;
        destination.Host = source.Host;
        destination.Providers = source.Providers;
        destination.DefaultModel = source.DefaultModel;
        destination.FastModel = source.FastModel;
        destination.Security = source.Security;
        destination.Workspaces = source.Workspaces;
        destination.Features = source.Features;
        destination.Integrations = source.Integrations;
        destination.Execution = source.Execution;
        destination.Cost = source.Cost;
        destination.Daemon = source.Daemon;
        destination.Retention = source.Retention;
        destination.Cli = source.Cli;
    }

    internal static ArcanumSettings LoadArcanumSettingsFile(
        string jsonPath,
        Func<ArcanumSettings>? fallbackFactory = null)
    {

        ArcanumSettings persisted = LoadPersistedArcanumSettingsFile(
            jsonPath,
            fallbackFactory);

        return ConfigurationEnvironmentResolver.Resolve(persisted).EffectiveSettings;

    }

    internal static ArcanumSettings LoadPersistedArcanumSettingsFile(
        string jsonPath,
        Func<ArcanumSettings>? fallbackFactory = null)
    {

        if (!File.Exists(jsonPath))
        {
            return fallbackFactory?.Invoke() ?? new ArcanumSettings();

        }

        try
        {

            using FileStream stream = new(
                jsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (stream.Length > MaxConfigurationBytes)
            {

                throw new InvalidOperationException(
                    $"arcanum.json is invalid: configuration exceeds the {MaxConfigurationBytes}-byte limit ({jsonPath})");

            }

            byte[] raw = new byte[stream.Length];

            stream.ReadExactly(raw);

            using JsonDocument document = JsonDocument.Parse(raw);
            Result treeValidation = new ConfigurationValidator()
                .ValidateConfigurationFileJson(document.RootElement);

            if (treeValidation.IsFailure)
            {
                string details = treeValidation.Error.Details is { Count: > 0 } validationDetails
                    ? string.Join(
                        "; ",
                        validationDetails.Select(static detail =>
                            $"{detail.Pointer}: {detail.Detail}"))
                    : treeValidation.Error.Message;

                throw new JsonException(details);
            }

            ArcanumConfigurationFile configurationFile =
                JsonSerializer.Deserialize(
                    raw,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile)
                ?? throw new JsonException("Root value must be a JSON object.");

            return configurationFile.Arcanum;
        }
        catch (JsonException ex)
        {

            throw new InvalidOperationException($"arcanum.json is invalid: {ex.Message} ({jsonPath})", ex);

        }

    }

    /// <summary>
    /// Writes one effective override into the in-memory provider the way the JSON provider would have
    /// written the same value: an array becomes indexed child keys and an object becomes nested keys,
    /// so a consumer enumerating <c>GetChildren()</c> sees the override rather than the file it replaces.
    /// Emitting the raw JSON text at the parent key instead would leave the file's children in place and
    /// silently drop the override.
    /// </summary>
    /// <remarks>
    /// <c>ConfigurationRoot.GetChildren</c> unions child keys across providers, so an override that
    /// shortens a list cannot remove the file's surplus entries by omission. Every key the persisted
    /// value would have produced but the override does not is written back as null, shadowing the stale
    /// entry rather than leaving it to be read as part of the effective list.
    /// </remarks>
    private static void ProjectOverride(
        Dictionary<string, string?> projected,
        string key,
        string canonical,
        string? persistedCanonical)
    {

        Dictionary<string, string?> effectiveKeys = new(StringComparer.OrdinalIgnoreCase);

        using (JsonDocument document = JsonDocument.Parse(canonical))
        {

            FlattenConfigurationValue(effectiveKeys, key, document.RootElement);

        }

        foreach ((string effectiveKey, string? value) in effectiveKeys)
        {

            projected[effectiveKey] = value;

        }

        if (persistedCanonical is null)
        {

            return;

        }

        Dictionary<string, string?> persistedKeys = new(StringComparer.OrdinalIgnoreCase);

        using (JsonDocument persistedDocument = JsonDocument.Parse(persistedCanonical))
        {

            FlattenConfigurationValue(persistedKeys, key, persistedDocument.RootElement);

        }

        foreach (string staleKey in persistedKeys.Keys)
        {

            if (!effectiveKeys.ContainsKey(staleKey))
            {

                projected[staleKey] = null;

            }

        }

    }

    private static void FlattenConfigurationValue(
        Dictionary<string, string?> projected,
        string key,
        JsonElement element)
    {

        switch (element.ValueKind)
        {

            case JsonValueKind.String:

                projected[key] = element.GetString();

                return;

            case JsonValueKind.Null:

                projected[key] = null;

                return;

            case JsonValueKind.Array:

                int index = 0;

                foreach (JsonElement item in element.EnumerateArray())
                {

                    FlattenConfigurationValue(projected, $"{key}:{index}", item);

                    index++;

                }

                return;

            case JsonValueKind.Object:

                foreach (JsonProperty property in element.EnumerateObject())
                {

                    FlattenConfigurationValue(projected, $"{key}:{property.Name}", property.Value);

                }

                return;

            default:

                projected[key] = element.GetRawText();

                return;

        }

    }

    private static string? TryReadPersistedCanonicalValue(ArcanumSettings persisted, string path)
    {

        try
        {

            return ConfigurationPathAccessor.GetCanonicalValue(persisted, path);

        }
        catch (ArgumentException)
        {

            return null;

        }

    }

}
