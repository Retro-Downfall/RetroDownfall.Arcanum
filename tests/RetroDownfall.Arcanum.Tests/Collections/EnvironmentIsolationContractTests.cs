using System.Reflection;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Collections;

internal static class ProcessEnvironmentCollectionName
{

    internal const string Value = "ProcessEnvironment";
}

/// <summary>
/// <see cref="ArcanumWebApplicationFactory"/> repoints process-global HOME/USERPROFILE at its own
/// temporary profile and deletes that tree on disposal, so it is only safe while nothing else is
/// reading those paths. xUnit v2 runs every <c>DisableParallelization</c> collection on its own —
/// serially, and never alongside another collection — which is the serialization
/// <see cref="ProcessEnvironmentCollection"/> already documents and depends on. Nothing enforced
/// that wiring, so a new factory-using class placed in a parallel collection would silently
/// reintroduce the race. These tests hold the invariant in place.
/// </summary>
/// <remarks>
/// Scanning the assembly forces every type in it to load and resolve, which is a large enough
/// burst of work to disturb the millisecond-scale timing assertions that run during the parallel
/// phase. Running inside the serialized collection this contract guards keeps that cost away from
/// them; it mutates no environment state itself.
/// </remarks>
[Collection(ProcessEnvironmentCollectionName.Value)]
public sealed class EnvironmentIsolationContractTests
{

    private const BindingFlags DeclaredMembers =
        BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    // Scanning every type in the assembly is the expensive part, and both tests need it.
    private static readonly Lazy<IReadOnlyList<Type>> AssemblyTypes = new(LoadAssemblyTypes);

    private static readonly Lazy<IReadOnlyDictionary<string, bool>> CollectionParallelism =
        new(LoadCollectionParallelism);

    private static readonly Lazy<IReadOnlyList<Type>> FactoryDependents =
        new(LoadFactoryDependents);

    /// <summary>
    /// Covers constructor-injected fixtures, plain fields, and — because the compiler lifts async
    /// locals and captured variables into nested state-machine and closure types — classes that
    /// construct a factory inline inside a test method.
    /// </summary>
    [Fact]
    public void Every_test_class_touching_the_web_application_factory_is_serialized()
    {

        IReadOnlyDictionary<string, bool> serialized = CollectionParallelism.Value;

        List<string> offenders = [];

        foreach (Type type in FactoryDependents.Value)
        {

            string? collection = AttributeName<CollectionAttribute>(type);

            if (collection is null)
            {

                offenders.Add($"{type.FullName} declares no [Collection]");

                continue;
            }

            if (!serialized.TryGetValue(collection, out bool disablesParallelization)
                || !disablesParallelization)
            {

                offenders.Add(
                    $"{type.FullName} is in collection '{collection}', which is not "
                    + "DisableParallelization");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "ArcanumWebApplicationFactory mutates process-global HOME/USERPROFILE, so every test "
            + "class that touches one must run in a DisableParallelization collection: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void Collections_that_mutate_the_process_environment_disable_parallelization()
    {

        IReadOnlyDictionary<string, bool> serialized = CollectionParallelism.Value;

        List<string> required = [ProcessEnvironmentCollectionName.Value];

        required.AddRange(AssemblyTypes.Value
            .Where(HostsFactoryFixture)
            .Select(AttributeName<CollectionDefinitionAttribute>)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Select(static name => name!));

        Assert.NotEmpty(required);

        foreach (string collection in required.Distinct(StringComparer.Ordinal))
        {

            Assert.True(
                serialized.TryGetValue(collection, out bool disablesParallelization)
                    && disablesParallelization,
                $"Collection '{collection}' mutates or depends on process-global environment "
                + "state and must keep DisableParallelization = true.");
        }
    }

    private static IReadOnlyList<Type> LoadAssemblyTypes()
    {

        try
        {
            return typeof(ArcanumWebApplicationFactory).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    private static IReadOnlyDictionary<string, bool> LoadCollectionParallelism()
    {

        Dictionary<string, bool> collections = new(StringComparer.Ordinal);

        foreach (Type type in AssemblyTypes.Value)
        {

            CollectionDefinitionAttribute? definition =
                type.GetCustomAttribute<CollectionDefinitionAttribute>();

            if (definition is null
                || AttributeName<CollectionDefinitionAttribute>(type) is not { } name)
            {

                continue;
            }

            collections[name] = definition.DisableParallelization;
        }

        return collections;
    }

    private static IReadOnlyList<Type> LoadFactoryDependents() =>
        AssemblyTypes.Value
            .Where(ReferencesFactory)
            .Select(OutermostDeclaring)
            // The factory's own lambdas capture it; the fixture itself is not a test class.
            .Where(static type => type != typeof(ArcanumWebApplicationFactory))
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// xUnit 2.x takes the collection name as a constructor argument and exposes no property for
    /// it, so read it back from the attribute metadata.
    /// </summary>
    private static string? AttributeName<TAttribute>(
        Type type)
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

    private static bool ReferencesFactory(
        Type type)
    {

        if (type == typeof(ArcanumWebApplicationFactory))
        {

            return false;
        }

        return type.GetConstructors(DeclaredMembers)
                .Any(static constructor => constructor.GetParameters()
                    .Any(static parameter =>
                        parameter.ParameterType == typeof(ArcanumWebApplicationFactory)))
            || type.GetFields(DeclaredMembers)
                .Any(static field => field.FieldType == typeof(ArcanumWebApplicationFactory))
            || type.GetProperties(DeclaredMembers)
                .Any(static property =>
                    property.PropertyType == typeof(ArcanumWebApplicationFactory));
    }

    private static bool HostsFactoryFixture(
        Type type) =>
        type.GetCustomAttribute<CollectionDefinitionAttribute>() is not null
        && type.GetInterfaces().Any(static contract =>
            contract.IsGenericType
            && contract.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)
            && contract.GetGenericArguments()[0] == typeof(ArcanumWebApplicationFactory));

    private static Type OutermostDeclaring(
        Type type)
    {

        Type outermost = type;

        while (outermost.DeclaringType is { } declaring)
        {

            outermost = declaring;
        }

        return outermost;
    }
}
