using System.Reflection;
using System.Reflection.Emit;

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

    private static readonly Lazy<IReadOnlyDictionary<short, OpCode>> OpCodesByValue =
        new(LoadOpCodesByValue);

    private static readonly Lazy<IReadOnlyList<Type>> EnvironmentMutatingTestClasses =
        new(LoadEnvironmentMutatingTestClasses);

    private static readonly Lazy<IReadOnlyList<Type>> ProcessGlobalSeamMutatingTestClasses =
        new(LoadProcessGlobalSeamMutatingTestClasses);

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

    /// <summary>
    /// The factory scan above only catches classes that hold an
    /// <see cref="ArcanumWebApplicationFactory"/>. Classes that call
    /// <c>Environment.SetEnvironmentVariable</c> themselves — directly, or through a helper scope
    /// such as <c>HostProcessToolsEscapeHatchScope</c> — mutate the very same process-global state
    /// and were entirely unguarded. This walks the IL of every method in the assembly so a new test
    /// class cannot reintroduce the race by forgetting <c>[Collection]</c>.
    /// </summary>
    [Fact]
    public void Every_test_class_that_mutates_the_process_environment_is_serialized()
    {

        IReadOnlyDictionary<string, bool> serialized = CollectionParallelism.Value;

        List<string> offenders = [];

        foreach (Type type in EnvironmentMutatingTestClasses.Value)
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
            "Environment.SetEnvironmentVariable mutates process-global state that other tests read "
            + "live, so every test class that reaches it must run in the "
            + $"'{ProcessEnvironmentCollectionName.Value}' collection (or another "
            + "DisableParallelization collection): "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// The same invariant for every other process-global seam.
    ///
    /// Environment variables were the only shared resource anything enforced, but they are not the
    /// only one. Production code exposes static <c>…ForTests</c> hooks that fake filesystem
    /// identity, file permissions, backup inventories, and session locking, and Serilog routes every
    /// log record through one static logger. A test that installs any of those is changing
    /// behaviour for every test running beside it, and a test that asserts over what it captured is
    /// measuring the whole suite. Both read as an unrelated test failing intermittently.
    /// </summary>
    [Fact]
    public void Every_test_class_that_mutates_a_process_global_seam_is_serialized()
    {

        IReadOnlyDictionary<string, bool> serialized = CollectionParallelism.Value;

        List<string> offenders = [];

        foreach (Type type in ProcessGlobalSeamMutatingTestClasses.Value)
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
            "Static test seams on production code and Serilog's static Log.Logger are read by every "
            + "test in the process, so every test class that assigns one must run in a "
            + "DisableParallelization collection: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// Guards the scanner above the same way the environment scanner is guarded: if the predicate
    /// stopped matching, the offender list would be empty and the contract would pass vacuously.
    /// </summary>
    [Fact]
    public void The_process_global_seam_scan_finds_the_known_seam_using_test_classes()
    {

        string[] found =
        [
            .. ProcessGlobalSeamMutatingTestClasses.Value
                .Select(static type => type.Name),
        ];

        Assert.NotEmpty(found);

        Assert.Contains("UploadedFileRepositoryTests", found);

        Assert.Contains("DataRetentionServiceTests", found);

    }

    /// <summary>
    /// The control above only pins the property-setter shape. Production exposes just as many
    /// method-shaped seams — <c>SessionAttachmentToolAmbient.SetUtcTicksNowForTests</c>,
    /// <c>WorkspacePathPolicy.SetSymlinkResolverForTests</c>,
    /// <c>OutboundUrlGuard.SetPinnedAddressRewriterForTests</c> — and a predicate that recognises
    /// only <c>set_</c> leaves every caller of those unguarded while still passing the control
    /// above. Pin the second shape separately so neither branch can rot unnoticed.
    /// </summary>
    [Fact]
    public void The_process_global_seam_scan_finds_the_method_shaped_seam_using_test_classes()
    {

        string[] found =
        [
            .. ProcessGlobalSeamMutatingTestClasses.Value
                .Select(static type => type.Name),
        ];

        Assert.Contains("SessionAttachmentToolAmbientTtlTests", found);

        Assert.Contains("WorkspacePathPolicySymlinkTests", found);

        Assert.Contains("OutboundUrlGuardEgressConnectTests", found);

    }

    /// <summary>
    /// Guards the scanner itself: if the IL walk stopped resolving
    /// <c>Environment.SetEnvironmentVariable</c> call sites it would report an empty offender list
    /// and pass vacuously forever.
    /// </summary>
    [Fact]
    public void The_environment_mutation_scan_finds_the_known_mutating_test_classes()
    {

        string[] found = EnvironmentMutatingTestClasses.Value
            .Select(static type => type.FullName ?? type.Name)
            .ToArray();

        Assert.Contains(
            typeof(RetroDownfall.Arcanum.Tests.Environment.ArcanumEnvironmentTests).FullName,
            found);

        Assert.Contains(
            typeof(RetroDownfall.Arcanum.Tests.Hosting.ArcanumLocalApiAddressTests).FullName,
            found);

        Assert.Contains(
            typeof(RetroDownfall.Arcanum.Tests.Api.ApiBootstrapperMetricsAuthTests).FullName,
            found);

        Assert.Contains(
            typeof(RetroDownfall.Arcanum.Tests.Security.HostProcessToolPolicyTests).FullName,
            found);

        // The process working directory is the same class of shared state as a variable, and every
        // relative path in the process resolves through it.
        Assert.Contains(
            typeof(RetroDownfall.Arcanum.Tests.Cli.FileBatchCommandTests).FullName,
            found);
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

    private static IReadOnlyDictionary<short, OpCode> LoadOpCodesByValue()
    {

        Dictionary<short, OpCode> lookup = [];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(
            BindingFlags.Public | BindingFlags.Static))
        {

            if (field.GetValue(null) is OpCode opCode)
            {

                lookup[opCode.Value] = opCode;
            }
        }

        return lookup;
    }

    /// <summary>
    /// A test class offends if its own IL calls <c>Environment.SetEnvironmentVariable</c>, or if it
    /// calls into a first-party helper that does (one closure pass covers scopes such as
    /// <c>HostProcessToolsEscapeHatchScope</c> and the web-application factory).
    /// </summary>
    private static IReadOnlyList<Type> LoadEnvironmentMutatingTestClasses() =>
        LoadMutatingTestClasses(
            IsEnvironmentMutation,
            // The web-application factory repoints HOME/USERPROFILE from native code paths the IL
            // walk cannot see through; treat it as mutating so its users are covered here too.
            typeof(ArcanumWebApplicationFactory));

    private static IReadOnlyList<Type> LoadProcessGlobalSeamMutatingTestClasses() =>
        LoadMutatingTestClasses(IsProcessGlobalSeamMutation);

    /// <summary>
    /// Walks every method in the assembly for a call matching <paramref name="mutates"/>, then
    /// closes over same-assembly callees so a class that reaches the mutation through a helper is
    /// caught too.
    /// </summary>
    private static IReadOnlyList<Type> LoadMutatingTestClasses(
        Func<MethodBase, bool> mutates,
        params Type[] seeds)
    {

        Dictionary<Type, HashSet<Type>> callees = [];

        HashSet<Type> mutating = [.. seeds];

        foreach (Type type in AssemblyTypes.Value)
        {

            HashSet<Type> referenced = [];

            bool found = false;

            foreach (MethodBase method in DeclaredMethods(type))
            {

                foreach (MethodBase called in CalledMethods(method))
                {

                    if (mutates(called))
                    {

                        found = true;

                        continue;
                    }

                    if (called.DeclaringType is { } declaring
                        && declaring.Assembly == type.Assembly)
                    {

                        _ = referenced.Add(declaring);
                    }
                }
            }

            callees[type] = referenced;

            if (found)
            {

                _ = mutating.Add(type);
            }
        }

        bool grew = true;

        while (grew)
        {

            grew = false;

            foreach ((Type type, HashSet<Type> referenced) in callees)
            {

                if (mutating.Contains(type) || !referenced.Any(mutating.Contains))
                {

                    continue;
                }

                _ = mutating.Add(type);

                grew = true;
            }
        }

        return mutating
            .Select(OutermostDeclaring)
            .Where(IsTestClass)
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<MethodBase> DeclaredMethods(
        Type type)
    {

        MethodBase[] methods;

        try
        {
            methods = [.. type.GetMethods(DeclaredMembers), .. type.GetConstructors(DeclaredMembers)];
        }
        catch (TypeLoadException)
        {
            yield break;
        }

        foreach (MethodBase method in methods)
        {

            yield return method;
        }
    }

    /// <summary>
    /// Walks a method body one opcode at a time — a naive byte scan for <c>call</c> would resolve
    /// operand bytes as tokens and produce false accusations. Bails out of the whole method on the
    /// first unknown opcode rather than guessing at an operand length.
    /// </summary>
    private static IEnumerable<MethodBase> CalledMethods(
        MethodBase method)
    {

        byte[]? il;

        Module module = method.Module;

        Type[]? typeArguments;

        Type[]? methodArguments;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();

            typeArguments = method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments()
                : null;

            methodArguments = method.IsGenericMethodDefinition
                ? method.GetGenericArguments()
                : null;
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            yield break;
        }

        if (il is null)
        {

            yield break;
        }

        IReadOnlyDictionary<short, OpCode> lookup = OpCodesByValue.Value;

        int offset = 0;

        while (offset < il.Length)
        {

            short code = il[offset];

            offset++;

            if (code == 0xFE)
            {

                if (offset >= il.Length)
                {

                    yield break;
                }

                code = unchecked((short)(0xFE00 | il[offset]));

                offset++;
            }

            if (!lookup.TryGetValue(code, out OpCode opCode))
            {

                yield break;
            }

            if (opCode.OperandType is OperandType.InlineMethod && offset + 4 <= il.Length)
            {

                MethodBase? called = ResolveMethod(
                    module,
                    BitConverter.ToInt32(il, offset),
                    typeArguments,
                    methodArguments);

                if (called is not null)
                {

                    yield return called;
                }
            }

            int operandSize = OperandSize(opCode, il, offset);

            if (operandSize < 0)
            {

                yield break;
            }

            offset += operandSize;
        }
    }

    private static int OperandSize(
        OpCode opCode,
        byte[] il,
        int offset) =>
        opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => offset + 4 <= il.Length
                ? 4 + (4 * BitConverter.ToInt32(il, offset))
                : -1,
            _ => -1,
        };

    private static MethodBase? ResolveMethod(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments)
    {

        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (Exception exception) when (exception is ArgumentException
            or BadImageFormatException
            or MissingMethodException
            or TypeLoadException
            or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Assignment to the process-global environment. <c>SetEnvironmentVariable</c> is the obvious
    /// one; the working directory is the same shared cell wearing a property, and every relative
    /// path resolved anywhere in the process reads it, so a test that repoints it is redirecting
    /// whatever else is running.
    /// </summary>
    private static bool IsEnvironmentMutation(
        MethodBase method) =>
        method.DeclaringType == typeof(global::System.Environment)
        && (string.Equals(
                method.Name,
                nameof(global::System.Environment.SetEnvironmentVariable),
                StringComparison.Ordinal)
            || string.Equals(
                method.Name,
                $"set_{nameof(global::System.Environment.CurrentDirectory)}",
                StringComparison.Ordinal));

    /// <summary>
    /// Assignment to a process-global seam that production code reads.
    ///
    /// Two shapes qualify. The first is a static <c>…ForTests</c>/<c>…ForTesting</c> seam on
    /// production code — the deliberate test hooks in <c>FileHandleIdentityInterop</c>,
    /// <c>SecureFileReader</c>, <c>SecureFilePermissions</c>, <c>SessionWriteLock</c>,
    /// <c>SessionEntryPersistence</c>, <c>BackupService</c>, and <c>BackupDatabaseSnapshotter</c>.
    /// Each is a single static field that every caller in the process observes, so a test that
    /// installs one is faking behaviour for every concurrently-running test as well as its own.
    /// Whether the seam is spelled as a property or as a <c>Set…ForTests(…)</c> method is a
    /// bookkeeping detail of the type that owns it — <c>SessionAttachmentToolAmbient</c>,
    /// <c>OutboundUrlGuard</c>, and <c>WorkspacePathPolicy</c> use the method form for exactly the
    /// same one static field — so both spellings have to count, or a whole class of seam slips
    /// through while the scan still looks healthy. The second shape is Serilog's static
    /// <c>Log.Logger</c>, which routes every log record in the process, so a test that swaps it and
    /// then asserts over what its sink captured is measuring the whole suite rather than itself.
    /// </summary>
    private static bool IsProcessGlobalSeamMutation(MethodBase method)
    {

        if (method.DeclaringType is not { } declaring)
        {

            return false;

        }

        // Production assemblies only: a test helper's own static seam is not shared with the code
        // under test, and the tests' own assembly is where the callers legitimately live.
        if (declaring.Assembly == typeof(EnvironmentIsolationContractTests).Assembly)
        {

            return false;

        }

        if (string.Equals(declaring.FullName, "Serilog.Log", StringComparison.Ordinal)
            && string.Equals(method.Name, "set_Logger", StringComparison.Ordinal))
        {

            return true;

        }

        if (!method.IsStatic
            || (!method.Name.EndsWith("ForTests", StringComparison.Ordinal)
                && !method.Name.EndsWith("ForTesting", StringComparison.Ordinal)))
        {

            return false;

        }

        // Only the assigning shape: `…ForTests` also names pure read-only helpers that expose an
        // internal calculation to a test, and those mutate nothing.
        return method.Name.StartsWith("set_", StringComparison.Ordinal)
            || method.Name.StartsWith("Set", StringComparison.Ordinal);

    }

    private static bool IsTestClass(
        Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(static method => method.GetCustomAttributesData().Any(static attribute =>
                attribute.AttributeType == typeof(FactAttribute)
                || attribute.AttributeType == typeof(TheoryAttribute)
                || attribute.AttributeType.IsSubclassOf(typeof(FactAttribute))));

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
