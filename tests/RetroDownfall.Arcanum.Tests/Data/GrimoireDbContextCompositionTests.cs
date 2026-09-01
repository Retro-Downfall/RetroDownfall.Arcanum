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
    public async Task Product_DbContext_options_use_the_singleton_admission_gate_and_drain(
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

        IGrimoireConnectionAdmissionGate admission = provider
            .GetRequiredService<IGrimoireConnectionAdmissionGate>();

        ICovenantConnectionDrain drain = provider
            .GetRequiredService<ICovenantConnectionDrain>();

        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        Assert.Same(
            admission,
            firstScope.ServiceProvider.GetRequiredService<IGrimoireConnectionAdmissionGate>());

        Assert.Same(
            admission,
            secondScope.ServiceProvider.GetRequiredService<IGrimoireConnectionAdmissionGate>());

        Assert.Same(
            drain,
            firstScope.ServiceProvider.GetRequiredService<ICovenantConnectionDrain>());

        Assert.Same(
            drain,
            secondScope.ServiceProvider.GetRequiredService<ICovenantConnectionDrain>());

        DbContextOptions<ArcanumDbContext> options = firstScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ArcanumDbContext>>();

        CovenantConnectionEnrolmentInterceptor interceptor = Assert.Single(Interceptors(options));

        Assert.Same(admission, Dependency<IGrimoireConnectionAdmissionGate>(interceptor, "_admissionGate"));

        Assert.Same(drain, Dependency<ICovenantConnectionDrain>(interceptor, "_drain"));

        AssertSingleServingConnectionInterceptor(options);

    }

    [SkippableFact]
    public async Task Api_test_host_replacement_uses_the_host_singleton_admission_gate_and_drain()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        IServiceProvider provider = factory.Services;

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DbContextOptions<ArcanumDbContext> options = scope.ServiceProvider
            .GetRequiredService<DbContextOptions<ArcanumDbContext>>();

        CovenantConnectionEnrolmentInterceptor interceptor = Assert.Single(Interceptors(options));

        Assert.Same(
            provider.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
            Dependency<IGrimoireConnectionAdmissionGate>(interceptor, "_admissionGate"));

        Assert.Same(
            provider.GetRequiredService<ICovenantConnectionDrain>(),
            Dependency<ICovenantConnectionDrain>(interceptor, "_drain"));

        AssertSingleServingConnectionInterceptor(options);

    }

    [Fact]
    public void Direct_DbContext_options_paths_are_named_non_serving_exemptions()
    {

        ProductionSource[] sources = [.. ProductionSourceInventory.Sources()];

        List<string> unnamed =
        [
            .. sources
                .Where(static source =>
                    source.Names("new DbContextOptionsBuilder<ArcanumDbContext>")
                    || source.Names("ArcanumDbContextOptionsConfigurator.Configure(optionsBuilder"))
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

    private static T Dependency<T>(
        CovenantConnectionEnrolmentInterceptor interceptor,
        string fieldName)
        where T : class
    {

        FieldInfo field = typeof(CovenantConnectionEnrolmentInterceptor).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The interceptor field {fieldName} is missing.");

        return Assert.IsAssignableFrom<T>(field.GetValue(interceptor));

    }

    public enum ProductComposition
    {

        NonPooledCli = 1,

        PooledHost = 2,

    }

}
