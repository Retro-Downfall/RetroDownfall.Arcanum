using System.Reflection;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("ProcessEnvironment")]
public sealed class GrimoireDbContextCompositionTests
{

    [Theory]
    [InlineData(ProductComposition.NonPooledCli)]
    [InlineData(ProductComposition.PooledHost)]
    public async Task Product_DbContext_options_use_the_singleton_ordinary_lifecycle(
        ProductComposition composition)
    {

        ServiceCollection services = new();

        if (composition == ProductComposition.NonPooledCli)
        {

            services.AddArcanumGrimoireForCli();

        }
        else
        {

            services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<GrimoireDbPassphraseSource>(
                provider.GetRequiredService<IGrimoireDbPassphraseSource>())
            .SetPassphrase("task-4-composition-passphrase");

        IGrimoireOrdinaryConnectionLifecycle lifecycle = provider
            .GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>();

        ICovenantConnectionDrain drain = provider.GetRequiredService<ICovenantConnectionDrain>();

        IGrimoireConnectionAdmissionGate admissionGate = provider
            .GetRequiredService<IGrimoireConnectionAdmissionGate>();

        IGrimoireOrdinaryConnectionFactory ordinaryFactory = provider
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        IGrimoireMaintenanceConnectionFactory maintenanceFactory = provider
            .GetRequiredService<IGrimoireMaintenanceConnectionFactory>();

        IGrimoireDbPassphraseSource passphraseSource = provider
            .GetRequiredService<IGrimoireDbPassphraseSource>();

        ICovenantSqliteConnectionInitializer initializer = provider
            .GetRequiredService<ICovenantSqliteConnectionInitializer>();

        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        Assert.Same(
            lifecycle,
            firstScope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>());

        Assert.Same(
            lifecycle,
            secondScope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>());

        Assert.Same(
            ordinaryFactory,
            firstScope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>());

        Assert.Same(
            ordinaryFactory,
            secondScope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>());

        Assert.Same(
            maintenanceFactory,
            firstScope.ServiceProvider.GetRequiredService<IGrimoireMaintenanceConnectionFactory>());

        Assert.Same(
            maintenanceFactory,
            secondScope.ServiceProvider.GetRequiredService<IGrimoireMaintenanceConnectionFactory>());

        DbContextOptions<ArcanumDbContext> options = firstScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ArcanumDbContext>>();

        CovenantConnectionEnrolmentInterceptor interceptor = Assert.Single(Interceptors(options));

        Assert.Same(lifecycle, GetLifecycle(interceptor));

        Assert.Same(drain, GetDrain(interceptor));

        Assert.Same(drain, GetDrain(admissionGate));

        Assert.Same(drain, GetDrain(lifecycle));

        Assert.Same(lifecycle, GetLifecycle(ordinaryFactory));

        Assert.Same(drain, GetDrain(ordinaryFactory));

        Assert.Same(
            passphraseSource,
            GetDependency<IGrimoireDbPassphraseSource>(
                maintenanceFactory,
                "_passphraseSource"));

        Assert.Same(
            initializer,
            GetDependency<ICovenantSqliteConnectionInitializer>(
                maintenanceFactory,
                "_initializer"));

        Assert.Same(
            SqliteNativeRuntime.Instance,
            GetDependency<ISqliteNativeRuntime>(maintenanceFactory, "_nativeRuntime"));

        Assert.Same(SqliteNativeRuntime.Instance, provider.GetRequiredService<ISqliteNativeRuntime>());

        AssertSingleServingConnectionInterceptor(options);

    }

    [SkippableFact]
    public async Task Api_test_host_replacement_uses_the_host_singleton_ordinary_lifecycle()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        IServiceProvider provider = factory.Services;

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DbContextOptions<ArcanumDbContext> options = scope.ServiceProvider
            .GetRequiredService<DbContextOptions<ArcanumDbContext>>();

        CovenantConnectionEnrolmentInterceptor interceptor = Assert.Single(Interceptors(options));

        IGrimoireOrdinaryConnectionLifecycle lifecycle = provider
            .GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>();

        ICovenantConnectionDrain drain = provider.GetRequiredService<ICovenantConnectionDrain>();

        IGrimoireConnectionAdmissionGate admissionGate = provider
            .GetRequiredService<IGrimoireConnectionAdmissionGate>();

        IGrimoireOrdinaryConnectionFactory ordinaryFactory = provider
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        IGrimoireMaintenanceConnectionFactory maintenanceFactory = provider
            .GetRequiredService<IGrimoireMaintenanceConnectionFactory>();

        IGrimoireDbPassphraseSource passphraseSource = provider
            .GetRequiredService<IGrimoireDbPassphraseSource>();

        ICovenantSqliteConnectionInitializer initializer = provider
            .GetRequiredService<ICovenantSqliteConnectionInitializer>();

        Assert.Same(lifecycle, GetLifecycle(interceptor));

        Assert.Same(drain, GetDrain(interceptor));

        Assert.Same(drain, GetDrain(admissionGate));

        Assert.Same(drain, GetDrain(lifecycle));

        Assert.Same(lifecycle, GetLifecycle(ordinaryFactory));

        Assert.Same(drain, GetDrain(ordinaryFactory));

        Assert.Same(
            passphraseSource,
            GetDependency<IGrimoireDbPassphraseSource>(
                maintenanceFactory,
                "_passphraseSource"));

        Assert.Same(
            initializer,
            GetDependency<ICovenantSqliteConnectionInitializer>(
                maintenanceFactory,
                "_initializer"));

        Assert.Same(
            SqliteNativeRuntime.Instance,
            GetDependency<ISqliteNativeRuntime>(maintenanceFactory, "_nativeRuntime"));

        Assert.Same(SqliteNativeRuntime.Instance, provider.GetRequiredService<ISqliteNativeRuntime>());

        AssertSingleServingConnectionInterceptor(options);

    }

