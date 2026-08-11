using System.Globalization;

using System.Text.Json;

using System.Text.Json.Nodes;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves configuration dot paths through source-generated JSON metadata. This keeps the Native
/// AOT CLI aligned with the generated binding contract without runtime property reflection.
/// </summary>
public static class ConfigurationPathAccessor
{

    private const string RedactionMask = "***";

    /// <summary>
    /// Generic collection shapes an ordered settings property may declare. <see cref="IReadOnlyList{T}"/>
    /// belongs here because <see cref="ProviderSettings.Models"/> declares it: omitting it makes every
    /// <c>providers.N.models.M.*</c> descriptor path unresolvable through <c>arcanum config get/set</c>
    /// and through an <c>ARCANUM_Arcanum__…</c> override.
    /// </summary>
    private static readonly Type[] IndexedCollectionDefinitions =
    [
        typeof(List<>),
        typeof(IList<>),
        typeof(IReadOnlyList<>),
    ];

    /// <summary>
    /// JSON shape of settings types written by a hand-authored converter. A converter-backed
    /// <see cref="JsonTypeInfo"/> reports no properties, so the generated-metadata walk cannot see
    /// through <see cref="ModelEntry"/> to its <c>providers.N.models.M.*</c> descriptor paths. These
    /// names and types must stay identical to <see cref="ModelEntryJsonConverter"/>.
    /// </summary>
    private static readonly Dictionary<Type, (string Name, Type Type)[]> ConverterBackedProperties =
        new()
        {

            [typeof(ModelEntry)] =
            [
                ("name", typeof(string)),
                ("supportsVision", typeof(bool)),
                ("reasoning", typeof(ModelReasoningSettings)),
            ],

        };

    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new(ConfigurationJsonContext.Default.Options)
        {

            WriteIndented = false,

        };

    public static ArcanumSettings Clone(ArcanumSettings settings)
    {

        ArgumentNullException.ThrowIfNull(settings);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        return root.Deserialize(ConfigurationJsonContext.Default.ArcanumSettings)
            ?? throw new InvalidOperationException("The cloned configuration snapshot was empty.");

    }

    public static ConfigurationPathUpdate Set(
        ArcanumSettings settings,
        string key,
        string rawValue)
    {

        ArgumentNullException.ThrowIfNull(settings);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        PathResolution resolution = Resolve(root, key);

        if (!resolution.IsSuccess)
        {

            return ConfigurationPathUpdate.Failure(settings, resolution.Error!);

        }

        if (!TryParseValue(
                rawValue,
                resolution.ValueType!,
                out JsonNode? parsed,
                out string? parseError))
        {

            return ConfigurationPathUpdate.Failure(settings, parseError!);

        }

        resolution.Assign(parsed);

        try
        {

            ArcanumSettings? updated = root.Deserialize(
                ConfigurationJsonContext.Default.ArcanumSettings);

            return updated is null
                ? ConfigurationPathUpdate.Failure(settings, "The updated configuration snapshot was empty.")
                : ConfigurationPathUpdate.Success(updated);

        }
        catch (JsonException exception)
        {

            return ConfigurationPathUpdate.Failure(
                settings,
                $"Value for '{key}' is not valid for the generated configuration descriptor: {exception.Message}");

        }

    }

    public static string GetDisplayValue(ArcanumSettings settings, string key)
    {

        ArgumentNullException.ThrowIfNull(settings);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        PathResolution resolution = Resolve(root, key);

        if (!resolution.IsSuccess)
        {

            throw new ArgumentException(resolution.Error, nameof(key));

        }

        JsonNode? value = resolution.Read();

        if (IsSensitive(key))
        {

            string? sensitive = value?.GetValue<string>();

            return string.IsNullOrEmpty(sensitive) ? string.Empty : RedactionMask;

        }

        if (value is null)
        {

            return "null";

        }

        if (value is JsonValue scalar)
        {

            if (scalar.TryGetValue(out string? text))
            {

                return text ?? "null";

            }

            if (scalar.TryGetValue(out bool boolean))
            {

                return boolean ? "true" : "false";

            }

        }

        return value.ToJsonString(ConfigurationJsonContext.Default.Options);

    }

