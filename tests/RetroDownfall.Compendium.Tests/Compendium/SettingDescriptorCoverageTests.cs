using System.Reflection;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Compendium.Ux.Models;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class SettingDescriptorCoverageTests
{

    private const string ConfigurationNamespace = "RetroDownfall.Arcanum.Core.Configuration";

    private static readonly string[] RemovedProviderProfilePrefixes =
    [
        "providers.tokenization",
        "providers.promptCaching",
        "providers.supportsPromptCaching",
        "providers.models.tokenization",
        "providers.models.promptCaching",
    ];

    /// <summary>
    /// docs/Compendium.README.md declares itself the sole complete reference for arcanum.json and states
    /// the exact number of editable paths in <see cref="SettingDescriptors.All"/>. Pin the count so the
    /// two cannot drift silently; update both together when a descriptor is added or removed.
    /// </summary>
    [Fact]
    public void Editable_descriptor_count_matches_the_documented_total()
    {

        Assert.Equal(159, SettingDescriptors.All.Count);

    }

    [Fact]
    public void Every_retained_public_choice_has_an_editable_descriptor()
    {

        HashSet<string> expectedKeys = [];

        CollectEditableKeys(typeof(ArcanumSettings), prefix: string.Empty, expectedKeys);

        HashSet<string> actualKeys = SettingDescriptors.All
            .Select(static descriptor => descriptor.Key)
            .ToHashSet(StringComparer.Ordinal);

        List<string> missing = expectedKeys
            .Where(key => !actualKeys.Contains(key))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} retained public choice(s) lack an editable SettingDescriptor:"
            + $"\n  {string.Join("\n  ", missing)}");

    }

    [Fact]
    public void Every_descriptor_key_matches_a_retained_public_choice()
    {

        HashSet<string> expectedKeys = [];

        CollectEditableKeys(typeof(ArcanumSettings), prefix: string.Empty, expectedKeys);

        List<string> orphaned = SettingDescriptors.All
            .Where(descriptor => !expectedKeys.Contains(descriptor.Key))
            .Select(static descriptor => descriptor.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            $"SettingDescriptor keys with no retained public choice: {string.Join(", ", orphaned)}");

    }

    [Fact]
    public void Descriptor_keys_are_unique()
    {

        List<string> duplicates = SettingDescriptors.All
            .GroupBy(static descriptor => descriptor.Key, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Duplicate SettingDescriptor keys: {string.Join(", ", duplicates)}");

    }

    [Theory]

    [InlineData("host.auditLog.retentionDays")]

    [InlineData("security.guardrails.auditLog.retentionDays")]

    [InlineData("integrations.embeddings.codebaseIndexing")]

    [InlineData("integrations.embeddings.attachmentIndexing")]

    [InlineData("integrations.embeddings.requestTimeoutSeconds")]

    [InlineData("execution.maxPendingApprenticeStarts")]

    [InlineData("retention.maxItemsPerSweep")]

    [InlineData("retention.checkpointInterval")]

    public void Internal_workflow_controls_are_not_exposed(string removedPath)
    {

        Assert.DoesNotContain(
            SettingDescriptors.All,
            descriptor => string.Equals(
                    descriptor.Key,
                    removedPath,
                    StringComparison.Ordinal)
                || descriptor.Key.StartsWith(
                    removedPath + ".",
                    StringComparison.Ordinal));

    }

    [Fact]
    public void Descriptor_sections_are_exactly_the_minimal_navigation()
    {

        ConfigSection[] expected =
        [
            ConfigSection.Presets,
            ConfigSection.Edition,
            ConfigSection.Host,
            ConfigSection.Providers,
            ConfigSection.Security,
            ConfigSection.Workspaces,
            ConfigSection.Features,
            ConfigSection.Integrations,
            ConfigSection.Execution,
            ConfigSection.Cost,
            ConfigSection.Retention,
            ConfigSection.Daemon,
            ConfigSection.Cli,
        ];

        Assert.Equal(expected, Enum.GetValues<ConfigSection>());
        Assert.Equal(expected, SectionDescriptors.All.Select(static descriptor => descriptor.Section));
        Assert.All(
            SettingDescriptors.All,
            descriptor => Assert.Contains(descriptor.Section, expected));

        Assert.True(SectionDescriptors.IsPolished(ConfigSection.Presets));

        Assert.DoesNotContain(
            SettingDescriptors.All,
            static descriptor => descriptor.Section == ConfigSection.Presets);

    }

    [Fact]
    public void Opaque_authored_maps_have_one_dictionary_editor_each()
    {

        string[] dictionaryPaths =
        [
            "integrations.workspaceChecks.customProfiles",
            "cost.pricing.modelPricing",
        ];

        foreach (string path in dictionaryPaths)
        {

            SettingDescriptor descriptor = Assert.Single(
                SettingDescriptors.All,
                candidate => candidate.Key == path);

            Assert.Equal(SettingKind.Dictionary, descriptor.Kind);
            Assert.DoesNotContain(
                SettingDescriptors.All,
                candidate => candidate.Key.StartsWith(
                    path + ".",
                    StringComparison.Ordinal));

        }

    }

    [Fact]
    public void Removed_implementation_profiles_have_no_descriptors()
    {

        Assert.DoesNotContain(
            SettingDescriptors.All,
            descriptor => RemovedProviderProfilePrefixes.Any(prefix =>
                descriptor.Key.Equals(prefix, StringComparison.Ordinal)
                || descriptor.Key.StartsWith(prefix + ".", StringComparison.Ordinal)));

        Assert.DoesNotContain(
            SectionDescriptors.All,
            descriptor => descriptor.Title.Contains("Advanced", StringComparison.OrdinalIgnoreCase)
                || descriptor.Title.Contains("Basic", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Retained_configuration_contract_is_source_generated_and_mutable()
    {

        HashSet<Type> configurationTypes = [];

        CollectConfigurationTypes(typeof(ArcanumSettings), configurationTypes);

        foreach (Type type in configurationTypes.OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {

            Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(type));

            foreach (PropertyInfo property in GetBindableProperties(type))
            {

                MethodInfo setter = property.SetMethod!;

                Assert.DoesNotContain(
                    typeof(IsExternalInit),
                    setter.ReturnParameter.GetRequiredCustomModifiers());

            }

        }

    }

    private static void CollectEditableKeys(
        Type type,
        string prefix,
        HashSet<string> keys)
    {

        foreach (PropertyInfo property in GetBindableProperties(type))
        {

            string name = ToCamelCase(property.Name);
            string key = string.IsNullOrEmpty(prefix)
                ? name
                : $"{prefix}.{name}";

            Type propertyType = UnwrapNullable(property.PropertyType);

            if (IsDictionary(propertyType))
            {

                keys.Add(key);

            }
            else if (TryGetRecordCollectionElement(propertyType, out Type? elementType))
            {

                CollectEditableKeys(elementType!, key, keys);

            }
            else if (IsConfigurationRecord(propertyType))
            {

                CollectEditableKeys(propertyType, key, keys);

            }
            else
            {

                keys.Add(key);

            }

        }

    }

    private static void CollectConfigurationTypes(Type type, HashSet<Type> types)
    {

        type = UnwrapNullable(type);

        if (!IsConfigurationRecord(type) || !types.Add(type))
        {

            return;

        }

        foreach (PropertyInfo property in GetBindableProperties(type))
        {

            Type propertyType = UnwrapNullable(property.PropertyType);

            if (TryGetRecordCollectionElement(propertyType, out Type? elementType))
            {

                CollectConfigurationTypes(elementType!, types);

            }
            else if (TryGetDictionaryValue(propertyType, out Type? valueType))
            {

                CollectConfigurationTypes(valueType!, types);

            }
            else
            {

                CollectConfigurationTypes(propertyType, types);

            }

        }

    }

    private static IEnumerable<PropertyInfo> GetBindableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property =>
                property.GetMethod?.IsPublic == true
                && property.SetMethod?.IsPublic == true);

    private static Type UnwrapNullable(Type type) =>
        Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsConfigurationRecord(Type type) =>
        type.IsClass
        && type != typeof(string)
        && type.Namespace == ConfigurationNamespace;

    private static bool IsDictionary(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private static bool TryGetDictionaryValue(Type type, out Type? valueType)
    {

        valueType = null;

        if (!IsDictionary(type))
        {

            return false;

        }

        valueType = type.GetGenericArguments()[1];

        return true;

    }

    private static bool TryGetRecordCollectionElement(Type type, out Type? elementType)
    {

        elementType = null;

        if (type.IsArray)
        {

            Type element = type.GetElementType()!;

            if (IsConfigurationRecord(element))
            {

                elementType = element;

                return true;

            }

            return false;

        }

        if (!type.IsGenericType)
        {

            return false;

        }

        Type genericDefinition = type.GetGenericTypeDefinition();

        if (genericDefinition != typeof(IReadOnlyList<>)
            && genericDefinition != typeof(List<>))
        {

            return false;

        }

        Type candidate = type.GetGenericArguments()[0];

        if (!IsConfigurationRecord(candidate))
        {

            return false;

        }

        elementType = candidate;

        return true;

    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];

}
