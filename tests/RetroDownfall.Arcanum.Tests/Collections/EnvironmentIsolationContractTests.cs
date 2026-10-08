using System.Reflection;
using System.Reflection.Emit;

using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

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

    private const string TestHomeVariable = "ARCANUM_TEST_HOME";

    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    private const string TestingEnvironmentName = "Testing";

    private static readonly Lazy<IReadOnlyList<Type>> GlobalConsoleMutatingTestClasses =
        new(LoadGlobalConsoleMutatingTestClasses);

    private static readonly Lazy<IReadOnlyList<Type>> WorkspacePathPolicyUsingTestClasses =
        new(LoadWorkspacePathPolicyUsingTestClasses);

    private static readonly Lazy<IReadOnlyList<TestHomeUse>> TestHomeUses = new(LoadTestHomeUses);

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
    /// <c>Console.SetOut</c>/<c>SetError</c>/<c>SetIn</c> and Spectre's
    /// <c>AnsiConsole.Console</c> are process-wide, so a class that swaps one while a parallel class
    /// writes the console loses or steals output. The CLI harness swaps all of them, so the scan
    /// closes over it the same way the environment scan closes over its helpers. The rule is
    /// serialization, not one named collection: <c>GlobalConsole</c> is the home for CLI command
    /// tests, but any <c>DisableParallelization</c> collection (the <c>ProcessEnvironment</c> one
    /// that <c>CliErrorOutputTests</c> and <c>StaticSerilogBridgeTests</c> already share) cannot
    /// race a console writer either, because those collections never overlap one another.
    /// </summary>
    [Fact]
    public void Every_test_class_that_swaps_the_global_console_runs_in_a_serialized_collection()
    {
        IReadOnlyDictionary<string, bool> serialized = CollectionParallelism.Value;

        List<string> offenders = [];

        foreach (Type type in GlobalConsoleMutatingTestClasses.Value)
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
            "Console.SetOut/SetError/SetIn and AnsiConsole.Console are process-global, so every test "
            + "class that assigns one, directly or through the CLI test harness, must run in a "
            + "DisableParallelization collection ('GlobalConsole' for CLI command tests): "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// <c>WorkspacePathPolicy</c> keeps process-global test seams (ordinal-ignore-case comparison, the
    /// containment observer) that its covering tests install and reset. A test that merely uses the policy
    /// or <c>PhysicalFileSystemWriter</c> (or its resolver) is exactly the neighbour such a seam leaks into, so every class
    /// that reaches either, directly or through a helper, joins the one serialized
    /// <c>WorkspacePathPolicy</c> collection instead of relying on each author to notice.
    /// </summary>
    [Fact]
    public void Every_test_class_that_uses_WorkspacePathPolicy_or_PhysicalFileSystemWriter_is_in_the_WorkspacePathPolicy_collection()
    {
        List<string> offenders = [];

        foreach (Type type in WorkspacePathPolicyUsingTestClasses.Value)
        {
            string? collection = AttributeName<CollectionAttribute>(type);

            if (!string.Equals(collection, WorkspacePathPolicyCollectionName, StringComparison.Ordinal))
            {
                offenders.Add($"{type.FullName} is in {(collection is null ? "no collection" : $"collection '{collection}'")}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Every test class that uses WorkspacePathPolicy or PhysicalFileSystemWriter must declare [Collection(\"{WorkspacePathPolicyCollectionName}\")]: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// Guards the scan above: an IL walk that stopped matching the policy and writer call sites would
    /// report no offenders and pass vacuously forever.
    /// </summary>
    [Fact]
    public void The_workspace_path_policy_scan_finds_direct_and_writer_using_test_classes()
    {
        string[] found =
        [
            .. WorkspacePathPolicyUsingTestClasses.Value
                .Select(static type => type.Name),
        ];

        Assert.Contains("WorkspacePathPolicySymlinkTests", found);

        Assert.Contains("PhysicalFileSystemWriterTests", found);

        Assert.Contains("WorkspacePathResolverTests", found);
    }

    /// <summary>
    /// Guards the console scan: an IL walk that stopped matching the swap call sites would report
    /// no offenders and pass vacuously forever.
    /// </summary>
    [Fact]
    public void The_global_console_scan_finds_direct_and_harness_driven_console_swappers()
    {
        string[] found =
        [
            .. GlobalConsoleMutatingTestClasses.Value
                .Select(static type => type.Name),
        ];

        // Direct AnsiConsole.Console assignment, and Console.SetOut/SetIn.
        Assert.Contains("MemoryStatusCovenantCommandTests", found);

        Assert.Contains("RunInputReaderTests", found);

        // Reaches the swap only through CliTestHarness.
        Assert.Contains("WorkspaceCommandTests", found);
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
    /// <c>WorkspacePathPolicy.SetUseOrdinalIgnoreCasePathComparisonForTests</c>,
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

    /// <summary>
    /// <c>ArcanumPaths</c> honours <c>ARCANUM_TEST_HOME</c> only while a host environment reads
    /// <c>Testing</c>; outside it the variable is deliberately ignored and every path resolves to
    /// the developer's real profile directory. A test class that sets the variable alone therefore
    /// writes the real profile while looking isolated. The class must either go through the shared
    /// <see cref="ArcanumTestHomeScope"/>, which sets both, or set a Testing environment itself.
    /// </summary>
    [Fact]
    public void Every_test_class_that_sets_ARCANUM_TEST_HOME_also_sets_a_Testing_environment()
    {
        List<string> offenders =
        [
            .. TestHomeUses.Value
                .Where(static use => use.SetsTestHome && !use.EstablishesTestingEnvironment)
                .Select(static use => use.TestClass.FullName ?? use.TestClass.Name),
        ];

        Assert.True(
            offenders.Count == 0,
            "ARCANUM_TEST_HOME is ignored unless DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT is "
            + "'Testing', so these classes write the developer's real profile directory. Use "
            + $"{nameof(ArcanumTestHomeScope)} or set a Testing environment in the same class: "
            + string.Join("; ", offenders));
    }

    /// <summary>
    /// Guards the scan above: an IL walk that stopped resolving <c>ldstr</c> operands would report
    /// no offenders and pass vacuously forever.
    /// </summary>
    [Fact]
    public void The_test_home_scan_finds_classes_that_set_the_variable_and_classes_using_the_scope()
    {
        TestHomeUse writer = Assert.Single(
            TestHomeUses.Value,
            static use => use.TestClass
                == typeof(RetroDownfall.Arcanum.Tests.Configuration.ConfigurationWriterTests));

        Assert.True(writer.SetsTestHome);

        Assert.True(writer.EstablishesTestingEnvironment);

        Assert.Contains(
            TestHomeUses.Value,
            static use => use.UsesSharedScope);
    }

    /// <summary>
    /// A class that names <c>DOTNET_ENVIRONMENT</c>/<c>ASPNETCORE_ENVIRONMENT</c> only to read it,
    /// to clear it, or to give it another value, or that mentions <c>Testing</c> for some other
    /// variable, does not make <c>ArcanumPaths</c> honour <c>ARCANUM_TEST_HOME</c>, so a literal of
    /// either kind alone must not satisfy the Testing rule above.
    /// </summary>
    [Theory]
    [InlineData(typeof(HostEnvironmentReader))]
    [InlineData(typeof(HostEnvironmentClearer))]
    [InlineData(typeof(DevelopmentHostEnvironmentWriter))]
    [InlineData(typeof(TestingForAnotherVariable))]
    public void The_Testing_scan_ignores_classes_that_only_read_clear_or_reassign_the_host_environment(
        Type fixture) =>
        Assert.False(
            ReadHostEnvironmentLiterals(fixture).SetsTesting,
            $"{fixture.Name} does not set a Testing host environment");

    /// <summary>
    /// Guards the scan above from rejecting everything. The split shape is the one real fixtures
    /// use when a helper takes the value as a parameter (<c>ArcanumPathsTestingOverrideTests</c>,
    /// <c>LoggingBootstrapperTests</c>): the variable name and <c>Testing</c> meet only at class
    /// level, so a per-method pairing would wrongly condemn them.
    /// </summary>
    [Theory]
    [InlineData(typeof(TestingHostEnvironmentWriter))]
    [InlineData(typeof(SplitTestingHostEnvironmentWriter))]
    public void The_Testing_scan_accepts_a_class_that_names_the_host_environment_and_Testing(
        Type fixture) =>
        Assert.True(
            ReadHostEnvironmentLiterals(fixture).SetsTesting,
            $"{fixture.Name} sets a Testing host environment");

    /// <summary>
    /// The archived-source packaging test performs a real Native AOT publish. Its five-minute
    /// deadline detects an actual hung toolchain only when the publish is not competing with the
    /// full parallel test suite for the runner's CPU and memory.
    /// </summary>
    [Fact]
    public void Native_AOT_packaging_test_class_is_serialized()
    {
        Type packagingTests =
            typeof(RetroDownfall.Arcanum.Tests.Packaging.GrimoireAdmissionBenchmarkPackagingTests);

        string? collection = AttributeName<CollectionAttribute>(packagingTests);

        Assert.True(
            collection is not null
                && CollectionParallelism.Value.TryGetValue(
                    collection,
                    out bool disablesParallelization)
                && disablesParallelization,
            $"{packagingTests.FullName} performs a real Native AOT publish and must run in a "
            + "DisableParallelization collection so its liveness deadline measures the toolchain, "
            + "not full-suite resource contention.");
    }

    /// <summary>
    /// Bootstrapper tests exercise durable hosted-service checkpoints whose production deadlines
    /// must not be consumed by unrelated full-suite CPU pressure.
    /// </summary>
    [Fact]
    public void Grimoire_database_bootstrapper_test_class_is_serialized()
    {
        Type bootstrapperTests =
            typeof(RetroDownfall.Arcanum.Tests.Hosting.GrimoireDatabaseBootstrapperTests);

        string collection = Assert.IsType<string>(
            AttributeName<CollectionAttribute>(bootstrapperTests));

        Assert.Equal(HostedServiceLifetimeCollection.Name, collection);

        Assert.True(
            CollectionParallelism.Value.TryGetValue(
                collection,
                out bool disablesParallelization)
            && disablesParallelization,
            $"{bootstrapperTests.FullName} exercises bounded durable hosted-service checkpoints "
            + "and must run in a DisableParallelization collection so their liveness deadlines "
            + "measure the host lifecycle, not full-suite resource contention.");
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

    // IL-only fixtures for the Testing-scan control tests. Nothing calls them; they sit inside this
    // class so every other scan attributes them to it, and it already runs serialized.
    internal static class HostEnvironmentRecorder
    {
        internal static void Record(
            string name,
            string? value)
        {
            _ = name;

            _ = value;
        }
    }

    internal static class HostEnvironmentReader
    {
        internal static string? Read() =>
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
    }

    internal static class HostEnvironmentClearer
    {
        internal static void Clear() =>
            HostEnvironmentRecorder.Record("ASPNETCORE_ENVIRONMENT", null);
    }

    internal static class DevelopmentHostEnvironmentWriter
    {
        internal static void Write() =>
            HostEnvironmentRecorder.Record("DOTNET_ENVIRONMENT", "Development");
    }

    internal static class TestingForAnotherVariable
    {
        internal static void Write() =>
            HostEnvironmentRecorder.Record("SOME_OTHER_ENVIRONMENT", "Testing");
    }

    internal static class TestingHostEnvironmentWriter
    {
        internal static void Write() =>
            HostEnvironmentRecorder.Record("ASPNETCORE_ENVIRONMENT", "Testing");
    }

    internal static class SplitTestingHostEnvironmentWriter
    {
        internal static void Write() =>
            Assign("Testing");

        private static void Assign(
            string value) =>
            HostEnvironmentRecorder.Record("DOTNET_ENVIRONMENT", value);
    }

    /// <summary>
    /// One outermost test class's relationship with the test-home variables, read from the string
    /// literals and call sites of its own IL and that of its nested closure and state-machine types.
    /// </summary>
    private sealed record TestHomeUse(
        Type TestClass,
        bool SetsTestHome,
        bool UsesSharedScope,
        bool EstablishesTestingEnvironment);

    private static IReadOnlyList<TestHomeUse> LoadTestHomeUses()
    {
        Dictionary<Type, (bool Home, HostEnvironmentLiterals Literals, bool Scope)> byClass = [];

        foreach (Type type in AssemblyTypes.Value)
        {
            Type outermost = OutermostDeclaring(type);

            if (outermost == typeof(EnvironmentIsolationContractTests)
                || outermost == typeof(ArcanumTestHomeScope)
                || !IsTestClass(outermost))
            {
                continue;
            }

            (bool home, HostEnvironmentLiterals literals, bool scope) = byClass.GetValueOrDefault(outermost);

            foreach (MethodBase method in DeclaredMethods(type))
            {
                home |= LoadedStrings(method)
                    .Any(static literal => string.Equals(
                        literal,
                        TestHomeVariable,
                        StringComparison.Ordinal));

                scope |= CalledMethods(method)
                    .Any(static called => called.DeclaringType == typeof(ArcanumTestHomeScope));
            }

            literals = literals.Merge(ReadHostEnvironmentLiterals(type));

            byClass[outermost] = (home, literals, scope);
        }

        return
        [
            .. byClass
                .Where(static entry => entry.Value.Home || entry.Value.Scope)
                .OrderBy(static entry => entry.Key.FullName, StringComparer.Ordinal)
                .Select(entry => new TestHomeUse(
                    entry.Key,
                    // A literal alone is not an assignment: a class can name the variable to strip
                    // it from a child process environment or to print it from a shell fixture. It
                    // only mutates this process when its IL also reaches an environment write.
                    entry.Value.Home && EnvironmentMutatingTestClasses.Value.Contains(entry.Key),
                    entry.Value.Scope,
                    // The web-application factory sets Testing itself before any test runs.
                    entry.Value.Scope
                        || entry.Value.Literals.SetsTesting
                        || FactoryDependents.Value.Contains(entry.Key))),
        ];
    }

    /// <summary>
    /// What one type's string literals say about the host environment: whether it names
    /// <c>DOTNET_ENVIRONMENT</c>/<c>ASPNETCORE_ENVIRONMENT</c>, and whether it names
    /// <c>Testing</c>. A class sets a Testing host environment only when both appear, so merely
    /// naming the variable (to read it, clear it, or give it another value) does not count. Real
    /// fixtures pass the value through helper parameters, so the two literals are paired across the
    /// whole class, nested closure and state-machine types included, rather than within one method.
    /// This is literal pairing, not data-flow analysis: a class that names Testing for an unrelated
    /// reason and also touches the variable passes, which the rule accepts rather than fail the
    /// parameterised helpers.
    /// </summary>
    private readonly record struct HostEnvironmentLiterals(
        bool Variable,
        bool Testing)
    {
        internal bool SetsTesting => Variable && Testing;

        internal HostEnvironmentLiterals Merge(
            HostEnvironmentLiterals other) =>
            new(Variable || other.Variable, Testing || other.Testing);
    }

    private static HostEnvironmentLiterals ReadHostEnvironmentLiterals(
        Type type)
    {
        bool variable = false;

        bool testing = false;

        foreach (string literal in DeclaredMethods(type).SelectMany(LoadedStrings))
        {
            variable |=
                string.Equals(literal, DotnetEnvironmentVariable, StringComparison.Ordinal)
                || string.Equals(literal, AspNetCoreEnvironmentVariable, StringComparison.Ordinal);

            testing |= string.Equals(literal, TestingEnvironmentName, StringComparison.Ordinal);
        }

        return new HostEnvironmentLiterals(variable, testing);
    }

    private static IReadOnlyList<Type> LoadProcessGlobalSeamMutatingTestClasses() =>
        LoadMutatingTestClasses(IsProcessGlobalSeamMutation);

    private static IReadOnlyList<Type> LoadGlobalConsoleMutatingTestClasses() =>
        LoadMutatingTestClasses(IsGlobalConsoleMutation);

    private const string WorkspacePathPolicyCollectionName = "WorkspacePathPolicy";

    private static IReadOnlyList<Type> LoadWorkspacePathPolicyUsingTestClasses() =>
        LoadMutatingTestClasses(IsWorkspacePathPolicyUse);

    /// <summary>
    /// Any call into <c>WorkspacePathPolicy</c>, <c>PhysicalFileSystemWriter</c> or the
    /// <c>WorkspacePathResolver</c> that routes every API path through the policy, including constructors.
    /// </summary>
    private static bool IsWorkspacePathPolicyUse(
        MethodBase method) =>
        method.DeclaringType == typeof(RetroDownfall.Arcanum.Infrastructure.Security.WorkspacePathPolicy)
        || method.DeclaringType == typeof(RetroDownfall.Arcanum.Infrastructure.Workspaces.PhysicalFileSystemWriter)
        || method.DeclaringType == typeof(RetroDownfall.Arcanum.Infrastructure.Workspaces.WorkspacePathResolver);

    /// <summary>
    /// Assignment to the process-wide console: the three <see cref="Console"/> stream swaps and the
    /// Spectre <c>AnsiConsole.Console</c> property every command renders through.
    /// </summary>
    private static bool IsGlobalConsoleMutation(
        MethodBase method) =>
        (method.DeclaringType == typeof(Console)
            && method.Name is nameof(Console.SetOut) or nameof(Console.SetError) or nameof(Console.SetIn))
        || (string.Equals(method.DeclaringType?.FullName, "Spectre.Console.AnsiConsole", StringComparison.Ordinal)
            && string.Equals(method.Name, "set_Console", StringComparison.Ordinal));

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
        Module module = method.Module;

        Type[]? typeArguments = null;

        Type[]? methodArguments = null;

        try
        {
            typeArguments = method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments()
                : null;

            methodArguments = method.IsGenericMethodDefinition
                ? method.GetGenericArguments()
                : null;
        }
        catch (Exception exception) when (exception is NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            yield break;
        }

        foreach ((OpCode opCode, int operand) in Instructions(method))
        {
            if (opCode.OperandType is not OperandType.InlineMethod)
            {
                continue;
            }

            MethodBase? called = ResolveMethod(module, operand, typeArguments, methodArguments);

            if (called is not null)
            {
                yield return called;
            }
        }
    }

    /// <summary>
    /// Every string literal a method loads. The C# compiler inlines <c>const string</c> values as
    /// <c>ldstr</c>, so a name spelled once as a constant and used in another method still shows up
    /// in the declaring class.
    /// </summary>
    private static IEnumerable<string> LoadedStrings(
        MethodBase method)
    {
        Module module = method.Module;

        foreach ((OpCode opCode, int operand) in Instructions(method))
        {
            if (opCode.OperandType is not OperandType.InlineString)
            {
                continue;
            }

            string? literal;

            try
            {
                literal = module.ResolveString(operand);
            }
            catch (Exception exception) when (exception is ArgumentException
                or BadImageFormatException)
            {
                continue;
            }

            yield return literal;
        }
    }

    /// <summary>
    /// The opcode stream of a method body, with the 32-bit operand of every token-carrying
    /// instruction. Shared by the call-site and string-literal walks so neither re-implements the
    /// operand-length bookkeeping.
    /// </summary>
    private static IEnumerable<(OpCode OpCode, int Operand)> Instructions(
        MethodBase method)
    {
        byte[]? il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
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

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineString
                && offset + 4 <= il.Length)
            {
                yield return (opCode, BitConverter.ToInt32(il, offset));
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
            // Helpers can hold the factory, but xUnit collection attributes only serialize tests.
            // The transitive environment-mutation scan above closes through those helpers and
            // verifies every test class that calls them.
            .Where(IsTestClass)
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