    /// <summary>
    /// The API suite's host shares one serving interceptor, the way the product host does.
    /// </summary>
    /// <remarks>
    /// The host registers the context with <c>AddDbContextPool</c>, which forces singleton options
    /// and therefore exactly one <c>CovenantConnectionEnrolmentInterceptor</c> - one lock, one
    /// lifecycle table, and pooled context and connection objects reused across requests, so a
    /// refused open's lifecycle state is inherited by whatever runs next on the same connection.
    /// A per-scope interceptor is a real composition too (the CLI's), but it is not the one the API
    /// suite is presented as exercising, and no test could see the difference: every existing check
    /// reads options from a single scope, where a fresh interceptor per scope looks identical.
    /// </remarks>
    [SkippableFact]
    public async Task Api_test_host_shares_one_serving_interceptor_across_scopes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        IServiceProvider provider = factory.Services;

        await using AsyncServiceScope first = provider.CreateAsyncScope();

        await using AsyncServiceScope second = provider.CreateAsyncScope();

        CovenantConnectionEnrolmentInterceptor firstInterceptor = Assert.Single(
            Interceptors(first.ServiceProvider
                .GetRequiredService<DbContextOptions<ArcanumDbContext>>()));

        CovenantConnectionEnrolmentInterceptor secondInterceptor = Assert.Single(
            Interceptors(second.ServiceProvider
                .GetRequiredService<DbContextOptions<ArcanumDbContext>>()));

