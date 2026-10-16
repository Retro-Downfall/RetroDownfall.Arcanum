using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The Limit Pins. docs/Arcanum.DESIGN.md states what the hosted-producer inventory does not follow: authored code that the framework or
/// the runtime runs at a point no operation of the census names. A limit is a claim about the shipped source only while something proves the
/// shipped source holds no instance of the shape the checker cannot see, so each limit has a pin here. A pin runs a semantic scan over the
/// production compilations and compares the findings with an exact reviewed list: empty where the product holds no instance, and where it
/// holds one, the instance with the reason it is outside the claim. A scan that found nothing in a source built to hold the shape would pass
/// for ever, so every scan also runs on a synthetic source that holds every shape it names.
/// </summary>
/// <remarks>
/// The register at the top ties each limit to its DESIGN sentence and to the pin that holds it; the first test refuses a limit without a pin
/// and a DESIGN that no longer states it.
/// </remarks>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
[Trait("Category", "HostedProducerBuildAnalysis")]
public sealed class CheckerLimitPinTests(ITestOutputHelper output) : IClassFixture<SemanticModelScope>
{
    /// <summary>One stated limit: the id, the words of docs/Arcanum.DESIGN.md that state it, and the test that pins the production source against it.</summary>
    private sealed record Limit(string Id, string Anchor, string Pin);

    private const string P = "RetroDownfall.Arcanum.";

    private static readonly Limit[] Register =
    [
        new("ef-model-code", "Value converters, value generators and comparers a model names", nameof(Production_holds_no_authored_ef_value_converter_generator_comparer_customizer_or_convention)),
        new("root-provider-context", "A context the container disposes when the host's root provider is disposed", nameof(The_only_context_that_outlives_a_scope_is_the_one_pooled_registration)),
        new("unknown-disposable", "a disposable of unknown provenance is not assumed to be a context", nameof(No_authored_context_is_converted_to_a_type_that_is_not_a_context)),
        new("provider-state-change", "a state change the provider makes by itself is not an operation of this lane", nameof(The_one_connection_state_subscription_is_pinned_by_the_inventory_fixtures)),
        new("framework-virtual-override", "a virtual member that the framework declares", nameof(Authored_overrides_of_framework_virtual_members_that_reach_a_site_are_exactly_the_reviewed_types)),
        new("serializer-logger-callbacks", "what a serializer or a logger calls on the value besides those slots", nameof(Production_has_no_serializer_callback_destructuring_template_or_converter_that_reaches_a_site)),
        new("serializer-property-reads", "a serializer reading a property", nameof(No_type_a_serializer_can_read_has_a_getter_that_reaches_a_site)),
        new("scheduling-context", "the scheduling context an awaiter captures is not followed", nameof(Production_installs_no_scheduling_context_and_declares_no_awaiter)),
        new("root-provider-disposal", "A disposable the container owns is disposed with the root provider", nameof(Authored_disposals_that_reach_a_site_and_belong_to_the_container_are_exactly_the_reviewed_types)),
        new("runtime-observers", "arbitrary third-party runtime observers are unproved limits", nameof(Runtime_observers_are_exactly_the_reviewed_registrations)),
        new("carrier-hoisting", "A compiler-generated state machine hoists the locals", nameof(No_iterator_async_void_or_non_task_async_member_declares_an_authority_handle)),
        new("carrier-reflection", "says what a reflective access does", nameof(Reflective_invocations_are_exactly_the_reviewed_ones_and_carry_no_authority_handle)),
        new("constant-guard", "any other enumeration of a member's value-binding sources that does not skip a constant", "EveryEnumerationOfAMembersValueBindingsIsGuardedAgainstAConstant"),
    ];

    private static IReadOnlyList<Compilation> Production() => [.. HostedGrimoireProducerInventory.ProductionCompilations];

    private static string[] Production(Func<IReadOnlyList<Compilation>, string[]> scan) => scan(Production());

