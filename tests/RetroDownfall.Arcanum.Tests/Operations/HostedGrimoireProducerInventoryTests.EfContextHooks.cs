using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The EfContextHooks fixtures. EF runs authored code of a DbContext on its own schedule: OnConfiguring and the model hooks (OnModelCreating,
// ConfigureConventions) at the first use of the context's services, and the constructor and the field and property
// initializers when the context is activated. The first use that really triggers them is not the read of a DbSet or of
// Database, so they run with no work, effect or recovery admission inherited. These fixtures pin that every authored
// hook is traversed at the use, that a receiver whose provenance cannot be followed does not hide it, and that
// nothing the registration or the options prove closed is traversed.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string HookDelete = "System.IO.File.Delete(\"context-hook\"); ";

    private const string HookRows = "public Microsoft.EntityFrameworkCore.DbSet<BlobRow> Rows => Set<BlobRow>(); ";

    private const string HookOpaqueContext = "internal static class HookOpaque { internal static BlobContext Context { get; set; } = null!; internal static BlobContext Field = null!; }";

    // The pipeline is removed so that nothing but the use under test touches the context: a hook effect can then
    // appear only if that use enters it.
    private HostedProducerDiscovery<HostedProducerSite> HookDiscover(
        string options,
        string early,
        string contextMembers = "",
        string? onConfiguring = null,
        string registration = "",
        string helpers = "",
        string contextBases = "",
        string? contextDeclaration = null,
        string admitted = "",
        bool production = false)
    {
        string source = SqliteIndexerSource(
            options,
            registration,
            contextBases,
            contextMembers,
            onConfiguring,
            early,
            string.Empty,
            contextExpression: "db",
            helpers: helpers);

        const string pipelineCall = "await BlobPipeline.RunAsync(db, token); }";

        Assert.Contains(pipelineCall, source, StringComparison.Ordinal);

        // `admitted` runs inside the admitted region; `early` runs before it.
        source = source.Replace(pipelineCall, admitted + "}", StringComparison.Ordinal);

        if (contextDeclaration is not null)
        {
            source = source.Replace(
                "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(options)" + contextBases,
                contextDeclaration,
                StringComparison.Ordinal);
        }

        return DiscoverSqliteIndexerProbe(source, [], production);
    }

    private static HostedProducerSite[] HookDeletes(HostedProducerDiscovery<HostedProducerSite> result) =>
        [.. result.Items.Where(static site => site.Callee == "System.IO.File.Delete")];

    // The hook runs at the use, with no admission: the site carries no frontier and the diagnostics name it.
    private void AssertHookTraversed(HostedProducerDiscovery<HostedProducerSite> result, string step)
    {
        HostedProducerSite[] deletes = HookDeletes(result);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics.Where(static diagnostic => diagnostic.Detail == "System.IO.File.Delete"))
        {
            output.WriteLine("HOOK diag " + diagnostic.Code + " " + diagnostic.Identity);
        }

        Assert.NotEmpty(deletes);

        Assert.All(deletes, static site => Assert.Null(site.WorkFrontierCapsuleId));

        // The missing frontier names the path the traversal took to the site, so it names the hook step.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete"
            && diagnostic.Identity.Contains(step, StringComparison.Ordinal));
    }

    // ---- A receiver whose provenance cannot be followed does not hide the hook.

    [Theory]
    [InlineData("static-property-set", "_ = HookOpaque.Context.Set<BlobRow>(); ")]
    [InlineData("static-property-database", "_ = HookOpaque.Context.Database; ")]
    [InlineData("static-property-dbset", "_ = HookOpaque.Context.Rows; ")]
    [InlineData("static-field", "_ = HookOpaque.Field.Set<BlobRow>(); ")]
    [InlineData("conditional-access", "_ = HookOpaque.Context?.Set<BlobRow>(); ")]
    [InlineData("generic-parameter", "HookGeneric.Use(HookOpaque.Context); ")]
    [InlineData("unsealed-base-parameter", "HookGeneric.UseBase(HookOpaque.Context); ")]
    [InlineData("framework-context-parameter", "HookGeneric.UseFramework(HookOpaque.Context); ")]
    public void AnOpaqueContextReceiverStillRunsTheAuthoredOnConfiguring(string shape, string early)
    {
        output.WriteLine("HOOK " + shape);

        string helpers = HookOpaqueContext
            + " internal static class HookGeneric { internal static void Use<T>(T context) where T : Microsoft.EntityFrameworkCore.DbContext { _ = context.Set<BlobRow>(); } "
            + "internal static void UseBase(Microsoft.EntityFrameworkCore.DbContext context) { _ = context.Database; } "
            + "internal static void UseFramework(object context) { _ = ((Microsoft.EntityFrameworkCore.DbContext)context).Set<BlobRow>(); } }";

        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            early,
            contextMembers: HookRows,
            onConfiguring: HookDelete,
            helpers: helpers);

        // The hook is entered through whichever candidate contexts the static type allows, and is unadmitted.
        AssertHookTraversed(result, "/call@");
    }

    [Fact]
    public void AnOpaqueReceiverOfAContextWithNoAuthoredHookRunsNothing()
    {
        // The twin: the context declares no OnConfiguring, so there is no authored code behind the same opaque use.
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = HookOpaque.Context.Set<BlobRow>(); ",
            helpers: HookOpaqueContext);

        Assert.Empty(HookDeletes(result));
    }

    [Fact]
    public void AnOpaqueReceiverOfAnAuthoredLookalikeWithoutTheFrameworkContextRunsNothing()
    {
        // A type that merely has a Set method is not a DbContext, so it is no first use of EF's services.
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = HookLookalike.Value.Set<BlobRow>(); ",
            onConfiguring: HookDelete,
            helpers: "internal sealed class HookLookalikeType { internal object Set<T>() => new(); } internal static class HookLookalike { internal static HookLookalikeType Value { get; set; } = null!; }");

        Assert.Empty(HookDeletes(result));
    }

    [Fact]
    public void AnUnsealedContextReceiverRunsTheOnConfiguringOfEveryAuthoredSubclass()
    {
        // The receiver is typed as the unsealed base. Either subclass may hold the instance, so both are judged.
        const string hierarchy = "public abstract class HookBase(Microsoft.EntityFrameworkCore.DbContextOptions options) : Microsoft.EntityFrameworkCore.DbContext(options) { protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { System.IO.File.Delete(\"base-hook\"); } } "
            + "public sealed class HookLeft(Microsoft.EntityFrameworkCore.DbContextOptions options) : HookBase(options) { protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { System.IO.File.Delete(\"left-hook\"); } } "
            + "public sealed class HookRight(Microsoft.EntityFrameworkCore.DbContextOptions options) : HookBase(options) { protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { System.IO.File.Delete(\"right-hook\"); } } "
            + "internal static class HookBaseInput { internal static HookBase Value { get; set; } = null!; }";

        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = HookBaseInput.Value.Database; ",
            helpers: hierarchy);

        foreach (HostedProducerSite site in HookDeletes(result))
        {
            output.WriteLine("HOOK site " + site.OperationId + " " + site.Member + " " + site.EnclosingType);
        }

        Assert.Equal(2, HookDeletes(result).Length);
    }

    // ---- OnModelCreating and ConfigureConventions run at the first model use unless a compiled model is supplied.

    private const string HookModelCreating = "protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder builder) { " + HookDelete + "}";

    private const string HookConventions = "protected override void ConfigureConventions(Microsoft.EntityFrameworkCore.ModelConfigurationBuilder builder) { " + HookDelete + "}";

    [Theory]
    [InlineData("on-model-creating", HookModelCreating)]
    [InlineData("configure-conventions", HookConventions)]
    public void TheModelHooksRunAtTheFirstUseWhenNoCompiledModelIsSupplied(string hook, string members)
    {
        output.WriteLine("HOOK " + hook);

        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = db.Set<BlobRow>(); ",
            contextMembers: members);

        AssertHookTraversed(result, "/model@");
    }

    [Theory]
    [InlineData("direct", ".UseModel(BlobInput.Model)")]
    [InlineData("chained-settings", ".EnableDetailedErrors().UseModel(BlobInput.Model).EnableSensitiveDataLogging()")]
    [InlineData("helper", "; HookRegistrationHelper.Supply(options)")]
    public void AModelSuppliedByTheRegistrationMeansTheModelHooksCannotRun(string shape, string options)
    {
        output.WriteLine("HOOK " + shape);

        // The compiled model is always supplied, so neither hook can run: EF does not build a model it was given.
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite + options,
            "_ = db.Set<BlobRow>(); ",
            contextMembers: HookModelCreating + HookConventions,
            helpers: "internal static class HookRegistrationHelper { internal static void Supply(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder builder) { builder.UseModel(BlobInput.Model); } }");

        Assert.Empty(HookDeletes(result));
    }

    [Theory]
    [InlineData("conditional", "; if (BlobInput.Model is null) { options.UseModel(BlobInput.Model); }", "")]
    [InlineData("null-argument", "; options.UseModel(null!)", "")]
    [InlineData("other-registration-lacks-it", ".UseModel(BlobInput.Model)", "services.AddDbContext<BlobContext>((sp, other) => { Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(other, \"Data Source=:memory:\"); }); ")]
    public void AModelThatIsNotAlwaysSuppliedStillRunsTheModelHooks(string shape, string options, string registration)
    {
        output.WriteLine("HOOK " + shape);

        // A conditional UseModel, a null model and a second registration that omits it can each leave EF to build
        // the model from the hook, so the hook is still judged.
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite + options,
            "_ = db.Set<BlobRow>(); ",
            contextMembers: HookModelCreating,
            registration: registration);

        AssertHookTraversed(result, "/model@");
    }

    [Fact]
    public void ACreatedContextWhoseOptionsSupplyTheModelRunsNoModelHook()
    {
        HostedProducerDiscovery<HostedProducerSite> supplied = HookDiscover(
            SqliteIndexerUseSqlite,
            "BlobContext created = new(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\").UseModel(BlobInput.Model).Options); _ = created.Set<BlobRow>(); ",
            contextMembers: HookModelCreating);

        Assert.DoesNotContain(supplied.Diagnostics, static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> unsupplied = HookDiscover(
            SqliteIndexerUseSqlite,
            "BlobContext created = new(Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\").Options); _ = created.Set<BlobRow>(); ",
            contextMembers: HookModelCreating);

        Assert.Contains(unsupplied.Diagnostics, static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));
    }

    private const string HookLocalBuilder = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\")";

    [Theory]
    [InlineData("initializer-only", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; BlobContext created = new(options); _ = created.Set<BlobRow>(); ", false)]
    [InlineData("every-assignment-supplies-it", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; BlobContext created = new(options); _ = created.Set<BlobRow>(); ", false)]
    [InlineData("one-assignment-omits-it", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; options = " + HookLocalBuilder + ".Options; BlobContext created = new(options); _ = created.Set<BlobRow>(); ", true)]
    [InlineData("assigned-from-an-opaque-value", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; options = HookOptionsInput.Value; BlobContext created = new(options); _ = created.Set<BlobRow>(); ", true)]
    [InlineData("captured-and-rewritten", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; System.Action change = () => options = HookOptionsInput.Value; change(); BlobContext created = new(options); _ = created.Set<BlobRow>(); ", true)]
    [InlineData("passed-by-reference", "var options = " + HookLocalBuilder + ".UseModel(BlobInput.Model).Options; HookOptionsInput.Replace(ref options); BlobContext created = new(options); _ = created.Set<BlobRow>(); ", true)]
    public void ALocalHoldingTheOptionsOfACreatedContextIsFollowedThroughItsAssignments(string shape, string early, bool hookRuns)
    {
        output.WriteLine("HOOK " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            early,
            contextMembers: HookModelCreating,
            helpers: "internal static class HookOptionsInput { internal static Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> Value { get; set; } = null!; internal static void Replace(ref Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) { options = Value; } }");

        Assert.Equal(hookRuns, result.Diagnostics.Any(static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal)));
    }

    // ---- The constructor and the field and property initializers run when the context is activated.

    [Theory]
    [InlineData("constructor-body", "public BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : base(options) { System.IO.File.Delete(\"context-hook\"); }", true)]
    [InlineData("field-initializer", "private readonly int hookField = HookInit(); private static int HookInit() { System.IO.File.Delete(\"context-hook\"); return 1; }", false)]
    [InlineData("property-initializer", "public int HookProperty { get; } = HookInit(); private static int HookInit() { System.IO.File.Delete(\"context-hook\"); return 1; }", false)]
    [InlineData("nested-allocation-in-initializer", "private readonly object hookOwner = new HookOwner(); private sealed class HookOwner { public HookOwner() { System.IO.File.Delete(\"context-hook\"); } }", false)]
    public void TheContextActivationRunsItsConstructorAndInitializersUnadmitted(string shape, string members, bool explicitConstructor)
    {
        output.WriteLine("HOOK " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = db.Set<BlobRow>(); ",
            contextMembers: members,
            contextDeclaration: explicitConstructor ? "public sealed class BlobContext : Microsoft.EntityFrameworkCore.DbContext" : null);

        AssertHookTraversed(result, "/activate@");
    }

    [Fact]
    public void ACreatedContextRunsItsInitializersAtTheCreationUnderTheFrontierLiveThere()
    {
        const string members = "private readonly int hookField = HookInit(); private static int HookInit() { System.IO.File.Delete(\"context-hook\"); return 1; }";

        // The creation inside the admitted region: the initializer runs there, under the frontiers held there.
        HostedProducerDiscovery<HostedProducerSite> admitted = HookDiscover(
            SqliteIndexerUseSqlite,
            string.Empty,
            contextMembers: members,
            admitted: "BlobContext created = new(BlobInput.Options); _ = created; ");

        Assert.NotEmpty(HookDeletes(admitted));

        Assert.All(HookDeletes(admitted), static site => Assert.NotNull(site.WorkFrontierCapsuleId));

        // The same creation outside it is the missing frontier, not a hidden one.
        HostedProducerDiscovery<HostedProducerSite> unadmitted = HookDiscover(
            SqliteIndexerUseSqlite,
            "BlobContext created = new(BlobInput.Options); _ = created; ",
            contextMembers: members);

        Assert.NotEmpty(HookDeletes(unadmitted));

        Assert.All(HookDeletes(unadmitted), static site => Assert.Null(site.WorkFrontierCapsuleId));
    }

    [Fact]
    public void AContextWithNoAuthoredActivationOrHookRunsNothing()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite,
            "_ = db.Set<BlobRow>(); _ = db.Database; ");

        Assert.Empty(HookDeletes(result));
    }
}