    public static string GetCanonicalValue(ArcanumSettings settings, string key)
    {

        ArgumentNullException.ThrowIfNull(settings);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        PathResolution resolution = Resolve(root, key);

        if (!resolution.IsSuccess)
        {

            throw new ArgumentException(resolution.Error, nameof(key));

        }

        JsonNode? value = resolution.Read();

        return value?.ToJsonString(CanonicalJsonOptions) ?? "null";

    }

    public static ConfigurationPathUpdate SetCanonicalValue(
        ArcanumSettings settings,
        string key,
        string canonicalJson)
    {

        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(canonicalJson);

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        PathResolution resolution = Resolve(root, key);

        if (!resolution.IsSuccess)
        {

            return ConfigurationPathUpdate.Failure(settings, resolution.Error!);

        }

        JsonNode? parsed;

        try
        {

            parsed = JsonNode.Parse(canonicalJson);

        }
        catch (JsonException exception)
        {

            return ConfigurationPathUpdate.Failure(
                settings,
                $"Value for '{key}' is not canonical JSON: {exception.Message}");

        }

        resolution.Assign(parsed);

        try
        {

            ArcanumSettings? updated = root.Deserialize(
                ConfigurationJsonContext.Default.ArcanumSettings);

            return updated is null
                ? ConfigurationPathUpdate.Failure(
                    settings,
                    "The updated configuration snapshot was empty.")
                : ConfigurationPathUpdate.Success(updated);

        }
        catch (JsonException exception)
        {

            return ConfigurationPathUpdate.Failure(
                settings,
                $"Canonical value for '{key}' is not valid for the generated configuration descriptor: {exception.Message}");

        }

    }

    /// <summary>
    /// Compares two configuration snapshots and returns one row per differing leaf, addressed by the
    /// same dotted descriptor path <see cref="Set"/> accepts. Sensitive leaves are reported as
    /// changed but their values are masked, so a caller can print a precise diff without disclosing
    /// a provider endpoint or any other sensitive value.
    /// </summary>
    public static IReadOnlyList<ConfigurationPathDifference> Diff(
        ArcanumSettings before,
        ArcanumSettings after)
    {

        ArgumentNullException.ThrowIfNull(before);

        ArgumentNullException.ThrowIfNull(after);

        List<ConfigurationPathDifference> differences = [];

        CollectDifferences(
            JsonSerializer.SerializeToNode(before, ConfigurationJsonContext.Default.ArcanumSettings),
            JsonSerializer.SerializeToNode(after, ConfigurationJsonContext.Default.ArcanumSettings),
            path: string.Empty,
            differences);

        return differences;

    }

