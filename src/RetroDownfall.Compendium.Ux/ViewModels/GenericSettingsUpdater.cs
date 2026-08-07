using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Compendium.Ux.Models;

namespace RetroDownfall.Compendium.Ux.ViewModels;

/// <summary>
/// Applies generic descriptor field edits onto an <see cref="ArcanumSettings"/> snapshot.
/// Uses reflection because Compendium is a desktop editor (not Native AOT-shipped).
/// PropertyInfo lookups are cached per (Type, propertyName) pair to avoid repeated reflection.
/// </summary>
public static class GenericSettingsUpdater
{

    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();

    private static readonly BindingFlags PropertyLookupFlags =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    public static object? ReadValue(ArcanumSettings settings, string key)
    {

        object? node = settings;

        foreach (string part in key.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {

            if (node is null)
            {

                return null;

            }

            PropertyInfo? property = GetCachedProperty(node.GetType(), ToPascal(part));

            if (property is null)
            {

                return null;

            }

            node = property.GetValue(node);

        }

        return node;

    }

    /// <summary>
    /// Resolves the declared CLR type of the property a descriptor key addresses, or <c>null</c> when the
    /// path does not exist. Lets the editor validate a value against the exact type Save will coerce to.
    /// </summary>
    public static Type? ResolveValueType(string key)
    {

        Type node = typeof(ArcanumSettings);

        string[] parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {

            return null;

        }

        foreach (string part in parts)
        {

            PropertyInfo? property = GetCachedProperty(node, ToPascal(part));

            if (property is null)
            {

                return null;

            }

            node = property.PropertyType;

        }

        return node;

    }

    public static ArcanumSettings ApplyFields(
        ArcanumSettings settings,
        IReadOnlyList<GenericSettingFieldViewModel> fields,
        ILogger? logger = null)
    {

        ArcanumSettings result = settings;

        foreach (GenericSettingFieldViewModel field in fields)
        {

            if (field.HasError)
            {

                logger?.LogWarning(
                    "Skipped invalid generic setting '{Key}'; the previous value was preserved.",
                    field.Descriptor.Key);

                continue;

            }

            ArcanumSettings? updated;

            try
            {

                updated = SetByPath(result, field.Descriptor.Key, Coerce(field), logger);

            }
            catch (Exception ex) when (
                ex is JsonException
                or FormatException
                or OverflowException
                or ArgumentException
                or InvalidCastException)
            {

                // Attribute the failure to the key that caused it: an unattributed exception escaping
                // here aborts the whole save with a message naming no field and no section.
                throw new InvalidOperationException(
                    $"'{field.Descriptor.Key}' could not be applied: {ex.Message}",
                    ex);

            }

            if (updated is null)
            {

                logger?.LogWarning(
                    "Failed to apply generic setting '{Key}' (kind {Kind}) to the configuration snapshot; the previous value was preserved.",
                    field.Descriptor.Key,
                    field.Descriptor.Kind);

                continue;

            }

            result = updated;

        }

        return result;

    }

    private static object? Coerce(GenericSettingFieldViewModel field)
    {

        object? value = field.Value;

        return field.Descriptor.Kind switch
        {
            SettingKind.StringArray when value is string s =>
                s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SettingKind.StringArray when value is string[] arr => arr,
            SettingKind.Int when value is double d => (int)Math.Round(d),
            SettingKind.Long when value is double d => (long)Math.Round(d),
            SettingKind.Float when value is double d => (float)d,
            SettingKind.Bool => value is true,
            _ => value,
        };

    }

    private static ArcanumSettings? SetByPath(ArcanumSettings root, string key, object? value, ILogger? logger)
    {

        string[] parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {

            return null;

        }

        return SetByPath(root, parts, 0, value, logger) as ArcanumSettings;

    }

    private static object? SetByPath(object node, string[] parts, int index, object? value, ILogger? logger)
    {

        string part = parts[index];

        PropertyInfo? property = GetCachedProperty(node.GetType(), ToPascal(part));

        if (property is null)
        {

            logger?.LogWarning(
                "Configuration property '{PropertyName}' was not found on type '{TypeName}' while applying path segment '{Segment}'.",
                ToPascal(part),
                node.GetType().Name,
                part);

            return null;

        }

        if (index == parts.Length - 1)
        {

            return SetPropertyOnClone(node, property, CoerceToPropertyType(property.PropertyType, value));

        }

        object? child = property.GetValue(node);

        if (child is null)
        {

            try
            {

                child = Activator.CreateInstance(property.PropertyType);

            }
            catch (Exception ex)
            {

                logger?.LogWarning(
                    ex,
                    "Could not instantiate intermediate configuration type '{TypeName}' for path segment '{Segment}'.",
                    property.PropertyType.Name,
                    part);

                return null;

            }

            if (child is null)
            {

                return null;

            }

        }

        object? updatedChild = SetByPath(child, parts, index + 1, value, logger);

        if (updatedChild is null)
        {

            return null;

        }

        return SetPropertyOnClone(node, property, updatedChild);

    }

    private static object? SetPropertyOnClone(object node, PropertyInfo property, object? value)
    {

        Type type = node.GetType();

        // Clone each record on the traversed path so an edit never mutates the loaded snapshot.
        MethodInfo? cloneMethod = type.GetMethod(
            "<Clone>$",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (cloneMethod is not null)
        {

            object? clone = cloneMethod.Invoke(node, null);

            if (clone is null)
            {

                return null;

            }

            property.SetValue(clone, value);

            return clone;

        }

        ConstructorInfo? ctor = type.GetConstructors()
            .Where(c => c.GetParameters().All(p =>
                GetCachedProperty(type, p.Name ?? string.Empty) is not null
                || p.HasDefaultValue))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor is null)
        {

            object? copy = Activator.CreateInstance(type);

            if (copy is null)
            {

                return null;

            }

            foreach (PropertyInfo source in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {

                if (!source.CanRead || !source.CanWrite)
                {

                    continue;

                }

                source.SetValue(
                    copy,
                    source.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)
                        ? value
                        : source.GetValue(node));

            }

            return copy;

        }

        ParameterInfo[] parameters = ctor.GetParameters();

        object?[] args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {

            ParameterInfo parameter = parameters[i];

            PropertyInfo? source = GetCachedProperty(type, parameter.Name ?? string.Empty);

            if (source is not null && source.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
            {

                args[i] = value;

            }
            else if (source is not null)
            {

                args[i] = source.GetValue(node);

            }
            else if (parameter.HasDefaultValue)
            {

                args[i] = parameter.DefaultValue;

            }

        }

        return ctor.Invoke(args);

    }

    private static object? CoerceToPropertyType(Type targetType, object? value)
    {

        if (value is null)
        {

            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null
                ? Activator.CreateInstance(targetType)
                : null;

        }

        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsInstanceOfType(value))
        {

            return value;

        }

        if (underlying.IsEnum)
        {

            if (value is string s)
            {

                return Enum.Parse(underlying, s, ignoreCase: true);

            }

            return Enum.ToObject(underlying, value);

        }

        if (underlying == typeof(string[]))
        {

            if (value is string[] arr)
            {

                return arr;

            }

            if (value is string text)
            {

                return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            }

        }

        if (underlying == typeof(Guid[]))
        {

            if (value is IEnumerable<string> ids)
            {

                return ids.Select(Guid.Parse).ToArray();

            }

            if (value is string text)
            {

                return text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Guid.Parse)
                    .ToArray();

            }

        }

        if (underlying == typeof(List<string>))
        {

            if (value is IEnumerable<string> items)
            {

                return items.ToList();

            }

            if (value is string text)
            {

                return text.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            }

        }

        if (underlying.IsGenericType
            && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && value is string json)
        {

            return JsonSerializer.Deserialize(
                string.IsNullOrWhiteSpace(json) ? "{}" : json,
                underlying,
                ConfigurationJsonContext.Default);

        }

        if (underlying == typeof(string))
        {

            return value.ToString();

        }

        try
        {

            return Convert.ChangeType(value, underlying);

        }
        catch
        {

            return value;

        }

    }

    private static PropertyInfo? GetCachedProperty(Type type, string name)
    {

        return PropertyCache.GetOrAdd((type, name), static key => key.Type.GetProperty(key.Name, PropertyLookupFlags));

    }

    private static string ToPascal(string value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return value;

        }

        return char.ToUpperInvariant(value[0]) + value[1..];

    }

}
