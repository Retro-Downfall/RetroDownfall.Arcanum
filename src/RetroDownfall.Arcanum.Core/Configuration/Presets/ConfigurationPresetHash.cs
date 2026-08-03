using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using System.Text.Json.Nodes;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration.Presets;

public static class ConfigurationPresetHash
{

    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new(ConfigurationJsonContext.Default.Options)
        {

            WriteIndented = false,

        };

    public static string ComputeSettings(ArcanumSettings settings)
    {

        ArgumentNullException.ThrowIfNull(settings);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        return Compute(root.ToJsonString(CanonicalJsonOptions));

    }

    public static string ComputeOwnedValues(
        ArcanumSettings settings,
        IEnumerable<ConfigurationPresetOwnedSetting> ownedSettings) =>
        ComputeValues(settings, ownedSettings.Select(static setting => setting.Path));

    public static string ComputeCanonicalValues(
        IEnumerable<ConfigurationPresetBaselineValue> values)
    {

        ArgumentNullException.ThrowIfNull(values);

        return ComputePairs(values.Select(static value =>
            (value.Path, value.CanonicalJson)));

    }

    public static string ComputeValues(
        ArcanumSettings settings,
        IEnumerable<string> paths)
    {

        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(paths);

        return ComputePairs(paths.Select(path =>
            (path, ConfigurationPathAccessor.GetCanonicalValue(settings, path))));

    }

    private static string ComputePairs(
        IEnumerable<(string Path, string CanonicalJson)> values)
    {

        StringBuilder canonical = new();

        foreach ((string path, string value) in values
                     .DistinctBy(static value => value.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static value => value.Path, StringComparer.Ordinal))
        {

            canonical.Append(path);

            canonical.Append('\0');

            canonical.Append(value);

            canonical.Append('\n');

        }

        return Compute(canonical.ToString());

    }

    private static string Compute(string canonical)
    {

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(digest);

    }

}
