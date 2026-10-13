using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The widened SQLite indexer certificate (DbDataReader.this[]) names a container-built DbContext
// whose final provider is closed. These theories pin the routes by which EF would still run
// authored code or change the provider after the registration was read: OnConfiguring at the first
// use of the context's services, authored interceptors and logger factories, framework-looking
// service-collection helpers, and the reviewed builder vocabulary an unconfigured context's
// OnConfiguring fallback may use. Source analysis only; nothing here executes EF, SQL or a provider.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string SqliteIndexerCallee = "System.Data.Common.DbDataReader.this[]";

    private const string SqliteIndexerUseSqlite = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, \"Data Source=:memory:\")";

    private const string SqliteIndexerDeleteFallback = "if (builder.IsConfigured) { return; } System.IO.File.Delete(\"on-configuring\");";

    private const string SqliteIndexerBuilderMembers = "Microsoft.EntityFrameworkCore.DbContextOptionsBuilder.";

    [Theory]
    [InlineData("explicit-set", true, false)]
    [InlineData("explicit-database", true, false)]
    [InlineData("this-database", true, false)]
    [InlineData("implicit-property", true, false)]
    [InlineData("implicit-method", true, false)]
    [InlineData("implicit-receiver", true, false)]
    [InlineData("conditional-property", true, false)]
    [InlineData("conditional-method", true, false)]
    [InlineData("accessor-extension", true, false)]
    [InlineData("delegate-dispatch", true, false)]
    [InlineData("interface-dispatch", false, false)]
    [InlineData("created-closed-guard", true, false)]
    [InlineData("admitted-implicit-property", false, false)]
    [InlineData("container-closed-guard", false, true)]
    public void SqliteBlobIndexerOnConfiguringRunsAtEveryDbContextUse(string shape, bool unadmittedEffect, bool database)
    {
        string touch = shape switch
        {
            "this-database" => "public void Touch() { _ = this.Database; }",
            "implicit-method" => "public void Touch() { _ = Set<BlobRow>(); }",
            "implicit-receiver" => "public void Touch() { _ = Database.CanConnect(); }",
            "implicit-property" or "delegate-dispatch" or "interface-dispatch" or "created-closed-guard" or "container-closed-guard" or "admitted-implicit-property" =>
                "public void Touch() { _ = Database; }",
            _ => string.Empty,
        };

        string early = shape switch
        {
            "explicit-set" => "_ = db.Set<BlobRow>(); ",
            "explicit-database" => "_ = db.Database; ",
            "conditional-property" => "_ = db?.Database; ",
            "conditional-method" => "_ = db?.Set<BlobRow>(); ",
            "accessor-extension" => "_ = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(db); ",
            "delegate-dispatch" => "System.Action touch = db.Touch; touch(); ",
            "interface-dispatch" => "IBlobTouch touch = db; touch.Touch(); ",
            "created-closed-guard" => "BlobContext created = new(BlobInput.Options); created.Touch(); ",
            "admitted-implicit-property" => string.Empty,
            _ => "db.Touch(); ",
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(
            SqliteIndexerSource(
                SqliteIndexerUseSqlite,
                registration: string.Empty,
                contextBases: shape == "interface-dispatch" ? ", IBlobTouch" : string.Empty,
                contextMembers: touch,
                onConfiguring: shape is "created-closed-guard" or "container-closed-guard"
                    ? SqliteIndexerDeleteFallback
                    : "System.IO.File.Delete(\"on-configuring\");",
                early: early,
                pipelinePrefix: shape == "admitted-implicit-property" ? "db.Touch(); " : string.Empty,
                contextExpression: shape == "created-closed-guard" ? "created" : "db",
                helpers: "public interface IBlobTouch { void Touch(); }"));

        Assert.True(IsSqliteIndexerReached(result));

        Assert.Equal(database, IsSqliteIndexerClassified(result));

        HostedProducerSite[] deletes = result.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

        if (shape == "interface-dispatch")
        {
            // An interface call is not traversed into the context's own member: it is refused, so
            // the effect of the OnConfiguring that member would trigger is never silently skipped.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED" && diagnostic.Detail == "IBlobTouch.Touch");

            return;
        }

        if (shape == "container-closed-guard")
        {
            // The registration configured the container-built instance, so the guard returns before the
            // fallback and the effect cannot run at any use.
            Assert.Empty(deletes);

            return;
        }

        Assert.NotEmpty(deletes);

        bool unadmitted = deletes.Any(static site => site.WorkFrontierCapsuleId is null)
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");

        Assert.Equal(unadmittedEffect, unadmitted);
    }

    [Theory]
    [InlineData("reviewed-settings", true)]
    [InlineData("framework-helpers", true)]
    [InlineData("interceptor-lifecycle", true)]
    [InlineData("interceptor-command", false)]
    [InlineData("interceptor-save-changes", false)]
    [InlineData("interceptor-created-override", false)]
    [InlineData("interceptor-descriptor", false)]
    [InlineData("logger-factory", false)]
    [InlineData("microsoft-lookalike-helper", false)]
    [InlineData("add-dbcontext-lookalike", false)]
    public void SqliteBlobIndexerRefusesUnreviewedEfCallbacksAndLookalikeRegistrations(string shape, bool database)
    {
        string options = shape switch
        {
            "reviewed-settings" => SqliteIndexerUseSqlite + ".EnableDetailedErrors().EnableSensitiveDataLogging()",
            "interceptor-lifecycle" => SqliteIndexerUseSqlite + ".AddInterceptors(new LifecycleInterceptor())",
            "interceptor-command" => SqliteIndexerUseSqlite + ".AddInterceptors(new CommandInterceptor())",
            "interceptor-save-changes" => SqliteIndexerUseSqlite + ".AddInterceptors(new SavingInterceptor())",
            "interceptor-created-override" => SqliteIndexerUseSqlite + ".AddInterceptors(new CreatedOverrideInterceptor())",
            "logger-factory" => SqliteIndexerUseSqlite + ".UseLoggerFactory(new Microsoft.Extensions.Logging.LoggerFactory())",
            _ => SqliteIndexerUseSqlite,
        };

        string registration = shape switch
        {
            "framework-helpers" => "services.AddLogging(); services.AddOptions(); ",
            "interceptor-descriptor" => "services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor, LifecycleInterceptor>(); ",
            "microsoft-lookalike-helper" => "Microsoft.Extensions.Lookalike.LookalikeRegistration.AddAll(services); ",
            "add-dbcontext-lookalike" => "services.AddDbContext<BlobContext>(\"competing\"); ",
            _ => string.Empty,
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(
            SqliteIndexerSource(
                options,
                registration,
                contextBases: string.Empty,
                contextMembers: string.Empty,
                onConfiguring: null,
                early: string.Empty,
                pipelinePrefix: string.Empty,
                contextExpression: "db",
                helpers: SqliteIndexerEfCallbackHelpers),
            shape == "microsoft-lookalike-helper"
                ? [EmitSqliteIndexerLookalike(
                    "Microsoft.Extensions.Lookalike",
                    "namespace Microsoft.Extensions.Lookalike { public static class LookalikeRegistration { public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAll(Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services; } }")]
                : []);

        Assert.True(IsSqliteIndexerReached(result));

        Assert.Equal(database, IsSqliteIndexerClassified(result));

        Assert.Equal(!database, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == SqliteIndexerCallee));
    }

    [Theory]
    [InlineData("reviewed-members")]
    [InlineData("unreviewed-provider-member")]
    [InlineData("lookalike-builder")]
    public void CallerCreatedContextFallbackClassifiesOnlyExactProviderNeutralBuilderMembers(string shape)
    {
        string fallback = shape == "unreviewed-provider-member"
            ? "builder.UseInternalServiceProvider(BlobInput.Provider);"
            : "_ = builder.AddInterceptors(new LifecycleInterceptor()).UseModel(BlobInput.Model);";

        const string lookalikeUse = "lookalike::Microsoft.EntityFrameworkCore.DbContextOptionsBuilder fake = null!; _ = fake.IsConfigured; _ = fake.AddInterceptors(); _ = fake.UseModel(null!); ";

        string source = SqliteIndexerSource(
            SqliteIndexerUseSqlite,
            registration: string.Empty,
            contextBases: string.Empty,
            contextMembers: string.Empty,
            onConfiguring: "if (builder.IsConfigured) { return; } " + fallback,
            early: shape == "lookalike-builder" ? lookalikeUse : string.Empty,
            pipelinePrefix: "BlobContext created = new(BlobInput.Options); _ = created.Database; ",
            contextExpression: "db",
            helpers: SqliteIndexerEfCallbackHelpers);

        if (shape == "lookalike-builder")
        {
            source = "extern alias lookalike;\n" + source;
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSqliteIndexerProbe(
            source,
            shape == "lookalike-builder"
                ? [EmitSqliteIndexerLookalike(
                    "Microsoft.EntityFrameworkCore.Lookalike",
                    "namespace Microsoft.EntityFrameworkCore { public class DbContextOptionsBuilder { public bool IsConfigured => true; public DbContextOptionsBuilder AddInterceptors(params object[] interceptors) => this; public DbContextOptionsBuilder UseModel(object model) => this; } }",
                    "lookalike")]
                : []);

        Assert.True(IsSqliteIndexerReached(result));

        // The unconfigured caller-created context really runs its fallback at the first use of
        // `created.Database`, so the members it calls are judged rather than skipped.
        string[] builderMembers = [SqliteIndexerBuilderMembers + "IsConfigured", SqliteIndexerBuilderMembers + "AddInterceptors", SqliteIndexerBuilderMembers + "UseModel"];

        string[] refusedAt = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && builderMembers.Contains(diagnostic.Detail))
            .Select(static diagnostic => diagnostic.Identity)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] lookalikeAt = shape == "lookalike-builder"
            ? new[] { "fake.IsConfigured", "fake.AddInterceptors()", "fake.UseModel(null!)" }
                .Select(use => "src/Fixture.cs:" + source.IndexOf(use, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

        // Exact identity only: the reviewed members of the real EF builder are classified, a
        // same-named type from another assembly is not, and no other builder member is.
        Assert.Equal(lookalikeAt, refusedAt);

        Assert.Equal(
            shape == "unreviewed-provider-member",
            result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == SqliteIndexerBuilderMembers + "UseInternalServiceProvider"));
    }

    private const string SqliteIndexerEfCallbackHelpers = """
        public sealed class LifecycleInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData) { }
        }

        public sealed class CommandInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
            {
                System.IO.File.Delete("command-interceptor");

                return result;
            }
        }

        public sealed class SavingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
        {
            public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
            {
                System.IO.File.Delete("saving-interceptor");

                return result;
            }
        }

        public sealed class CreatedOverrideInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override System.Data.Common.DbConnection ConnectionCreated(Microsoft.EntityFrameworkCore.Diagnostics.ConnectionCreatedEventData eventData, System.Data.Common.DbConnection result) => BlobInput.Connection;
        }

        public static class CompetingRegistrations
        {
            public static IServiceCollection AddDbContext<T>(this IServiceCollection services, string name) where T : class
            {
                services.AddScoped<T>(sp => (T)(object)new BlobContext(BlobInput.Options));

                return services;
            }
        }
        """;

    private static string SqliteIndexerSource(
        string options,
        string registration,
        string contextBases,
        string contextMembers,
        string? onConfiguring,
        string early,
        string pipelinePrefix,
        string contextExpression,
        string helpers)
    {
        string onConfiguringMember = onConfiguring is null
            ? string.Empty
            : "protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { " + onConfiguring + " }";

        string body = pipelinePrefix
            + "System.Data.Common.DbConnection connection = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database); "
            + "await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(token); "
            + "await using System.Data.Common.DbCommand command = connection.CreateCommand(); command.Transaction = transaction; "
            + "System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); "
            + "_ = reader[0]; await reader.DisposeAsync();";

        string types = $$"""
            public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(options){{contextBases}}
            {
                {{onConfiguringMember}}
                {{contextMembers}}
            }

            public sealed class BlobRow { public int Id { get; set; } }

            internal static class BlobInput
            {
                internal static Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> Options { get; set; } = null!;
                internal static System.Data.Common.DbConnection Connection { get; set; } = null!;
                internal static IServiceProvider Provider { get; set; } = null!;
                internal static Microsoft.EntityFrameworkCore.Metadata.IModel Model { get; set; } = null!;
            }

            internal static class BlobPipeline
            {
                internal static async Task RunAsync(BlobContext db, CancellationToken token) { {{body}} }
            }
            {{helpers}}
            """;

        return RegistrationSource("services.AddDbContext<BlobContext>((sp, options) => { " + options + "; }); " + registration + "services.AddHostedService<Worker>();")
            .Replace("public class Worker : IHostedService", "public class Worker(BlobContext db) : IHostedService", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", string.Empty, StringComparison.Ordinal)
            .Replace(
                "public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "public async Task StartAsync(CancellationToken token) { _ = db; " + early + R2Admission + "await BlobPipeline.RunAsync(" + contextExpression + ", token); }",
                StringComparison.Ordinal)
            + AdmissionTypes + types;
    }

    private HostedProducerDiscovery<HostedProducerSite> DiscoverSqliteIndexerProbe(string source, IReadOnlyList<MetadataReference> references)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api").AddReferences(references);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine("SQLITE-INDEXER diag " + diagnostic.Code + " " + diagnostic.Identity + " " + diagnostic.Detail);
        }

        return result;
    }

    private HostedProducerDiscovery<HostedProducerSite> DiscoverSqliteIndexerProbe(string source) =>
        DiscoverSqliteIndexerProbe(source, []);

    private static bool IsSqliteIndexerReached(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Items.Any(static site => site.Callee == SqliteIndexerCallee)
        || result.Diagnostics.Any(static diagnostic => diagnostic.Detail == SqliteIndexerCallee);

    private static bool IsSqliteIndexerClassified(HostedProducerDiscovery<HostedProducerSite> result) =>
        result.Items.Any(static site => site.Kind == HostedProducerSiteKind.DatabaseAccess && site.Callee == SqliteIndexerCallee);

    // A metadata assembly the fixture can call but the analysis can neither read nor trust: it is
    // named like a framework assembly but is not the signed assembly the test host loads.
    private static PortableExecutableReference EmitSqliteIndexerLookalike(string assemblyName, string source, string? alias = null)
    {
        CSharpCompilation lookalike = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            HostedGrimoireProducerInventory.ProductionCompilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api").References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using MemoryStream image = new();

        Assert.True(lookalike.Emit(image).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(image.ToArray());

        return alias is null ? reference : reference.WithAliases([alias]);
    }
}
