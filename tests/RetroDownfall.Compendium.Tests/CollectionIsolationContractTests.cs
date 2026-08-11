using System.Reflection;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests;

/// <summary>
/// xUnit resolves <c>[CollectionDefinition]</c> per test assembly, and
/// <c>DisableParallelization</c> is carried only on the definition. A <c>[Collection("…")]</c> whose
/// name has no definition in this assembly therefore silently lands in a parallelizable collection:
/// the attribute reads as an isolation guard while enforcing nothing. That is exactly how
/// <c>ServiceCollectionConfiguratorTests</c> came to name <c>ProcessEnvironment</c>, a collection
/// defined only in the sibling <c>RetroDownfall.Arcanum.Tests</c> assembly. These tests keep the
/// wiring honest.
/// </summary>
public sealed class CollectionIsolationContractTests
{

    [Fact]

    public void Every_collection_attribute_names_a_serialized_definition_in_this_assembly()
    {

        IReadOnlyDictionary<string, bool> definitions = CollectionDefinitions();

        List<string> offenders = [];

        int attributed = 0;

        foreach (Type type in AssemblyTypes())
        {

            if (AttributeName<CollectionAttribute>(type) is not { } collection)
            {

                continue;

            }

            attributed++;

            if (!definitions.TryGetValue(collection, out bool disablesParallelization))
            {

                offenders.Add(
                    $"{type.FullName} is in collection '{collection}', which has no "
                    + "[CollectionDefinition] in this assembly");

                continue;

            }

            if (!disablesParallelization)
            {

                offenders.Add(
                    $"{type.FullName} is in collection '{collection}', which is not "
                    + "DisableParallelization");

            }

        }

        Assert.True(attributed > 0, "The scan found no [Collection]-attributed test classes.");

        Assert.True(
            offenders.Count == 0,
            "Collection definitions are assembly-scoped, so a [Collection] naming a definition that "
            + "lives elsewhere serializes nothing: "
            + string.Join("; ", offenders));

    }

    private static IReadOnlyDictionary<string, bool> CollectionDefinitions()
    {

        Dictionary<string, bool> definitions = new(StringComparer.Ordinal);

        foreach (Type type in AssemblyTypes())
        {

            if (type.GetCustomAttribute<CollectionDefinitionAttribute>() is not { } definition
                || AttributeName<CollectionDefinitionAttribute>(type) is not { } name)
            {

                continue;

            }

            definitions[name] = definition.DisableParallelization;

        }

        return definitions;

    }

    private static IReadOnlyList<Type> AssemblyTypes()
    {

        try
        {

            return typeof(CollectionIsolationContractTests).Assembly.GetTypes();

        }
        catch (ReflectionTypeLoadException exception)
        {

            return [.. exception.Types.OfType<Type>()];

        }

    }

    /// <summary>
    /// xUnit 2.x takes the collection name as a constructor argument and exposes no property for
    /// it, so read it back from the attribute metadata.
    /// </summary>
    private static string? AttributeName<TAttribute>(Type type)
        where TAttribute : Attribute
    {

        CustomAttributeData? data = type
            .GetCustomAttributesData()
            .FirstOrDefault(static item => item.AttributeType == typeof(TAttribute));

        if (data is null || data.ConstructorArguments.Count == 0)
        {

            return null;

        }

        return data.ConstructorArguments[0].Value as string;

    }

}