    private static void CollectDifferences(
        JsonNode? before,
        JsonNode? after,
        string path,
        List<ConfigurationPathDifference> differences)
    {

        // A newly added (or removed) object/array must still be reported leaf by leaf: reporting the
        // whole node would print a sensitive leaf such as a provider endpoint verbatim, because
        // sensitivity is decided by the leaf's own path.
        if (before is null && after is JsonObject)
        {

            before = new JsonObject();

        }
        else if (after is null && before is JsonObject)
        {

            after = new JsonObject();

        }
        else if (before is null && after is JsonArray)
        {

            before = new JsonArray();

        }
        else if (after is null && before is JsonArray)
        {

            after = new JsonArray();

        }

        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {

            foreach (string name in beforeObject
                .Select(static property => property.Key)
                .Concat(afterObject.Select(static property => property.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal))
            {

                CollectDifferences(
                    beforeObject[name],
                    afterObject[name],
                    Combine(path, name),
                    differences);

            }

            return;

        }

        if (before is JsonArray beforeArray && after is JsonArray afterArray)
        {

            for (int index = 0; index < Math.Max(beforeArray.Count, afterArray.Count); index++)
            {

                CollectDifferences(
                    index < beforeArray.Count ? beforeArray[index] : null,
                    index < afterArray.Count ? afterArray[index] : null,
                    Combine(path, index.ToString(CultureInfo.InvariantCulture)),
                    differences);

            }

            return;

        }

        string beforeJson = before?.ToJsonString(CanonicalJsonOptions) ?? "null";

        string afterJson = after?.ToJsonString(CanonicalJsonOptions) ?? "null";

        if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {

            return;

        }

        bool sensitive = IsSensitive(path);

        differences.Add(
            new ConfigurationPathDifference(
                path,
                sensitive ? Mask(beforeJson) : beforeJson,
                sensitive ? Mask(afterJson) : afterJson,
                sensitive));

    }

    private static string Mask(string canonicalJson) =>
        string.Equals(canonicalJson, "null", StringComparison.Ordinal)
            ? "null"
            : RedactionMask;

    private static string Combine(string path, string segment) =>
        path.Length == 0 ? segment : path + "." + segment;

    public static bool IsSensitive(string key)
    {

        string[] segments = Split(key);

        return segments.Length == 3
            && string.Equals(segments[0], "providers", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && string.Equals(segments[2], "endpoint", StringComparison.OrdinalIgnoreCase);

    }

    public static bool Exists(ArcanumSettings settings, string key)
    {

        JsonNode root = JsonSerializer.SerializeToNode(
                settings,
                ConfigurationJsonContext.Default.ArcanumSettings)
            ?? new JsonObject();

        return Resolve(root, key).IsSuccess;

    }

    private static PathResolution Resolve(JsonNode root, string key)
    {

        string[] segments = Split(key);

        if (segments.Length == 0)
        {

            return PathResolution.Failure("Configuration key must not be empty.");

        }

        JsonNode? node = root;

        Type nodeType = typeof(ArcanumSettings);

        JsonNode? parent = null;

        string? propertyName = null;

        int? arrayIndex = null;

        for (int index = 0; index < segments.Length; index++)
        {

            string segment = segments[index];

            if (TryGetElementType(nodeType, out Type? elementType))
            {

                if (node is not JsonArray array
                    || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedIndex)
                    || parsedIndex < 0
                    || parsedIndex >= array.Count)
                {

                    return PathResolution.Failure(
                        $"Unknown configuration key '{key}': '{segment}' is not an existing collection index.");

                }

                parent = array;

                arrayIndex = parsedIndex;

                propertyName = null;

                node = array[parsedIndex];

                nodeType = elementType!;

                continue;

            }

            if (!TryGetPropertyMetadata(
                    nodeType,
                    segment,
                    out string? resolvedName,
                    out Type? resolvedType)
                || node is not JsonObject jsonObject)
            {

                return PathResolution.Failure(
                    $"Unknown configuration key '{key}' at segment '{segment}'.");

            }

            parent = jsonObject;

            propertyName = resolvedName;

            arrayIndex = null;

            node = FindPropertyValue(jsonObject, resolvedName!);

            nodeType = resolvedType!;

            if (index < segments.Length - 1 && node is null)
            {

                node = TryGetElementType(nodeType, out _) ? new JsonArray() : new JsonObject();

                jsonObject[resolvedName!] = node;

            }

        }

        return parent is null
            ? PathResolution.Failure($"Unknown configuration key '{key}'.")
            : PathResolution.Success(parent, propertyName, arrayIndex, nodeType);

    }

    /// <summary>
    /// Resolves one path segment to its canonical JSON property name and declared type. Generated
    /// metadata is authoritative; a converter-backed type reports no
    /// <see cref="JsonTypeInfo.Properties"/>, so its hand-authored shape is declared in
    /// <see cref="ConverterBackedProperties"/> instead.
    /// </summary>
    private static bool TryGetPropertyMetadata(
        Type nodeType,
        string segment,
        out string? propertyName,
        out Type? propertyType)
    {

        JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(nodeType);

        JsonPropertyInfo? property = typeInfo?.Properties.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                segment,
                StringComparison.OrdinalIgnoreCase));