        Assert.Same(firstInterceptor, secondInterceptor);

    }

    [Fact]
    public void Direct_DbContext_options_paths_are_named_non_serving_exemptions()
    {

        ProductionSource[] sources = [.. ProductionSourceInventory.Sources()];

        ProductionSource[] optionsPaths =
        [
            .. sources.Where(static source => BuildsArcanumDbContextOptions(source)),
        ];

        // The filter has to see the call sites this inventory is written about before its emptiness
        // assertion means anything. Both serving registrations break the configurator call across
        // lines and spell the argument `options`, and two of the three named exemptions build the
        // builder with a target-typed `new`, so a filter keyed to one exact spelling of each
        // statement selects none of them and the check below compares an empty list against itself.
        Assert.Contains(optionsPaths, static source => source.Is("ServiceCollectionExtensions.cs"));

        Assert.Contains(optionsPaths, static source => source.Is("ArcanumDbContextFactory.cs"));

        Assert.Contains(optionsPaths, static source => source.Is("ArcanumDbContext.cs"));

        Assert.Contains(
            optionsPaths,
            static source => source.Is("InstallationResetExistingGrimoire.cs"));

        List<string> unnamed =
        [
            .. optionsPaths
                .Where(static source => !InstallsTheServingConfigurator(source))
                .Where(static source => !IsNamedNonServingOptionsPath(source))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(unnamed);

        // The installation bootstrap has an intentionally separate raw connection: it runs before
        // serving/readiness and Task 5 owns its durable-owner handoff. It is named here so a later
        // options-path cleanup cannot quietly treat bootstrap as ordinary serving composition.
        Assert.Contains(
            sources,
            static source => source.Is("GrimoireDatabaseBootstrapper.cs")
                && source.Names("SqliteConnection connection = new("));

    }

    /// <summary>
    /// Every shape that builds <c>DbContextOptions&lt;ArcanumDbContext&gt;</c> in this repository.
    /// </summary>
    /// <remarks>
    /// Construct-level tokens rather than one exact spelling of a statement. The builder is written
    /// both as <c>new DbContextOptionsBuilder&lt;ArcanumDbContext&gt;()</c> and as a target-typed
    /// <c>= new()</c>, and both configurator call sites break the argument onto its own line - so a
    /// filter that matches a whole statement selects whichever site happened to be formatted that
    /// way when it was written, and goes quiet the first time one of them is reflowed.
    /// </remarks>
    private static bool BuildsArcanumDbContextOptions(ProductionSource source) =>
        source.Names("DbContextOptionsBuilder<ArcanumDbContext>")
        || InstallsTheServingConfigurator(source)
        || source.Names("ArcanumDbContextOptionsConfigurator.ConfigureNonServingFallback(")

        // The provider call itself, because the bypass this inventory exists to catch need not name
        // the builder type at all: `AddDbContext<ArcanumDbContext>((sp, o) => o.UseSqlite(...))`
        // configures serving options through the lambda's parameter and matches none of the tokens
        // above. That shape shipped past this test until it was added.
        || source.Names(".UseSqlite(");

    /// <summary>
    /// The serving composition: options built by the configurator that installs the interceptor.
    /// </summary>
    /// <remarks>
    /// The configurator's own file counts. It is where the serving provider call and the interceptor
    /// registration live, so it names its own method rather than calling it, and it is the one file
    /// that could not be an exemption without exempting serving composition itself.
    /// </remarks>
    private static bool InstallsTheServingConfigurator(ProductionSource source) =>
        source.Names("ArcanumDbContextOptionsConfigurator.Configure(")
        || source.Is("ArcanumDbContextOptionsConfigurator.cs");

    private static bool IsNamedNonServingOptionsPath(ProductionSource source) =>

        // `dotnet ef` design-time scaffolding against its temp-root scratch database.
        source.Is("ArcanumDbContextFactory.cs")

        // Manual/design-time fallback only; every serving registration supplies configured options.
        || source.Is("ArcanumDbContext.cs")

        // The authenticated stopped-host reset reader. It is unpooled, runs with no host gate, and
        // is not reachable while the serving process is live.
        || source.Is("InstallationResetExistingGrimoire.cs");

    private static CovenantConnectionEnrolmentInterceptor[] Interceptors(
        DbContextOptions<ArcanumDbContext> options)
    {

        CoreOptionsExtension core = options.FindExtension<CoreOptionsExtension>()
            ?? throw new InvalidOperationException("The EF Core options extension is missing.");

        return [.. core.Interceptors!.OfType<CovenantConnectionEnrolmentInterceptor>()];

    }

    private static void AssertSingleServingConnectionInterceptor(
        DbContextOptions<ArcanumDbContext> options)
    {

        CoreOptionsExtension core = options.FindExtension<CoreOptionsExtension>()
            ?? throw new InvalidOperationException("The EF Core options extension is missing.");

        DbConnectionInterceptor interceptor = Assert.Single(
            core.Interceptors!.OfType<DbConnectionInterceptor>());

        _ = Assert.IsType<CovenantConnectionEnrolmentInterceptor>(interceptor);

    }

    private static IGrimoireOrdinaryConnectionLifecycle GetLifecycle(
        CovenantConnectionEnrolmentInterceptor interceptor)
    {

        FieldInfo field = typeof(CovenantConnectionEnrolmentInterceptor).GetField(
            "_lifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The interceptor lifecycle field is missing.");

        return Assert.IsAssignableFrom<IGrimoireOrdinaryConnectionLifecycle>(field.GetValue(interceptor));

    }

    private static ICovenantConnectionDrain GetDrain(
        CovenantConnectionEnrolmentInterceptor interceptor)
    {

        FieldInfo field = typeof(CovenantConnectionEnrolmentInterceptor).GetField(
            "_drain",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The interceptor drain field is missing.");

        return Assert.IsAssignableFrom<ICovenantConnectionDrain>(field.GetValue(interceptor));

    }

    private static ICovenantConnectionDrain GetDrain(
        IGrimoireConnectionAdmissionGate gate)
    {

        FieldInfo field = typeof(GrimoireConnectionAdmissionGate).GetField(
            "_drain",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The admission gate drain field is missing.");

        return Assert.IsAssignableFrom<ICovenantConnectionDrain>(field.GetValue(gate));

    }

    private static IGrimoireOrdinaryConnectionLifecycle GetLifecycle(
        IGrimoireOrdinaryConnectionFactory factory)
    {

        FieldInfo field = typeof(GrimoireOrdinaryConnectionFactory).GetField(
            "_lifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The ordinary factory lifecycle field is missing.");

        return Assert.IsAssignableFrom<IGrimoireOrdinaryConnectionLifecycle>(field.GetValue(factory));

    }

    private static ICovenantConnectionDrain GetDrain(
        IGrimoireOrdinaryConnectionFactory factory)
    {

        FieldInfo field = typeof(GrimoireOrdinaryConnectionFactory).GetField(
            "_drain",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The ordinary factory drain field is missing.");

        return Assert.IsAssignableFrom<ICovenantConnectionDrain>(field.GetValue(factory));

    }

    private static ICovenantConnectionDrain GetDrain(
        IGrimoireOrdinaryConnectionLifecycle lifecycle)
    {

        FieldInfo field = typeof(GrimoireOrdinaryConnectionLifecycle).GetField(
            "_drain",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The lifecycle drain field is missing.");

        return Assert.IsAssignableFrom<ICovenantConnectionDrain>(field.GetValue(lifecycle));

    }

    private static T GetDependency<T>(object instance, string fieldName)
    {

        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"The {instance.GetType().Name} field '{fieldName}' is missing.");

        return Assert.IsAssignableFrom<T>(field.GetValue(instance));

    }

    public enum ProductComposition
    {

        NonPooledCli = 1,

        PooledHost = 2,

    }

}
