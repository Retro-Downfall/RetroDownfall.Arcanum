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

            JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(nodeType);

            JsonPropertyInfo? property = typeInfo?.Properties.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Name,
                    segment,
                    StringComparison.OrdinalIgnoreCase));

            if (property is null || node is not JsonObject jsonObject)
            {

                return PathResolution.Failure(
                    $"Unknown configuration key '{key}' at segment '{segment}'.");

            }

            parent = jsonObject;

            propertyName = property.Name;

            arrayIndex = null;

            node = FindPropertyValue(jsonObject, property.Name);

            nodeType = property.PropertyType;

            if (index < segments.Length - 1 && node is null)
            {

                node = TryGetElementType(nodeType, out _) ? new JsonArray() : new JsonObject();

                jsonObject[property.Name] = node;

            }

        }

        return parent is null
            ? PathResolution.Failure($"Unknown configuration key '{key}'.")
            : PathResolution.Success(parent, propertyName, arrayIndex, nodeType);

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
            && type.GetGenericTypeDefinition() == typeof(List<>))
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

        if (targetType == typeof(string[]))
        {

            string[] items;

            try
            {

                items = trimmed.StartsWith("[", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize(trimmed, ConfigurationJsonContext.Default.StringArray) ?? []
                    : rawValue.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            }
            catch (JsonException exception)
            {

                value = null;

                error = $"Expected a comma-separated list or valid JSON string array: {exception.Message}";

                return false;

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

            value = JsonValue.Create(Convert.ToInt32(parsed, CultureInfo.InvariantCulture));

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

            error = $"Expected valid JSON for configuration type '{targetType.Name}': {exception.Message}";

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
