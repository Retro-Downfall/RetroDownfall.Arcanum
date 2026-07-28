using System.Reflection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;

namespace RetroDownfall.Compendium.Ux.ViewModels;

/// <summary>
/// Applies generic descriptor field edits onto an <see cref="ArcanumSettings"/> snapshot.
/// Uses reflection because Compendium is a desktop editor (not Native AOT-shipped).
/// </summary>
public static class GenericSettingsUpdater
{

    public static object? ReadValue(ArcanumSettings settings, string key)
    {

        object? node = settings;

        foreach (string part in key.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {

            if (node is null)
            {

                return null;

            }

            PropertyInfo? property = node.GetType().GetProperty(
                ToPascal(part),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (property is null)
            {

                return null;

            }

            node = property.GetValue(node);

        }

        return node;

    }

    public static ArcanumSettings ApplyFields(ArcanumSettings settings, IReadOnlyList<GenericSettingFieldViewModel> fields)
    {

        ArcanumSettings result = settings;

        foreach (GenericSettingFieldViewModel field in fields)
        {

            result = SetByPath(result, field.Descriptor.Key, Coerce(field)) ?? result;

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

    private static ArcanumSettings? SetByPath(ArcanumSettings root, string key, object? value)
    {

        string[] parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {

            return null;

        }

        return SetByPath(root, parts, 0, value) as ArcanumSettings;

    }

    private static object? SetByPath(object node, string[] parts, int index, object? value)
    {

        string part = parts[index];

        PropertyInfo? property = node.GetType().GetProperty(
            ToPascal(part),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property is null)
        {

            return null;

        }

        if (index == parts.Length - 1)
        {

            return SetInitProperty(node, property, CoerceToPropertyType(property.PropertyType, value));

        }

        object? child = property.GetValue(node);

        if (child is null)
        {

            child = Activator.CreateInstance(property.PropertyType);

            if (child is null)
            {

                return null;

            }

        }

        object? updatedChild = SetByPath(child, parts, index + 1, value);

        if (updatedChild is null)
        {

            return null;

        }

        return SetInitProperty(node, property, updatedChild);

    }

    private static object? SetInitProperty(object node, PropertyInfo property, object? value)
    {

        Type type = node.GetType();

        // C# records expose a compiler-generated <Clone>$ method. Prefer that so init-only
        // properties can be updated via reflection without reconstructing via the copy ctor.
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
                type.GetProperty(
                    p.Name ?? string.Empty,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null
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

            PropertyInfo? source = type.GetProperty(
                parameter.Name ?? string.Empty,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

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

    private static string ToPascal(string value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return value;

        }

        return char.ToUpperInvariant(value[0]) + value[1..];

    }

}