    [Fact]
    public void Every_stated_limit_has_a_pin_and_the_design_still_states_it()
    {
        string design = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.DESIGN.md"));

        string tests = string.Concat(Directory.EnumerateFiles(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains("/obj/", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        foreach (Limit limit in Register)
        {
            Assert.True(design.Contains(limit.Anchor, StringComparison.Ordinal), $"The design no longer states the limit '{limit.Id}': '{limit.Anchor}'.");

            Assert.True(tests.Contains(" void " + limit.Pin + "(", StringComparison.Ordinal) || tests.Contains(" Task " + limit.Pin + "(", StringComparison.Ordinal), $"The limit '{limit.Id}' has no pin named '{limit.Pin}'.");
        }

        Assert.Equal(Register.Length, Register.Select(static limit => limit.Id).Distinct(StringComparer.Ordinal).Count());
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // The synthetic source: every shape the scans name, once, plus the control that each scan must leave alone.
    // ---------------------------------------------------------------------------------------------------------------------------------

    private const string Shapes = """
        #nullable disable
        #pragma warning disable
        using System;
        using System.Collections.Generic;
        using System.Diagnostics.Metrics;
        using System.Diagnostics.Tracing;
        using System.IO;
        using System.Linq.Expressions;
        using System.Reflection;
        using System.Runtime.CompilerServices;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.ChangeTracking;
        using Microsoft.EntityFrameworkCore.Infrastructure;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;
        using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
        using Microsoft.EntityFrameworkCore.ValueGeneration;
        using Microsoft.Extensions.DependencyInjection;
        using Serilog;

        namespace RetroDownfall.Arcanum.Infrastructure.Data
        {
            public interface IGrimoireWorkLease : IAsyncDisposable { }

            public interface IGrimoireExternalEffectGroup : IAsyncDisposable { }
        }

        namespace Fixture
        {
            using RetroDownfall.Arcanum.Infrastructure.Data;

            public enum Kind { A }

            public sealed class Row { public int Id { get; set; } public Kind Kind { get; set; } }

            // EF model code.
            public sealed class AuthoredConverter : ValueConverter<int, string> { public AuthoredConverter() : base(v => v.ToString(), s => int.Parse(s)) { } }

            public sealed class AuthoredGenerator : ValueGenerator<int> { public override int Next(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) => 1; public override bool GeneratesTemporaryValues => false; }

            public sealed class AuthoredComparer : ValueComparer<int> { public AuthoredComparer() : base((a, b) => a == b, v => v) { } }

            public sealed class Customizer : IModelCustomizer { public void Customize(ModelBuilder modelBuilder, DbContext context) { } }

            public sealed class Ctx : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Row>().Property(row => row.Id).HasConversion(v => v.ToString(), s => int.Parse(s));
                    modelBuilder.Entity<Row>().Property(row => row.Id).HasValueGenerator<AuthoredGenerator>();
                    modelBuilder.Entity<Row>().Property(row => row.Kind).HasConversion<int>();
                }

                protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) { }
            }

            // Containers and contexts.
            public static class Wiring
            {
                public static void Register(IServiceCollection services)
                {
                    services.AddDbContext<Ctx>();
                    services.AddDbContextPool<Ctx>(options => { });
                    services.AddSingleton<Ctx>();
                    services.AddDbContextFactory<Ctx>();
                    services.AddSingleton<IDisposable>(sp => new Disposer());
                    services.AddScoped<Scoped>();
                    services.AddHostedService<Hosted>();
                    services.AddHostedService<HostedWithDispose>();
                    services.AddSingleton<IDisposable>(sp => Builders.Build());
                    services.AddSingleton<IDisposable>(sp => sp.GetRequiredService<IProductFactory>().Create());
                    services.AddSingleton<IDisposable>(sp => Holders.Held.Invoke(sp));
                    services.AddSingleton<IDisposable>(Holders.Held);
                    services.AddScoped<Both>();
                    services.AddSingleton<Both>();
                    services.AddScoped<Both>();
                    services.AddTransient<Transient>();
                    services.AddSingleton<Both>(sp => { int ignored = Holders.Count.Invoke(); return new Both(); });
                }

                public static void Convert(Ctx context)
                {
                    IDisposable disposable = context;
                    object boxed = context;
                    DbContext same = context;
                    ArgumentNullException.ThrowIfNull(context);
                    bool unchanged = object.ReferenceEquals(context, null);
                }
            }

            public sealed class Disposer : IDisposable { public void Dispose() { } }

            public sealed class Scoped : IDisposable { public void Dispose() { } }

            public sealed class Hosted : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask; public override void Dispose() { } }

            // A hosted service whose root member and whose other override both reach a site: only the root member is the hosted root.
            public sealed class HostedWithDispose : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { File.Delete("effect"); return Task.CompletedTask; }

                public override void Dispose() => File.Delete("effect");
            }

            // Disposals that are not the implementation itself: an override of an authored virtual Dispose, an explicit implementation.
            public class AuthoredBaseDisposal : IDisposable { public virtual void Dispose() { } }

            public sealed class OverridingDisposal : AuthoredBaseDisposal { public override void Dispose() => File.Delete("effect"); }

            public sealed class ExplicitDisposal : IDisposable { void IDisposable.Dispose() => File.Delete("effect"); }

            // Types a registration factory builds through a helper, through an interface member and through nothing it can list.
            public sealed class HelperBuilt : IDisposable { public void Dispose() => File.Delete("effect"); }

            public sealed class ViaInterfaceBuilt : IDisposable { public void Dispose() => File.Delete("effect"); }

            public interface IProductFactory { IDisposable Create(); }

            public sealed class ProductFactory : IProductFactory { public IDisposable Create() => new ViaInterfaceBuilt(); }

            public static class Builders { public static IDisposable Build() => new HelperBuilt(); }

            public static class Holders { public static readonly Func<IServiceProvider, IDisposable> Held = sp => null; public static readonly Func<int> Count = () => 1; }

            public sealed class Both : IDisposable { public void Dispose() { } }

            public sealed class Transient : IDisposable { public void Dispose() { } }

            // Scheduling.
            public sealed class Contextual : SynchronizationContext { }

            public sealed class Scheduler : TaskScheduler
            {
                protected override IEnumerable<Task> GetScheduledTasks() => [];

                protected override void QueueTask(Task task) { }

                protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
            }

            public readonly struct OwnAwaiter : INotifyCompletion { public bool IsCompleted => true; public void GetResult() { } public void OnCompleted(Action continuation) { } }

            public sealed class Awaitable { public OwnAwaiter GetAwaiter() => default; }

            public static class Ambient
            {
                public static void Install()
                {
                    AsyncLocal<int> handled = new(args => { });
                    AsyncLocal<int> plain = new();
                    SynchronizationContext.SetSynchronizationContext(null);
                    TaskScheduler.UnobservedTaskException += (sender, args) => { };
                }
            }

            // State machines and reflection.
            public static class Machines
            {
                public static IEnumerable<int> Iterates(IGrimoireWorkLease lease) { yield return 1; }

                public static IEnumerable<int> IteratesOverALocal() { IGrimoireWorkLease local = null; yield return 1; }

                public static async IAsyncEnumerable<int> IteratesAsynchronously(IGrimoireWorkLease lease) { await Task.Yield(); yield return 1; }

                public static async void FiresAndForgets(IGrimoireWorkLease lease) { await Task.Yield(); }

                public static async Task AwaitedWithALease(IGrimoireWorkLease lease) { await Task.Yield(); }

                public static async ValueTask AwaitedValueWithALease(IGrimoireWorkLease lease) { await Task.Yield(); }

                public static IEnumerable<int> IteratesWithoutALease(int count) { yield return count; }
            }

            public static class Reflective
            {
                public static void Carry(IGrimoireWorkLease lease, MethodInfo method, FieldInfo field, object target)
                {
                    method.Invoke(target, new object[] { lease });
                    field.SetValue(target, lease);
                    Activator.CreateInstance(typeof(Row), lease);
                }

                public static void Load()
                {
                    Assembly.Load("fixture");
                }

                // An unsafe accessor reads a field or invokes a member of another type without the source naming a call of it; a delegate built from
                // a method object and a handler added through an event object run authored code the same way.
                public static void ViaAccessors(Row row, Keeper keeper, IGrimoireWorkLease lease)
                {
                    ref int id = ref Accessors.Id(row);
                    Accessors.Carry(keeper, lease);
                    Accessors.Count(keeper);
                }

                public static void Delegates(MethodInfo method, EventInfo eventInfo, object target, Delegate handler)
                {
                    method.CreateDelegate(typeof(Action), target);
                    eventInfo.AddEventHandler(target, handler);
                }

                public static void Plain(MethodInfo method, FieldInfo field, object target)
                {
                    method.Invoke(target, new object[] { 1 });
                    field.SetValue(target, 1);
                    Activator.CreateInstance(typeof(Row));
                    Expression<Func<int>> expression = () => 1;
                    expression.Compile();
                }
            }

            public sealed class Keeper { private void Carry(IGrimoireWorkLease lease) { } private int Count() => 1; }

            public static class Accessors
            {
                [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Id>k__BackingField")]
                public static extern ref int Id(Row @this);

                [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Carry")]
                public static extern void Carry(Keeper @this, IGrimoireWorkLease lease);

                [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Count")]
                public static extern int Count(Keeper @this);

                // Declared and never called: an accessor nothing uses reads nothing.
                [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Id>k__BackingField")]
                public static extern ref int Unused(Row @this);
            }

            // Serializers, loggers, observers.
            public sealed class Callbacks : IJsonOnSerializing { public void OnSerializing() { } }

            public sealed class Converter : JsonConverter<int>
            {
                public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => 0;

                public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) { }
            }

            public static class Logging
            {
                private const string Template = "constant {@Value}";

                private const string Joined = "joined " + "{@" + "Value}";

                private const string Plain = "plain {Value}";

                public static void Log(object value)
                {
                    Serilog.Log.Information("destructured {@Value}", value);
                    Serilog.Log.Information("formatted {Value}", value);
                    new LoggerConfiguration().Destructure.ByTransforming<Row>(row => row);
                }

                public static void ViaConstant(object value) { Serilog.Log.Information(Template, value); }

                public static void ViaConcatenation(object value) { Serilog.Log.Information(Joined, value); }

                public static void ViaMicrosoftLogger(Microsoft.Extensions.Logging.ILogger logger, object value) { Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, Template, value); }

                public static void ViaPlainConstant(object value) { Serilog.Log.Information(Plain, value); }

                public static void ViaRuntimeTemplate(object value, string template) { Serilog.Log.Information(template, value); }

                public static void ViaConditional(object value, bool flag) { Serilog.Log.Information(flag ? "branch {@Value}" : "branch {Value}", value); }

                public static void ViaConditionalPlain(object value, bool flag) { Serilog.Log.Information(flag ? "plain a {Value}" : "plain b {Value}", value); }

                private static void Helper(string template, object value) { Serilog.Log.Information(template, value); }

                public static void CallsHelperWithAnOperator(object value) { Helper("through a helper {@Value}", value); }

                private static void PlainHelper(string template, object value) { Serilog.Log.Information(template, value); }

                public static void CallsPlainHelper(object value) { PlainHelper("plain helper {Value}", value); PlainHelper("another {Value}", value); }

                private static void EscapingHelper(string template, object value) { Serilog.Log.Information(template, value); }

                public static Action<string, object> Escapes() => EscapingHelper;

                public static void PublicHelper(string template, object value) { Serilog.Log.Information(template, value); }
            }

            public static partial class Generated
            {
                [Microsoft.Extensions.Logging.LoggerMessage(EventId = 1, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "generated {@Value}")]
                public static partial void Destructured(Microsoft.Extensions.Logging.ILogger logger, object value);

                public static partial void Destructured(Microsoft.Extensions.Logging.ILogger logger, object value) { }

                [Microsoft.Extensions.Logging.LoggerMessage(EventId = 2, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "generated {Value}")]
                public static partial void Plain(Microsoft.Extensions.Logging.ILogger logger, object value);

                public static partial void Plain(Microsoft.Extensions.Logging.ILogger logger, object value) { }
            }

            public sealed class Listener : EventListener { protected override void OnEventWritten(EventWrittenEventArgs eventData) { } }

            public static class Observers
            {
                public static void Observe()
                {
                    MeterListener listener = new();
                    Console.CancelKeyPress += (sender, args) => { };
                    AppDomain.CurrentDomain.ProcessExit += (sender, args) => { };
                    AppContext.SetSwitch("fixture", true);
                    FileSystemWatcher watcher = new();
                    watcher.Created += (sender, args) => { };
                }
            }

            [JsonSerializable(typeof(Root))]
            [JsonSerializable(typeof(Holder))]
            public sealed class SerializerRoots { }

            // A context that reads public fields: the types of the fields of the types it roots are read too.
            [JsonSourceGenerationOptions(IncludeFields = true)]
            [JsonSerializable(typeof(FieldRoot))]
            public sealed class FieldSerializerRoots { }

            public sealed class FieldRoot { public ViaField Field; }

            public sealed class ViaField { public string Effect => File.ReadAllText("field"); }

            public sealed class Root { public Child Child { get; set; } public List<Leaf> Leaves { get; set; } public int[] Numbers { get; set; } private Hidden Private { get; set; } public UnreadField Field; }

            // Members the serializer reads without a public property: a subtype a base type names, a non-public property and a field marked for inclusion.
            public sealed class Holder
            {
                public Poly Poly { get; set; }

                [JsonInclude] internal Secret Included { get; set; }

                [JsonInclude] public IncludedField Marked;

                public UnreadField Unmarked;
            }

            [JsonPolymorphic]
            [JsonDerivedType(typeof(Sub))]
            public class Poly { }

            public sealed class Sub : Poly { public string Effect => File.ReadAllText("sub"); }

            public sealed class Secret { public string Effect => File.ReadAllText("secret"); }

            public sealed class IncludedField { public string Effect => File.ReadAllText("included"); }

            public sealed class UnreadField { public string Effect => File.ReadAllText("unread"); }

            public class Child : Base { public string Name => "child"; }

            public class Base { public string BaseName => "base"; }

            public sealed class Leaf { public string Text { get; set; } }

            public sealed class Hidden { }

            public sealed class NotSerialized { public string Reads => File.ReadAllText("a"); }

            // Overrides, disposals and accessors the probe must flag, with controls it must not.
            public sealed class FileBackedStream : Stream
            {
                public override bool CanRead => false;

                public override bool CanSeek => false;

                public override bool CanWrite => true;

                public override long Length => 0;
                public override long Position { get => 0; set { } }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count) => 0;

                public override long Seek(long offset, SeekOrigin origin) => 0;
                public override void SetLength(long value) { }
                public override void Write(byte[] buffer, int offset, int count) => File.Delete("effect");
            }

            public sealed class MemoryOnlyStream : Stream
            {
                public override bool CanRead => false;

                public override bool CanSeek => false;

                public override bool CanWrite => true;

                public override long Length => 0;
                public override long Position { get => 0; set { } }
                public override void Flush() { }
                public override int Read(byte[] buffer, int offset, int count) => 0;

                public override long Seek(long offset, SeekOrigin origin) => 0;
                public override void SetLength(long value) { }
                public override void Write(byte[] buffer, int offset, int count) { }
            }

            public sealed class EffectfulDisposal : IDisposable { public void Dispose() => File.Delete("effect"); }

            public sealed class QuietDisposal : IDisposable { public void Dispose() { } }

            public sealed class EffectfulGetter { public string Value => File.ReadAllText("effect"); public int Plain { get; set; } }
        }
        """;

    private static readonly Lazy<IReadOnlyList<CSharpCompilation>> Synthetic = new(static () =>
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticLimits",
            [CSharpSyntaxTree.ParseText(Shapes, path: "src/Fixture.cs")],
            TrustedPlatformReferences.Shared,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return [compilation];
    });

    private static IReadOnlyList<Compilation> SyntheticCompilations() => [.. Synthetic.Value];

    private void Show(string pin, string[] findings)
    {
        foreach (string finding in findings)
        {
            output.WriteLine($"LIMIT_PIN {pin} :: {finding}");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // EF value converters, value generators, comparers and convention plug-ins.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Production_holds_no_authored_ef_value_converter_generator_comparer_customizer_or_convention()
    {
        string[] findings = Production(CheckerLimitScans.EfModelCustomization);

        Show(nameof(CheckerLimitScans.EfModelCustomization), findings);

        // The model's two conversions are the built-in conversion of an enum to its integer, HasConversion<int>(), which runs framework code only.
        Assert.Empty(findings);
    }

    [Fact]
    public void The_ef_model_scan_finds_every_authored_extension_point_and_leaves_the_built_in_conversion_alone()
    {
        string[] findings = CheckerLimitScans.EfModelCustomization(SyntheticCompilations());

        foreach (string expected in new[]
        {
            "type Fixture.AuthoredConverter derives from Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter`2",
            "type Fixture.AuthoredGenerator derives from Microsoft.EntityFrameworkCore.ValueGeneration.ValueGenerator`1",
            "type Fixture.AuthoredComparer derives from Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer`1",
            "type Fixture.Customizer implements Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer",
            "override Fixture.Ctx.ConfigureConventions",
        })
        {
            Assert.Contains(expected, findings);
        }

        // HasConversion with lambdas and HasValueGenerator are calls; HasConversion<int>() is not.
        Assert.Equal(2, findings.Count(static finding => finding.StartsWith("call ", StringComparison.Ordinal)));

        Assert.DoesNotContain(findings, static finding => finding.Contains("HasConversion", StringComparison.Ordinal) && finding.Contains("ConfigureConventions", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Contexts the container owns: root-provider disposal, and a disposable of unknown provenance.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_only_context_that_outlives_a_scope_is_the_one_pooled_registration()
    {
        Assert.Equal(["RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext"], Production(CheckerLimitScans.AuthoredContexts));

        string[] registrations = Production(CheckerLimitScans.ContextRegistrations);

        Show(nameof(CheckerLimitScans.ContextRegistrations), registrations);

        // The scoped registration is the offline CLI composition; the pool is the host's. The pool is a singleton that holds idle contexts and
        // disposes them with the root provider, at host shutdown after the hosted services have stopped and the gate has closed. A pooled context
        // is returned to the pool with its connection closed: the Grimoire opens a connection for one operation and closes it after, so disposing
        // an idle pooled context raises no connection callback. The registration list is exact so that a second pool, a singleton context or a
        // context factory is a failing test rather than a new owner of contexts that nothing here disposes.
        Assert.Equal(
            [
                "Pool RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext by AddDbContextPool in RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddArcanumInfrastructure",
                "Scoped RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext by AddDbContext in RetroDownfall.Arcanum.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddArcanumGrimoireForCli",
            ],
            registrations);
    }

    [Fact]
    public void The_context_registration_scan_finds_a_pool_a_singleton_a_factory_and_a_scoped_context()
    {
        string[] registrations = CheckerLimitScans.ContextRegistrations(SyntheticCompilations());

        Assert.Contains("Scoped Fixture.Ctx by AddDbContext in Fixture.Wiring.Register", registrations);

        Assert.Contains("Pool Fixture.Ctx by AddDbContextPool in Fixture.Wiring.Register", registrations);

        Assert.Contains("Singleton Fixture.Ctx by AddSingleton in Fixture.Wiring.Register", registrations);

        Assert.Contains("Factory Fixture.Ctx by AddDbContextFactory in Fixture.Wiring.Register", registrations);
    }

    [Fact]
    public void No_authored_context_is_converted_to_a_type_that_is_not_a_context()
    {
        string[] findings = Production(CheckerLimitScans.ContextConversions);

        Show(nameof(CheckerLimitScans.ContextConversions), findings);

        // A context is disposed where it is written, as a context. A context held as an interface or as object is a disposable the checker
        // does not assume is a context (every Stream.Dispose would otherwise be refused), so the source keeps none. The null guard and the
        // reference comparison only look at the value, as the Authority Carrier Ban reviews them, and are no conversion that keeps one.
        Assert.Empty(findings);
    }

    [Fact]
    public void The_context_conversion_scan_finds_a_conversion_to_an_interface_and_to_object_and_not_to_the_context_base()
    {
        string[] findings = CheckerLimitScans.ContextConversions(SyntheticCompilations());

        Assert.Contains("Fixture.Ctx to System.IDisposable in Fixture.Wiring.Convert", findings);

        Assert.Contains("Fixture.Ctx to System.Object in Fixture.Wiring.Convert", findings);

        Assert.Equal(2, findings.Length);
    }

    [Fact]
    public void The_one_connection_state_subscription_is_pinned_by_the_inventory_fixtures()
    {
        // A state change the provider makes by itself is not an operation of the Interceptor and Logger lane. The product subscribes one named
        // handler to DbConnection.StateChange and the inventory pins exactly that (Production_subscribes_exactly_one_named_handler_to_connection_state_changes).
        Assert.Contains("Production_subscribes_exactly_one_named_handler_to_connection_state_changes", File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "Operations", "HostedGrimoireProducerInventoryTests.ConnectionState.cs")), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // The scheduling context an awaiter captures.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Production_installs_no_scheduling_context_and_declares_no_awaiter()
    {
        string[] findings = Production(CheckerLimitScans.SchedulingContext);

        Show(nameof(CheckerLimitScans.SchedulingContext), findings);

        // The one finding is the host's handler for a faulted task nobody observed. It runs on the finalizer thread when such a task is
        // collected, outside every root. It logs the exception and marks it observed, and it calls nothing else.
        Assert.Equal(["UnobservedTaskException subscription in RetroDownfall.Arcanum.Cli.Commands.ServeCommand.RunHostAsync"], findings);

        Assert.Equal(
            ["Serilog.Log.Error", "System.Threading.Tasks.UnobservedTaskExceptionEventArgs.SetObserved"],
            CheckerLimitScans.CallsOfHandler(Production(), static (model, node) =>
                node is Microsoft.CodeAnalysis.CSharp.Syntax.ParenthesizedLambdaExpressionSyntax lambda
                && lambda.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax assignment
                && model.GetSymbolInfo(assignment.Left).Symbol is IEventSymbol { Name: "UnobservedTaskException" }));
    }

    [Fact]
    public void The_scheduling_scan_finds_a_context_a_scheduler_an_awaiter_an_async_local_handler_and_the_installers()
    {
        string[] findings = CheckerLimitScans.SchedulingContext(SyntheticCompilations());

        Assert.Contains("type Fixture.Contextual derives from System.Threading.SynchronizationContext", findings);

        Assert.Contains("type Fixture.Scheduler derives from System.Threading.Tasks.TaskScheduler", findings);

        Assert.Contains("awaiter Fixture.Awaitable.GetAwaiter", findings);

        Assert.Contains("AsyncLocal with a change handler in Fixture.Ambient.Install", findings);

        Assert.Contains("SetSynchronizationContext in Fixture.Ambient.Install", findings);

        Assert.Contains("UnobservedTaskException subscription in Fixture.Ambient.Install", findings);

        // The AsyncLocal with no handler is not a finding.
        Assert.Single(findings, static finding => finding.StartsWith("AsyncLocal", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // The Authority Carrier Ban scanner: state machines and reflection.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_iterator_async_void_or_non_task_async_member_declares_an_authority_handle()
    {
        string[] findings = Production(CheckerLimitScans.CarrierStateMachineHoisting);

        Show(nameof(CheckerLimitScans.CarrierStateMachineHoisting), findings);

        // The scanner reads the source, and an async or iterator method keeps its parameters and locals in a compiler-generated state machine
        // the source does not show. A task-returning async method's machine is owned by the awaiter for the whole of the method, which is the
        // lifetime of the lease it holds, so it is no carrier; an iterator's machine is the sequence, enumerated at the consumer's pace, and an
        // async void method's machine is owned by nobody. The product declares neither with a handle.
        Assert.Empty(findings);
    }

    [Fact]
    public void The_hoisting_scan_finds_an_iterator_an_async_iterator_and_an_async_void_and_leaves_awaited_methods_alone()
    {
        string[] findings = CheckerLimitScans.CarrierStateMachineHoisting(SyntheticCompilations());

        Assert.Contains(findings, static finding => finding.StartsWith("iterator Fixture.Machines.Iterates ", StringComparison.Ordinal) || finding.Contains("Fixture.Machines.Iterates declares", StringComparison.Ordinal));

        Assert.Contains(findings, static finding => finding.Contains("Fixture.Machines.IteratesOverALocal declares", StringComparison.Ordinal));

        Assert.Contains(findings, static finding => finding.Contains("Fixture.Machines.IteratesAsynchronously declares", StringComparison.Ordinal));

        Assert.Contains(findings, static finding => finding.StartsWith("async void ", StringComparison.Ordinal) && finding.Contains("Fixture.Machines.FiresAndForgets declares", StringComparison.Ordinal));

        Assert.DoesNotContain(findings, static finding => finding.Contains("AwaitedWithALease", StringComparison.Ordinal) || finding.Contains("AwaitedValueWithALease", StringComparison.Ordinal) || finding.Contains("IteratesWithoutALease", StringComparison.Ordinal));
    }

    [Fact]
    public void Reflective_invocations_are_exactly_the_reviewed_ones_and_carry_no_authority_handle()
    {
        string[] findings = Production(CheckerLimitScans.CarrierReflection);

        Show(nameof(CheckerLimitScans.CarrierReflection), findings);

        // Two constructions of authored tools by the container's activator, each handed the provider and the registered tool, neither of which
        // holds a lease. The product reads and writes no member by reflection, invokes none, builds no delegate from a method object, adds no
        // handler through an event object, and loads no assembly: the compiled model and the source-generated serializers use the reflection API
        // to name members, which hands no value to a member. The third finding is the design-time context factory loading the pinned SQLCipher
        // library so that the EF tools can open a model; the factory is allocated only by those tools
        // (Production_allocates_the_context_in_the_installation_reset_and_in_a_design_time_factory_only).
        Assert.Equal(
            [
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance in RetroDownfall.Arcanum.Api.ApiBootstrapper.AddArcanumApiServices",
                "Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance in RetroDownfall.Arcanum.Api.ApiBootstrapper.AddArcanumApiServices",
                "System.Runtime.InteropServices.NativeLibrary.Load in RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContextFactory.InitializeDesignTimeNativeRuntime",
            ],
            findings.Where(static finding => !finding.StartsWith("UnsafeAccessor ", StringComparison.Ordinal)));

        // The unsafe accessors are the EF compiled model's: it reads and writes the backing fields of its entity types through them, in the
        // members that build the model. The entity classes hold the columns of the Grimoire and no lease or group, which the scan checks for
        // every accessor (the type it reads or returns and every argument), so no finding carries a handle.
        Assert.Equal(ReviewedUnsafeAccessors.Order(StringComparer.Ordinal), findings.Where(static finding => finding.StartsWith("UnsafeAccessor ", StringComparison.Ordinal)));
    }

    /// <summary>The generated accessor classes of the compiled model, and the generated member that calls each: entity fields only, none holding a handle.</summary>
    private static readonly string[] ReviewedUnsafeAccessors =
    [
        .. new (string Accessors, string Holder)[]
        {
            ("Apprentice", "ApprenticeEntityType.Create"),
            ("Campaign", "CampaignEntityType.Create"),
            ("Entry", "EntryEntityType.Create"),
            ("Entry", "EntryEntityType.CreateForeignKey1"),
            ("MageSetting", "MageSettingEntityType.Create"),
            ("Prompt", "PromptEntityType.Create"),
            ("Prompt", "PromptEntityType.CreateForeignKey1"),
            ("Session", "EntryEntityType.CreateForeignKey1"),
            ("Session", "SessionEntityType.Create"),
            ("WorkspaceContext", "WorkspaceContextEntityType.Create"),
        }.Select(static pair => $"UnsafeAccessor {P}Infrastructure.Generated.{pair.Accessors}UnsafeAccessors in {P}Infrastructure.Generated.{pair.Holder}"),
    ];

    [Fact]
    public void The_reflection_scan_finds_each_reflective_call_and_marks_the_ones_handed_a_handle()
    {
        string[] findings = CheckerLimitScans.CarrierReflection(SyntheticCompilations());

        Assert.Contains("System.Reflection.MethodBase.Invoke in Fixture.Reflective.Carry carries an authority handle", findings);

        Assert.Contains("System.Reflection.FieldInfo.SetValue in Fixture.Reflective.Carry carries an authority handle", findings);

        Assert.Contains("System.Activator.CreateInstance in Fixture.Reflective.Carry carries an authority handle", findings);

        Assert.Contains("System.Reflection.MethodBase.Invoke in Fixture.Reflective.Plain", findings);

        Assert.Contains("System.Reflection.FieldInfo.SetValue in Fixture.Reflective.Plain", findings);

        Assert.Contains("System.Activator.CreateInstance in Fixture.Reflective.Plain", findings);

        Assert.Contains("System.Linq.Expressions.Expression`1.Compile in Fixture.Reflective.Plain", findings);

        Assert.Contains("System.Reflection.Assembly.Load in Fixture.Reflective.Load", findings);

        // The unsafe accessors that are called are reflective accesses, the one handed a handle marked; the one nothing calls is not.
        // The accessors of a class that one member calls are one finding, marked when any of them is handed or returns a handle.
        Assert.Single(findings, static finding => finding.StartsWith("UnsafeAccessor ", StringComparison.Ordinal));

        Assert.Contains("UnsafeAccessor Fixture.Accessors in Fixture.Reflective.ViaAccessors carries an authority handle", findings);

        Assert.Contains("System.Reflection.MethodInfo.CreateDelegate in Fixture.Reflective.Delegates", findings);

        Assert.Contains("System.Reflection.EventInfo.AddEventHandler in Fixture.Reflective.Delegates", findings);

        Assert.DoesNotContain(findings, static finding => finding.Contains("Plain", StringComparison.Ordinal) && finding.Contains("carries", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Serializers and loggers.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Production_has_no_serializer_callback_destructuring_template_or_converter_that_reaches_a_site()
    {
        string[] findings = Production(CheckerLimitScans.SerializerAndLoggerCallbacks);

        Show(nameof(CheckerLimitScans.SerializerAndLoggerCallbacks), findings);

        // No type implements a serializer callback contract, no message template destructures its argument (so a structured logger formats a
        // value with the slots the inventory follows and reads none of its properties), and no destructuring is configured.
        Assert.Empty(findings);

        // The product's converters are the wire converters of the OpenAI surface and the configuration; a converter runs when a value is read or
        // written, in code the census does not follow, so the list is exact and none of them reaches a producer site.
        string[] converters = Production(CheckerLimitScans.JsonConverters);

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Api.Intelligence.OpenAi.OpenAiChatUsageJsonConverter",
                "RetroDownfall.Arcanum.Api.Intelligence.OpenAi.OpenAiEmbeddingInputConverter",
                "RetroDownfall.Arcanum.Api.Intelligence.OpenAi.OpenAiEmbeddingVectorConverter",
                "RetroDownfall.Arcanum.Api.Intelligence.OpenAi.OpenAiMessageContentConverter",
                "RetroDownfall.Arcanum.Api.Intelligence.OpenAi.OpenAiModerationInputConverter",
                "RetroDownfall.Arcanum.Core.Configuration.ModelEntryJsonConverter",
                "RetroDownfall.Arcanum.Core.Serialization.StringOnlyJsonStringEnumConverter`1",
            ],
            converters);

        string[] reaching = [.. ReachingMembers(HostedGrimoireProducerInventory.FrameworkEntryPoints.Overrides)
            .Where(finding => converters.Any(converter => finding.StartsWith(converter.Replace("`1", "", StringComparison.Ordinal), StringComparison.Ordinal)))];

        Assert.Empty(reaching);
    }

    [Fact]
    public void The_serializer_and_logger_scan_finds_a_callback_contract_a_destructuring_template_and_a_destructuring_configuration()
    {
        string[] findings = CheckerLimitScans.SerializerAndLoggerCallbacks(SyntheticCompilations());

        Assert.Contains("type Fixture.Callbacks implements System.Text.Json.Serialization.IJsonOnSerializing", findings);

        Assert.Contains("destructuring configuration in Fixture.Logging.Log", findings);

        // A template is judged by its constant value, wherever it is written: a literal, a constant, a concatenation of constants, a call through
        // Microsoft.Extensions.Logging (whose provider is Serilog, so the operator is honoured) and the message of a LoggerMessage attribute.
        // A conditional is both of its branches, and a parameter of a private method is every value its callers pass. A method that a method group,
        // an override or a caller outside the source can reach has values the source does not list, and so does a runtime template: each is its own
        // finding. The templates with no operator, the plain LoggerMessage and a private helper whose callers all pass plain constants are not findings.
        Assert.Equal(
            [
                "destructuring template in Fixture.Generated.Destructured",
                "destructuring template in Fixture.Logging.Helper",
                "destructuring template in Fixture.Logging.Log",
                "destructuring template in Fixture.Logging.ViaConcatenation",
                "destructuring template in Fixture.Logging.ViaConditional",
                "destructuring template in Fixture.Logging.ViaConstant",
                "destructuring template in Fixture.Logging.ViaMicrosoftLogger",
                "template without a constant value in Fixture.Logging.EscapingHelper",
                "template without a constant value in Fixture.Logging.PublicHelper",
                "template without a constant value in Fixture.Logging.ViaRuntimeTemplate",
            ],
            findings.Where(static finding => finding.Contains("template", StringComparison.Ordinal)));

        Assert.Equal(["Fixture.Converter"], CheckerLimitScans.JsonConverters(SyntheticCompilations()));
    }

    [Fact]
    public void No_type_a_serializer_can_read_has_a_getter_that_reaches_a_site()
    {
        HashSet<string> serializable = CheckerLimitScans.SerializableClosure(Production());

        Assert.NotEmpty(serializable);

        string[] reaching = ReachingMembers(HostedGrimoireProducerInventory.FrameworkEntryPoints.Accessors);

        Show("Accessors", reaching);

        Assert.NotEmpty(reaching);

        // A source-generated serializer reads the properties of the types its attributes name and the types those reach, and nothing else. No
        // type whose getter reaches a producer site is among them, so the getters the census does not follow are never read by a serializer.
        foreach (string finding in reaching)
        {
            string type = finding[..finding.IndexOf(" :: ", StringComparison.Ordinal)];

            type = type[..type.LastIndexOf('.')];

            Assert.DoesNotContain(type, serializable);
        }
    }

    [Fact]
    public void The_serializable_closure_follows_properties_bases_arguments_and_arrays_and_ignores_private_members()
    {
        HashSet<string> closure = CheckerLimitScans.SerializableClosure(SyntheticCompilations());

        Assert.Superset(new HashSet<string>(StringComparer.Ordinal) { "Fixture.Root", "Fixture.Child", "Fixture.Base", "Fixture.Leaf" }, closure);

        Assert.DoesNotContain("Fixture.Hidden", closure);

        Assert.DoesNotContain("Fixture.NotSerialized", closure);

        // The serializer reads the subtypes a base type names, a non-public property marked for inclusion, a field marked for inclusion, and
        // every public field of the types a context that includes fields roots. It does not read an unmarked public field of a context that does not.
        Assert.Superset(new HashSet<string>(StringComparer.Ordinal) { "Fixture.Holder", "Fixture.Poly", "Fixture.Sub", "Fixture.Secret", "Fixture.IncludedField", "Fixture.FieldRoot", "Fixture.ViaField" }, closure);

        Assert.DoesNotContain("Fixture.UnreadField", closure);

        // The accessor probe flags the effectful getter and not the plain property, and the closure does not hold the flagged type.
        string[] reaching = [.. HostedGrimoireProducerInventory.ProbeFrameworkEntryPointsThatReach(Synthetic.Value, HostedGrimoireProducerInventory.FrameworkEntryPoints.Accessors)];

        Assert.Contains(reaching, static finding => finding.StartsWith("Fixture.EffectfulGetter.get_Value", StringComparison.Ordinal));

        Assert.DoesNotContain(reaching, static finding => finding.Contains("Plain", StringComparison.Ordinal));

        Assert.DoesNotContain("Fixture.EffectfulGetter", closure);

        // The getters that reach a site in the types the serializer reads are exactly the ones the pin must refuse: each of these is in the closure.
        foreach (string type in new[] { "Sub", "Secret", "IncludedField", "ViaField" })
        {
            Assert.Contains(reaching, finding => finding.StartsWith("Fixture." + type + ".get_Effect", StringComparison.Ordinal));

            Assert.Contains("Fixture." + type, closure);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // The framework calls authored members through its own types: overrides, and disposals.
    // ---------------------------------------------------------------------------------------------------------------------------------

    // One graph over the production compilations answers every class of entry point, once for the class of tests.
    private static readonly Lazy<IReadOnlyDictionary<HostedGrimoireProducerInventory.FrameworkEntryPoints, IReadOnlyList<string>>> ProductionEntryPoints =
        new(static () => HostedGrimoireProducerInventory.ProbeAllFrameworkEntryPointsThatReach(HostedGrimoireProducerInventory.ProductionCompilations), LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] ReachingMembers(HostedGrimoireProducerInventory.FrameworkEntryPoints kind) => [.. ProductionEntryPoints.Value[kind]];

    private static string TypeOfFinding(string finding)
    {
        string head = finding[..finding.IndexOf(" :: ", StringComparison.Ordinal)];

        return head[..head.LastIndexOf('.')];
    }

    private static string MemberOfFinding(string finding)
    {
        string head = finding[..finding.IndexOf(" :: ", StringComparison.Ordinal)];

        return head[(head.LastIndexOf('.') + 1)..];
    }

    // The members of a hosted service that the host runs as its roots: the rest of what a hosted service overrides is run by something else.
    private static readonly HashSet<string> HostedRootMembers = new(StringComparer.Ordinal) { "StartAsync", "ExecuteAsync", "StopAsync" };

    /// <summary>The types that override a framework virtual member and reach a site, once the hosted roots and the EF interceptors are left out.</summary>
    private static string[] OverridesOutsideTheRoots(IEnumerable<string> findings, IReadOnlySet<string> hosted, Func<string, bool> isInterceptor) =>
    [
        .. findings
            .Where(finding => !(hosted.Contains(TypeOfFinding(finding)[(TypeOfFinding(finding).LastIndexOf('.') + 1)..]) && HostedRootMembers.Contains(MemberOfFinding(finding))))
            .Select(TypeOfFinding)
            .Distinct(StringComparer.Ordinal)
            .Where(type => !isInterceptor(type))
            .Order(StringComparer.Ordinal),
    ];

    [Fact]
    public void Authored_overrides_of_framework_virtual_members_that_reach_a_site_are_exactly_the_reviewed_types()
    {
        IReadOnlyList<Compilation> production = Production();

        HashSet<string> hosted = [.. HostedGrimoireProducerInventory.Catalog.Select(static entry => entry.ServiceType)];

        // Three kinds of override are not a limit. A hosted service's root member (StartAsync, ExecuteAsync, StopAsync) is the hosted root, and its
        // other overrides are not. An EF interceptor's override is a callback the Interceptor and Logger lane follows. Every other type is a framework
        // class or interface subclassed in the product, reached through the framework's type, and is pinned exactly with the reason it is outside the claim.
        string[] unreviewed = OverridesOutsideTheRoots(ReachingMembers(HostedGrimoireProducerInventory.FrameworkEntryPoints.Overrides), hosted, type => IsInterceptor(production, type));

        Show("Overrides", unreviewed);

        Assert.Equal(ReviewedFrameworkOverrides.Select(static entry => entry.Type).Order(StringComparer.Ordinal), unreviewed);

        // The reviewed types are constructed exactly where they were reviewed.
        foreach ((string type, string[] sites, string reason) in ReviewedFrameworkOverrides)
        {
            string[] actual = CheckerLimitScans.ConstructionSites(production, type);

            Assert.True(actual.SequenceEqual(sites.Order(StringComparer.Ordinal), StringComparer.Ordinal), $"{type} ({reason}) is constructed in [{string.Join("; ", actual)}].");
        }
    }

    private static bool IsInterceptor(IReadOnlyList<Compilation> production, string type) => HostedGrimoireProducerInventory.DeclaredTypesOf(production)
        .Where(candidate => CheckerLimitScans.Key(candidate) == type)
        .Any(candidate => CheckerLimitScans.DerivesFrom(candidate, static ancestor => ancestor.Name.EndsWith("Interceptor", StringComparison.Ordinal)
            && ancestor.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)));

    /// <summary>The authored types that override a framework virtual member and reach a site: where each is constructed, and why it is outside the claim.</summary>
    private static readonly (string Type, string[] ConstructedIn, string Reason)[] ReviewedFrameworkOverrides =
    [
        (P + "Api.Intelligence.OpenAiRequestAugmentingHandler", [],
            "A message handler of the OpenAI-compatible provider client, built by the client factory. It forwards the request the classified model call makes, so its send is that call."),
        (P + "Api.Intelligence.OpenAiRequestAugmentingHandler.PrefixedStream", [P + "Api.Intelligence.OpenAiRequestAugmentingHandler.ResponseMentionsStrictAsync"],
            "Replays a response body that handler has already read, inside the handler's send."),
        (P + "Api.Intelligence.Tools.ArcanumBrowseWebTool", [P + "Api.Intelligence.Tools.BuiltInToolRegistry.Resolve", P + "Api.Intelligence.WizardIntelligenceProvider.BuildToolSetWithMcpAsync"],
            "An AI tool the chat client's function-invocation loop invokes inside the classified model call."),
        (P + "Api.Intelligence.Tools.ArcanumDelegateTaskTool", [P + "Api.Intelligence.WizardIntelligenceProvider.BuildToolSetWithMcpAsync"],
            "An AI tool the chat client's function-invocation loop invokes inside the classified model call."),
        (P + "Api.Intelligence.Tools.ArcanumSpellScriptTool", [P + "Api.Intelligence.WizardIntelligenceProvider.BuildToolSetWithMcpAsync"],
            "An AI tool the chat client's function-invocation loop invokes inside the classified model call."),
        (P + "Api.Security.IdempotencyBufferingStream", [P + "Api.Security.IdempotencyEndpointFilters.ExecuteMissAsync"],
            "Buffers an HTTP response inside an endpoint filter: request-pipeline code that no hosted root runs."),
        (P + "Api.Tower.SessionEndpoints.AttachmentMultipartAggregateReadStream", [P + "Api.Tower.SessionEndpoints.MapSessionEndpoints"],
            "Reads an uploaded attachment inside an endpoint handler: request-pipeline code that no hosted root runs."),
        (P + "Cli.Infrastructure.DeferredJsonTextWriter", [P + "Cli.Infrastructure.CliApplicationFactory.RunAsync"],
            "The command-line client's output writer: a client process, not the process that hosts the roots."),
        (P + "Cli.Services.StreamingTextReader",
            [P + "Cli.Services.ArcanumApiClient.AskStreamAsync", P + "Cli.Services.ArcanumApiClient.ResearchWebAsync", P + "Cli.Services.ArcanumApiClient.StreamApprenticeChronicleAsync", P + "Cli.Services.ArcanumApiClient.WatchSseAsync"],
            "Reads a server-sent stream in the command-line client: a client process, not the process that hosts the roots."),
        (P + "Infrastructure.A2A.ArcanumA2AServer", [P + "Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddArcanumInfrastructure"],
            "The A2A server object the container builds; the SDK's request handler calls its override for an inbound HTTP request."),
        (P + "Infrastructure.Hosting.CappedOutputStream", [P + "Infrastructure.Hosting.DaemonProcessRunner.RunAsync", P + "Infrastructure.Hosting.DaemonProcessRunner.RunAsync"],
            "Caps the captured output of a daemon process, built inside the runner that IDaemonRunner.RunScheduledAsync, a classified site, calls."),
        (P + "Infrastructure.Logging.ReverseJsonlFileReader.ReadSegmentStream", [P + "Infrastructure.Logging.ReverseJsonlFileReader.DeserializeRangeAsync"],
            "Reads a range of a log file for the log endpoints and the command line: not run by a hosted root."),
        (P + "Infrastructure.Logging.SecureSerilogFileHooks", [P + "Infrastructure.Logging.HostLockSerilogFileSink.Activate"],
            "The file sink's open hook, built when the logger is activated, before any hosted service runs."),
        (P + "Infrastructure.Mcp.McpHttpResponseBoundHandler.BoundedContent", [P + "Infrastructure.Mcp.McpHttpResponseBoundHandler.SendAsync"],
            "Bounds the body of an MCP HTTP response inside the handler's send, which is an MCP client call the inventory classifies."),
        (P + "Infrastructure.Mcp.McpHttpResponseBoundHandler.BoundedReadStream", [P + "Infrastructure.Mcp.McpHttpResponseBoundHandler.BoundedContent.CreateContentReadStreamAsync"],
            "Bounds the read of that body, inside the same send."),
        (P + "Infrastructure.Storage.EncryptedBlobStore.EncryptedBlobReadStream", [P + "Infrastructure.Storage.EncryptedBlobStore.OpenReadCoreAsync"],
            "Returned only by the encrypted blob store's open-for-read slot, a classified site."),
        (P + "Infrastructure.Storage.EncryptedBlobStore.StreamingEncryptedBlobWriter", [P + "Infrastructure.Storage.EncryptedBlobStore.CreateWriterAsync"],
            "Returned only by the encrypted blob store's create-writer slot, a classified site whose publication region the inventory requires to be admitted and completed."),
    ];

    [Fact]
    public void The_override_probe_flags_an_override_that_reaches_a_site_and_leaves_a_quiet_one_alone()
    {
        string[] reaching = [.. HostedGrimoireProducerInventory.ProbeFrameworkEntryPointsThatReach(Synthetic.Value, HostedGrimoireProducerInventory.FrameworkEntryPoints.Overrides)];

        Assert.Contains(reaching, static finding => finding.StartsWith("Fixture.FileBackedStream.Write", StringComparison.Ordinal));

        Assert.DoesNotContain(reaching, static finding => finding.Contains("MemoryOnlyStream", StringComparison.Ordinal));

        string[] disposals = [.. HostedGrimoireProducerInventory.ProbeFrameworkEntryPointsThatReach(Synthetic.Value, HostedGrimoireProducerInventory.FrameworkEntryPoints.Disposals)];

        Assert.Contains(disposals, static finding => finding.StartsWith("Fixture.EffectfulDisposal.Dispose", StringComparison.Ordinal));

        Assert.DoesNotContain(disposals, static finding => finding.Contains("QuietDisposal", StringComparison.Ordinal));

        // The container calls the implementation through the slot, so an override of an authored virtual Dispose, the Dispose a hosted service
        // overrides and an explicit implementation are each a disposal; the quiet virtual they override is not one that reaches a site.
        Assert.Contains(disposals, static finding => finding.StartsWith("Fixture.OverridingDisposal.Dispose ", StringComparison.Ordinal));

        Assert.Contains(disposals, static finding => finding.StartsWith("Fixture.HostedWithDispose.Dispose ", StringComparison.Ordinal));

        Assert.Contains(disposals, static finding => finding.StartsWith("Fixture.ExplicitDisposal.Dispose ", StringComparison.Ordinal));

        Assert.DoesNotContain(disposals, static finding => finding.Contains("AuthoredBaseDisposal", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hosted_exclusion_covers_the_root_members_of_a_hosted_service_and_not_its_other_overrides()
    {
        string[] reaching = [.. HostedGrimoireProducerInventory.ProbeFrameworkEntryPointsThatReach(Synthetic.Value, HostedGrimoireProducerInventory.FrameworkEntryPoints.Overrides)];

        Assert.Contains(reaching, static finding => finding.StartsWith("Fixture.HostedWithDispose.ExecuteAsync ", StringComparison.Ordinal));

        Assert.Contains(reaching, static finding => finding.StartsWith("Fixture.HostedWithDispose.Dispose ", StringComparison.Ordinal));

        // The execute override is the hosted root and the dispose override is not, so the hosted service is still a type to review for its dispose.
        Assert.Equal(
            ["Fixture.FileBackedStream", "Fixture.HostedWithDispose"],
            OverridesOutsideTheRoots(reaching, new HashSet<string>(StringComparer.Ordinal) { "HostedWithDispose" }, static _ => false));

        // A service the catalog does not list has no root member at all.
        Assert.Equal(
            ["Fixture.FileBackedStream", "Fixture.HostedWithDispose"],
            OverridesOutsideTheRoots(reaching, new HashSet<string>(StringComparer.Ordinal), static _ => false));

        Assert.Equal(
            ["Fixture.FileBackedStream"],
            OverridesOutsideTheRoots([.. reaching.Where(static finding => !finding.Contains(".Dispose ", StringComparison.Ordinal))], new HashSet<string>(StringComparer.Ordinal) { "HostedWithDispose" }, static _ => false));
    }

    [Fact]
    public void Authored_disposals_that_reach_a_site_and_belong_to_the_container_are_exactly_the_reviewed_types()
    {
        IReadOnlyDictionary<string, string> registered = CheckerLimitScans.RegisteredImplementations(Production());

        string[] types = [.. ReachingMembers(HostedGrimoireProducerInventory.FrameworkEntryPoints.Disposals).Select(TypeOfFinding).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // A disposable that a container registration builds is disposed by the container, and a singleton by the root provider when the host's root
        // provider is disposed, after every hosted root has stopped. The census does not follow that disposal. The disposals the product writes
        // itself are located where they are written, and the rest are leases and handles that code disposes in a using.
        string[] owned = [.. types.Where(type => registered.TryGetValue(type, out string? lifetime) && lifetime is "Singleton" or "Pool" or "Factory")];

        Show("Disposals", owned);

        Assert.Equal(ReviewedContainerDisposals.Select(static entry => entry.Type).Order(StringComparer.Ordinal), owned);

        // The registrations are complete only while every factory's product can be listed: a factory that invokes a delegate, takes a delegate as
        // a value or builds by reflection may build an authored type that this list cannot name.
        string[] unlisted = CheckerLimitScans.UnresolvedFactoryProducts(Production());

        Show("UnlistedFactoryProducts", unlisted);

        Assert.Equal(ReviewedUnlistedFactoryProducts.Select(static entry => entry.Finding).Order(StringComparer.Ordinal), unlisted);
    }

    /// <summary>The factories whose product the scan cannot list, and why none of them builds a service the container owns.</summary>
    private static readonly (string Finding, string Reason)[] ReviewedUnlistedFactoryProducts =
    [
        ("factory delegate invoked by " + P + "Infrastructure.Security.SecureFilePermissions.CreateOwnerOnlyStream for a registration in " + P + "Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddArcanumInfrastructure",
            "The test seam WindowsOwnerOnlyTempFileCreateForTests, a static delegate that only the tests assign, returns the FileStream the secure file writer uses as a local handle while it writes a file. It is a resource of that call, not the service a registration builds."),
    ];

    /// <summary>The singleton registrations whose disposal reaches a site: the type, and why disposing it with the root provider is outside the claim.</summary>
    private static readonly (string Type, string Reason)[] ReviewedContainerDisposals =
    [
        (P + "Infrastructure.Hosting.GrimoireDatabaseHostedService",
            "Releases the installation maintenance lock it holds, under its own lifecycle gate, as the last step of host shutdown. It writes no Grimoire data and makes no provider, network or workspace call."),
    ];

    [Fact]
    public void The_registered_implementation_scan_finds_a_service_an_implementation_a_factory_product_and_a_hosted_service()
    {
        IReadOnlyDictionary<string, string> registered = CheckerLimitScans.RegisteredImplementations(SyntheticCompilations());

        Assert.Equal("Singleton", registered["Fixture.Disposer"]);

        Assert.Equal("Scoped", registered["Fixture.Scoped"]);

        Assert.Equal("Singleton", registered["Fixture.Hosted"]);

        Assert.False(registered.ContainsKey("Fixture.QuietDisposal"));

        // A factory is followed through the authored methods it calls, an interface member included (every authored implementation of it), to the
        // types they construct, which the container builds and owns with the registration's lifetime.
        Assert.Equal("Singleton", registered["Fixture.HelperBuilt"]);

        Assert.Equal("Singleton", registered["Fixture.ViaInterfaceBuilt"]);

        // A type registered with more than one lifetime is owned for the longest of them, whichever registration is written last.
        Assert.Equal("Singleton", registered["Fixture.Both"]);

        Assert.Equal("Transient", registered["Fixture.Transient"]);
    }

    [Fact]
    public void The_factory_scan_reports_a_factory_whose_product_it_cannot_list()
    {
        // A delegate invoked inside a factory, and a delegate registered as a value rather than written at the registration, have bodies the
        // scan does not read, so what they build is unlisted: each is a finding of its own. The factories that call authored methods are followed.
        Assert.Equal(
            [
                "factory delegate invoked by Fixture.Wiring.Register for a registration in Fixture.Wiring.Register",
                "factory delegate passed as a value for a registration in Fixture.Wiring.Register",
            ],
            CheckerLimitScans.UnresolvedFactoryProducts(SyntheticCompilations()));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Runtime observers.
    // ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Runtime_observers_are_exactly_the_reviewed_registrations()
    {
        string[] findings = Production(CheckerLimitScans.RuntimeObservers);

        Show(nameof(CheckerLimitScans.RuntimeObservers), findings);

        Assert.Equal(ReviewedRuntimeObservers.Select(static entry => entry.Finding).Order(StringComparer.Ordinal), findings);

        // The client commands never run in the process that hosts the roots: every reviewed observer in the Cli project is in a command other
        // than serve, or in the entry point before any host exists.
        Assert.All(ReviewedRuntimeObservers.Where(static entry => entry.Finding.Contains(" in RetroDownfall.Arcanum.Cli.", StringComparison.Ordinal)),
            static entry => Assert.DoesNotContain("ServeCommand", entry.Finding, StringComparison.Ordinal));
    }

    [Fact]
    public void The_metrics_listener_callback_reaches_no_producer_site()
    {
        // The callback runs synchronously on whichever thread records a measurement, including a hosted root's, outside any Work. It must reach no
        // producer site: it only adds the measurement to in-memory series.
        string[] reaching = [.. HostedGrimoireProducerInventory.ProbeMembersThatReach(
            HostedGrimoireProducerInventory.ProductionCompilations,
            static (type, method) => type.Name == "PrometheusMetricsExporter" && method.Name is "OnMeasurementRecorded" or "EnsureListenerStarted" or "SanitizeMetricName" or "BuildLabelString" or "ClassifyInstrument")];

        Assert.Empty(reaching);

        // The probe is not blind to this type: the exporter's rendering path, which is not a callback, is asked the same question.
        Assert.NotNull(HostedGrimoireProducerInventory.DeclaredTypesOf(Production()).Single(static type => type.Name == "PrometheusMetricsExporter"));
    }

    private static readonly (string Finding, string Reason)[] ReviewedRuntimeObservers =
    [
        ("System.AppContext.SetSwitch in " + P + "Cli.Program.Main", "Turns off an ASP.NET Core metadata switch with a constant, in the entry point before any host exists."),
        ("System.Console.CancelKeyPress subscription in " + P + "Cli.CommandCenter.CommandCenterHost.StartUpAsync", "The command center's interrupt handler: it cancels a token."),
        ("System.Console.CancelKeyPress subscription in " + P + "Cli.Commands.AskCommand.Ask", "The ask command's interrupt handler: it cancels a token."),
        ("System.Console.CancelKeyPress subscription in " + P + "Cli.Commands.WatchCommands.WithCancellationAsync", "The watch commands' interrupt handler: it cancels a token."),
        ("System.Diagnostics.Metrics.MeterListener constructed in " + P + "Infrastructure.Telemetry.PrometheusMetricsExporter.EnsureListenerStarted",
            "The metrics exporter's listener. Its callback runs on the thread that records a measurement and only adds the measurement to in-memory series (OnMeasurementRecorded reaches no producer site, pinned below)."),
        ("System.IO.FileSystemWatcher.Changed subscription in " + P + "Cli.CommandCenter.CommandCenterAttachmentDriftMonitor.Start", "The command center's attachment drift monitor: a client process."),
        ("System.IO.FileSystemWatcher.Changed subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "The workspace watcher's handler hands the change to the indexing service's in-memory scheduler; the indexing work itself runs in that service's hosted roots under their own Work."),
        ("System.IO.FileSystemWatcher.Created subscription in " + P + "Cli.CommandCenter.CommandCenterAttachmentDriftMonitor.Start", "The command center's attachment drift monitor: a client process."),
        ("System.IO.FileSystemWatcher.Created subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Created subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Deleted subscription in " + P + "Cli.CommandCenter.CommandCenterAttachmentDriftMonitor.Start", "The command center's attachment drift monitor: a client process."),
        ("System.IO.FileSystemWatcher.Deleted subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Deleted subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Error subscription in " + P + "Cli.CommandCenter.CommandCenterAttachmentDriftMonitor.Start", "The command center's attachment drift monitor: a client process."),
        ("System.IO.FileSystemWatcher.Error subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Error subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Renamed subscription in " + P + "Cli.CommandCenter.CommandCenterAttachmentDriftMonitor.Start", "The command center's attachment drift monitor: a client process."),
        ("System.IO.FileSystemWatcher.Renamed subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.IO.FileSystemWatcher.Renamed subscription in " + P + "Infrastructure.Hosting.FileSystemWorkspaceFileWatcher..ctor", "As the Changed handler."),
        ("System.Runtime.InteropServices.PosixSignalRegistration.Create in " + P + "Cli.CommandCenter.CommandCenterTermination.RegisterWithOperatingSystem", "The command center's termination signal handler: a client process."),
    ];

    [Fact]
    public void The_observer_scan_finds_a_listener_a_meter_listener_runtime_events_an_ambient_switch_and_a_file_watcher()
    {
        string[] findings = CheckerLimitScans.RuntimeObservers(SyntheticCompilations());

        Assert.Contains("type Fixture.Listener is a runtime listener", findings);

        Assert.Contains("System.Diagnostics.Metrics.MeterListener constructed in Fixture.Observers.Observe", findings);

        Assert.Contains("System.Console.CancelKeyPress subscription in Fixture.Observers.Observe", findings);

        Assert.Contains("System.AppDomain.ProcessExit subscription in Fixture.Observers.Observe", findings);

        Assert.Contains("System.AppContext.SetSwitch in Fixture.Observers.Observe", findings);

        Assert.Contains("System.IO.FileSystemWatcher.Created subscription in Fixture.Observers.Observe", findings);
    }
}