        if (property is not null)
        {

            propertyName = property.Name;

            propertyType = property.PropertyType;

            return true;

        }

        if (ConverterBackedProperties.TryGetValue(nodeType, out (string Name, Type Type)[]? declared))
        {

            foreach ((string name, Type type) in declared)
            {

                if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                {

                    propertyName = name;

                    propertyType = type;

                    return true;

                }

            }

        }

        propertyName = null;

        propertyType = null;

        return false;

    }

    private static JsonNode? FindPropertyValue(JsonObject jsonObject, string propertyName)
    {

        foreach ((string name, JsonNode? value) in jsonObject)
        {

            if (string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
            {

                return value;

            }

        }

        return null;

    }

    private static bool TryGetElementType(Type type, out Type? elementType)
    {

        if (type.IsArray)
        {

            elementType = type.GetElementType();

            return elementType is not null;

        }

        if (type.IsGenericType
            && IndexedCollectionDefinitions.Contains(type.GetGenericTypeDefinition()))
        {

            elementType = type.GenericTypeArguments[0];

            return true;

        }

        elementType = null;

        return false;

    }

    private static bool TryParseValue(
        string rawValue,
        Type declaredType,
        out JsonNode? value,
        out string? error)
    {

        Type targetType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        string trimmed = rawValue.Trim();

        if (targetType == typeof(string))
        {

            value = JsonValue.Create(rawValue);

            error = null;

            return true;

        }

        if (targetType == typeof(bool))
        {

            if (bool.TryParse(trimmed, out bool parsed)
                || TryParseBinaryBoolean(trimmed, out parsed))
            {

                value = JsonValue.Create(parsed);

                error = null;

                return true;

            }

            value = null;

            error = "Expected a Boolean value ('true' or 'false').";

            return false;

        }

        if (targetType == typeof(int))
        {

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {

                value = JsonValue.Create(parsed);

                error = null;

                return true;

            }

            value = null;

            error = "Expected an integer value.";

            return false;

        }

        if (targetType == typeof(long))
        {

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {

                value = JsonValue.Create(parsed);

                error = null;

                return true;

            }

            value = null;

            error = "Expected an integer value.";

            return false;

        }

        if (targetType == typeof(float))
        {

            if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {

                value = JsonValue.Create(parsed);

                error = null;

                return true;

            }

            value = null;

            error = "Expected a numeric value.";

            return false;

        }

        if (targetType == typeof(double))
        {

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {

                value = JsonValue.Create(parsed);

                error = null;

                return true;

            }

            value = null;

            error = "Expected a numeric value.";

            return false;

        }

        if (TryGetElementType(targetType, out Type? elementType)
            && (elementType == typeof(string) || elementType == typeof(Guid)))
        {

            if (!TryParseListItems(rawValue, out string[] items, out error))
            {

                value = null;

                return false;

            }

            if (elementType == typeof(Guid))
            {

                for (int index = 0; index < items.Length; index++)
                {

                    if (!Guid.TryParse(items[index], out Guid parsedIdentifier))
                    {

                        value = null;

                        error = $"Expected a comma-separated list or valid JSON array of GUIDs: '{items[index]}' is not a GUID.";

                        return false;

                    }

                    items[index] = parsedIdentifier.ToString();

                }

            }

            value = JsonSerializer.SerializeToNode(
                items,
                ConfigurationJsonContext.Default.StringArray);

            error = null;

            return true;

        }

        if (targetType.IsEnum)
        {

            if (!Enum.TryParse(targetType, trimmed, ignoreCase: true, out object? parsed))
            {

                value = null;

                error = $"Expected a named value for configuration enum '{targetType.Name}'.";

                return false;

            }

            JsonTypeInfo? enumTypeInfo = ConfigurationJsonContext.Default.GetTypeInfo(targetType);

            // Write the enum the way its own contract writes it. Several configuration enums declare
            // StringOnlyJsonStringEnumConverter, which rejects the numeric form on read.
            value = enumTypeInfo is null
                ? JsonValue.Create(Convert.ToInt32(parsed, CultureInfo.InvariantCulture))
                : JsonSerializer.SerializeToNode(parsed, enumTypeInfo);

            error = null;

            return true;

        }

        try
        {

            value = JsonNode.Parse(trimmed);

            error = null;

            return true;

        }
        catch (JsonException exception)
        {

            value = null;

            error = TryGetElementType(targetType, out _)
                ? $"Expected a valid JSON array: {exception.Message}"
                : $"Expected valid JSON for configuration type '{targetType.Name}': {exception.Message}";

            return false;

        }

    }

    /// <summary>
    /// Accepts either the plain comma-separated form or an explicit JSON array for any collection
    /// key, so a single-valued list never has to be spelled as JSON.
    /// </summary>
    private static bool TryParseListItems(
        string rawValue,
        out string[] items,
        out string? error)
    {

        string trimmed = rawValue.Trim();

        try
        {

            items = trimmed.StartsWith("[", StringComparison.Ordinal)
                ? JsonSerializer.Deserialize(trimmed, ConfigurationJsonContext.Default.StringArray) ?? []
                : rawValue.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            error = null;

            return true;

        }
        catch (JsonException exception)
        {

            items = [];

            error = $"Expected a comma-separated list or valid JSON array: {exception.Message}";

            return false;

        }

    }

    private static bool TryParseBinaryBoolean(string value, out bool parsed)
    {

        if (string.Equals(value, "1", StringComparison.Ordinal))
        {

            parsed = true;

            return true;

        }

        if (string.Equals(value, "0", StringComparison.Ordinal))
        {

            parsed = false;

            return true;

        }

        parsed = false;

        return false;

    }

    private static string[] Split(string key) =>
        key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record PathResolution(
        bool IsSuccess,
        JsonNode? Parent,
        string? PropertyName,
        int? ArrayIndex,
        Type? ValueType,
        string? Error)
    {

        public static PathResolution Success(
            JsonNode parent,
            string? propertyName,
            int? arrayIndex,
            Type valueType) =>
            new(true, parent, propertyName, arrayIndex, valueType, null);

        public static PathResolution Failure(string error) =>
            new(false, null, null, null, null, error);

        public JsonNode? Read() =>
            Parent switch
            {
                JsonObject jsonObject when PropertyName is not null => jsonObject[PropertyName],
                JsonArray jsonArray when ArrayIndex is not null => jsonArray[ArrayIndex.Value],
                _ => null,
            };

        public void Assign(JsonNode? value)
        {

            switch (Parent)
            {

                case JsonObject jsonObject when PropertyName is not null:

                    jsonObject[PropertyName] = value;

                    break;

                case JsonArray jsonArray when ArrayIndex is not null:

                    jsonArray[ArrayIndex.Value] = value;

                    break;

                default:

                    throw new InvalidOperationException("The resolved configuration path has no assignable target.");

            }

        }

    }

}

/// <summary>
/// One differing configuration leaf. <see cref="Before"/> and <see cref="After"/> are canonical JSON
/// except where <see cref="IsSensitive"/> is <see langword="true"/>, in which case a present value is
/// masked and only its presence is disclosed.
/// </summary>
public sealed record ConfigurationPathDifference(
    string Path,
    string Before,
    string After,
    bool IsSensitive);

public sealed record ConfigurationPathUpdate(
    bool IsSuccess,
    ArcanumSettings? Settings,
    string? Error)
{

    public static ConfigurationPathUpdate Success(ArcanumSettings settings) =>
        new(true, settings, null);

    public static ConfigurationPathUpdate Failure(ArcanumSettings settings, string error) =>
        new(false, settings, error);

}
