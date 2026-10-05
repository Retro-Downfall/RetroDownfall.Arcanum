using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{
    public const int MaxConfigurationBytes = 10 * 1024 * 1024;

    private static readonly AsyncLocal<Action<string>?> PersistedFileReadObserverScope = new();

    /// <summary>
    /// Test seam: invoked with the path each time the persisted file is opened for reading. Scoped to
    /// the setting execution context, so a configuration load running on an unrelated thread (another
    /// test class in parallel) is never counted.
    /// </summary>
    internal static Action<string>? PersistedFileReadObserver
    {
        get => PersistedFileReadObserverScope.Value;

        set => PersistedFileReadObserverScope.Value = value;
    }

    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {
        string configPath = ArcanumPaths.GrimoireDirectory;

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        // One read: the same bytes are validated, bound to the settings the environment resolver
        // consumes, and handed to the JSON provider, so the file cannot change between the three.
        (ArcanumSettings persisted, byte[]? raw) = ReadPersistedArcanumSettingsFile(jsonPath, fallbackFactory: null);

        if (raw is not null)
        {
            builder.Add(new ValidatedSnapshotJsonConfigurationSource(raw));
        }

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
        Func<ArcanumSettings>? fallbackFactory = null) =>
        ReadPersistedArcanumSettingsFile(jsonPath, fallbackFactory).Settings;

    /// <summary>
    /// Reads, validates, and binds the persisted file once. The raw bytes are returned beside the
    /// settings (null when the file is absent) so a caller that also needs the JSON provider can feed
    /// it the same snapshot rather than reading the file again.
    /// </summary>
    private static (ArcanumSettings Settings, byte[]? Raw) ReadPersistedArcanumSettingsFile(
        string jsonPath,
        Func<ArcanumSettings>? fallbackFactory)
    {
        if (!File.Exists(jsonPath))
        {
            return (fallbackFactory?.Invoke() ?? new ArcanumSettings(), null);
        }

        try
        {
            PersistedFileReadObserver?.Invoke(jsonPath);

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

            return (configurationFile.Arcanum, raw);
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

    /// <summary>
    /// Feeds the JSON provider the bytes <see cref="ReadPersistedArcanumSettingsFile"/> already
    /// validated, rather than letting it open the file a second time.
    /// </summary>
    private sealed class ValidatedSnapshotJsonConfigurationSource(byte[] raw) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new ValidatedSnapshotJsonConfigurationProvider(raw);
    }

    /// <summary>
    /// A JSON provider over an in-memory snapshot. <c>AddJsonStream</c> would work once and then throw
    /// from <c>IConfigurationRoot.Reload()</c> ("cannot be loaded more than once") and would leave its
    /// stream undisposed; this one reads a fresh, disposed stream from the same validated bytes on every
    /// load, so a reload is a no-op against the snapshot instead of a crash.
    /// </summary>
    private sealed class ValidatedSnapshotJsonConfigurationProvider(byte[] raw)
        : JsonStreamConfigurationProvider(new JsonStreamConfigurationSource())
    {
        public override void Load()
        {
            using MemoryStream stream = new(raw, writable: false);

            Load(stream);
        }
    }
}
