using System.Collections.Concurrent;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class SettingDescriptorCoverageTests
{

    private const string ConfigurationNamespace = "RetroDownfall.Arcanum.Core.Configuration";

    [Fact]

    public void Every_ArcanumSettings_leaf_property_has_a_descriptor()
    {

        HashSet<string> expectedKeys = [];

        CollectExpectedKeys(typeof(ArcanumSettings), prefix: string.Empty, expectedKeys);

        HashSet<string> actualKeys = SettingDescriptors.All.Select(d => d.Key).ToHashSet();

        List<string> missing = expectedKeys.Where(k => !actualKeys.Contains(k)).OrderBy(k => k).ToList();

        if (missing.Count > 0)

        {

            Assert.Fail($"{missing.Count} ArcanumSettings field(s) lack a SettingDescriptor:\n  {string.Join("\n  ", missing)}");

        }

    }

    [Fact]

    public void Descriptor_keys_are_unique()
    {

        List<string> duplicates = SettingDescriptors.All

            .GroupBy(d => d.Key)

            .Where(g => g.Count() > 1)

            .Select(g => g.Key)

            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate SettingDescriptor keys: {string.Join(", ", duplicates)}");

    }

    [Fact]
    public void Workspace_check_custom_profiles_have_one_opaque_dictionary_descriptor()
    {
        SettingDescriptor descriptor = Assert.Single(
            SettingDescriptors.All,
            static d => d.Key == "codingTools.workspaceCheck.customProfiles");

        Assert.Equal(SettingKind.Dictionary, descriptor.Kind);
        Assert.DoesNotContain(
            SettingDescriptors.All,
            static d => d.Key.StartsWith(
                "codingTools.workspaceCheck.customProfiles.",
                StringComparison.Ordinal));
    }

    [Fact]

    public void Every_descriptor_key_matches_a_real_ArcanumSettings_property()
    {

        HashSet<string> expectedKeys = [];

        CollectExpectedKeys(typeof(ArcanumSettings), prefix: string.Empty, expectedKeys);

        List<string> orphaned = SettingDescriptors.All

            .Where(d => !expectedKeys.Contains(d.Key))

            .Select(d => d.Key)

            .OrderBy(k => k)

            .ToList();

        Assert.True(orphaned.Count == 0, $"SettingDescriptor keys with no matching ArcanumSettings field: {string.Join(", ", orphaned)}");

    }

    private static void CollectExpectedKeys(Type type, string prefix, HashSet<string> keys)
    {

        foreach (PropertyInfo property in GetInitProperties(type))

        {

            string name = ToCamelCase(property.Name);

            string key = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

            Type propertyType = UnwrapNullable(property.PropertyType);

            if (IsSubRecord(propertyType))

            {

                CollectExpectedKeys(propertyType, key, keys);

            }
            else if (IsRecordCollection(propertyType, out Type? elementType))

            {

                CollectExpectedKeys(elementType!, key, keys);

            }
            else

            {

                keys.Add(key);

            }

        }

    }

    private static IEnumerable<PropertyInfo> GetInitProperties(Type type)
    {

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)

            .Where(p => p.GetSetMethod(true) is not null);

    }

    private static Type UnwrapNullable(Type type)
    {

        Type? underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null)

        {

            return underlying;

        }

        return type;

    }

    private static bool IsSubRecord(Type type)
    {

        if (type == typeof(string))

        {

            return false;

        }

        if (type.IsEnum)

        {

            return false;

        }

        if (type.IsArray)

        {

            return false;

        }

        if (type.IsGenericType)

        {

            return false;

        }

        return type.IsClass && type.Namespace == ConfigurationNamespace;

    }

    private static bool IsRecordCollection(Type type, out Type? elementType)
    {

        elementType = null;

        if (type.IsArray)

        {

            Type element = type.GetElementType()!;

            if (IsSubRecord(element))

            {

                elementType = element;

                return true;

            }

            return false;

        }

        if (type.IsGenericType)

        {

            Type genericDef = type.GetGenericTypeDefinition();

            if (genericDef == typeof(IReadOnlyList<>) || genericDef == typeof(List<>))

            {

                Type element = type.GetGenericArguments()[0];

                if (IsSubRecord(element))

                {

                    elementType = element;

                    return true;

                }

            }

        }

        return false;

    }

    private static string ToCamelCase(string name)
    {

        if (string.IsNullOrEmpty(name))

        {

            return name;

        }

        if (char.IsLower(name[0]))

        {

            return name;

        }

        if (name.Length == 1)

        {

            return name.ToLowerInvariant();

        }

        return char.ToLowerInvariant(name[0]) + name[1..];

    }

}
