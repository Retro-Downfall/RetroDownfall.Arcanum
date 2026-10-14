using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// A context nobody can follow to its source is still an instance of a type the program can make, and authored code makes
// one in exactly two ways: the container builds it from the registrations of its type, and an allocation in analysed
// source builds it from options that source supplies. What EF is given (a compiled model, the interceptors) is bounded by
// the union of the two, so a receiver of unknown provenance is judged by that union rather than assumed to be anything.
// These fixtures pin the union, the twin that must stay refused for each way it can fail to be closed, and the shipped
// universe. Source analysis only; nothing here executes EF, SQL or a provider.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string UniverseBuilder = "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BlobContext>(), \"Data Source=:memory:\")";

    private const string UniverseOpaque = "_ = HookOpaque.Context.Set<BlobRow>(); ";

    private bool UniverseModelHookRuns(string registrationOptions, string helpers, string? contextDeclaration = null)
    {
        HostedProducerDiscovery<HostedProducerSite> result = HookDiscover(
            SqliteIndexerUseSqlite + registrationOptions,
            UniverseOpaque,
            contextMembers: HookModelCreating,
            helpers: HookOpaqueContext + helpers,
            contextDeclaration: contextDeclaration);

        return result.Diagnostics.Any(static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("registration-supplies-it", ".UseModel(BlobInput.Model)", "", false)]
    [InlineData("registration-lacks-it", "", "", true)]
    [InlineData("allocation-also-supplies-it", ".UseModel(BlobInput.Model)", "public static class UniverseMaker { public static BlobContext Make() => new(" + UniverseBuilder + ".UseModel(BlobInput.Model).Options); }", false)]
    [InlineData("allocation-lacks-it", ".UseModel(BlobInput.Model)", "public static class UniverseMaker { public static BlobContext Make() => new(" + UniverseBuilder + ".Options); }", true)]
    [InlineData("target-typed-allocation-lacks-it", ".UseModel(BlobInput.Model)", "public static class UniverseMaker { public static BlobContext Make() { BlobContext made = new(" + UniverseBuilder + ".Options); return made; } }", true)]
    [InlineData("allocation-options-cannot-be-followed", ".UseModel(BlobInput.Model)", "public static class UniverseMaker { public static BlobContext Make(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> supplied) => new(supplied); }", true)]
    [InlineData("allocation-in-a-field-initializer", ".UseModel(BlobInput.Model)", "public static class UniverseMaker { public static readonly BlobContext Made = new(" + UniverseBuilder + ".Options); }", true)]
    public void AReceiverOfUnknownProvenanceIsJudgedByEveryWayItsTypeIsMade(string shape, string registrationOptions, string helpers, bool hookRuns)
    {
        output.WriteLine("UNIVERSE " + shape);

        Assert.Equal(hookRuns, UniverseModelHookRuns(registrationOptions, helpers));
    }

    [Fact]
    public void AContextConstructorThatDoesNotForwardItsOptionsProvesNothingAboutTheModel()
    {
        // The registration supplies the compiled model, but the constructor hands EF other options, so what the
        // registration supplied says nothing about what EF holds.
        Assert.False(UniverseModelHookRuns(".UseModel(BlobInput.Model)", string.Empty));

        Assert.True(UniverseModelHookRuns(
            ".UseModel(BlobInput.Model)",
            "public static class UniverseRewrite { public static Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> Same(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) => options; }",
            "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(UniverseRewrite.Same(options))"));
    }

    [Fact]
    public void AContainerBuiltContextWhoseConstructorRewritesItsOptionsRunsTheModelHookToo()
    {
        // The receiver is the injected, container-resolved context; the registration supplies the compiled model, but the
        // constructor hands EF other options, so the model hook is judged although the registration chain is understood.
        const string rewrite = "public static class UniverseRewrite { public static Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> Same(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) => options; }";

        HostedProducerDiscovery<HostedProducerSite> forwarded = HookDiscover(
            SqliteIndexerUseSqlite + ".UseModel(BlobInput.Model)",
            "_ = db.Set<BlobRow>(); ",
            contextMembers: HookModelCreating,
            helpers: rewrite);

        Assert.DoesNotContain(forwarded.Diagnostics, static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));

        HostedProducerDiscovery<HostedProducerSite> rewritten = HookDiscover(
            SqliteIndexerUseSqlite + ".UseModel(BlobInput.Model)",
            "_ = db.Set<BlobRow>(); ",
            contextMembers: HookModelCreating,
            helpers: rewrite,
            contextDeclaration: "public sealed class BlobContext(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> options) : Microsoft.EntityFrameworkCore.DbContext(UniverseRewrite.Same(options))");

        Assert.Contains(rewritten.Diagnostics, static diagnostic => diagnostic.Detail == "System.IO.File.Delete" && diagnostic.Identity.Contains("/model@", StringComparison.Ordinal));
    }

    // ---- EF's design-time factory is run by EF tooling, never by the analysed host.

    private const string DesignTimeFactory = "public sealed class UniverseDesignFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<BlobContext> { public BlobContext CreateDbContext(string[] args) => new(" + UniverseBuilder + ".Options); }";

    [Fact]
    public void AnAllocationInADesignTimeFactoryNothingNamesIsNotAWayTheHostMakesTheContext()
    {
        Assert.False(UniverseModelHookRuns(".UseModel(BlobInput.Model)", DesignTimeFactory));

        // The twin: once analysed source names the factory, its allocation is something the host can run.
        Assert.True(UniverseModelHookRuns(".UseModel(BlobInput.Model)", DesignTimeFactory + " public static class UniverseUsesFactory { public static object Use() => new UniverseDesignFactory(); }"));

        Assert.True(UniverseModelHookRuns(".UseModel(BlobInput.Model)", DesignTimeFactory + " public static class UniverseUsesFactory { public static System.Type Use() => typeof(UniverseDesignFactory); }"));
    }

    [Fact]
    public void AnAllocationInACreateDbContextThatIsNotTheFrameworkFactoryIsStillAWayTheHostMakesTheContext()
    {
        // The method has the right name and shape but the type does not implement the framework contract.
        const string lookalike = "public sealed class UniverseLookalikeFactory { public BlobContext CreateDbContext(string[] args) => new(" + UniverseBuilder + ".Options); }";

        Assert.True(UniverseModelHookRuns(".UseModel(BlobInput.Model)", lookalike));
    }

    private HostedGrimoireProducerInventory.ContextConstruction[] UniverseConstructions(string helpers)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(
            SqliteIndexerSource(SqliteIndexerUseSqlite, string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty, contextExpression: "db", helpers: HookOpaqueContext + helpers),
            "RetroDownfall.Arcanum.Api");

        return [.. HostedGrimoireProducerInventory.ProbeContextConstructions([compilation])];
    }

    [Fact]
    public void AFactoryNothingNamesIsMarkedDesignTimeOnlyAndAFactoryTheSourceNamesIsNot()
    {
        HostedGrimoireProducerInventory.ContextConstruction unreferenced = Assert.Single(UniverseConstructions(DesignTimeFactory));

        Assert.True(unreferenced.DesignTimeOnly);

        HostedGrimoireProducerInventory.ContextConstruction referenced = Assert.Single(
            UniverseConstructions(DesignTimeFactory + " public static class UniverseUsesFactory { public static object Use() => new UniverseDesignFactory(); }"));

        Assert.False(referenced.DesignTimeOnly);

        // An allocation outside any factory is never design-time only.
        Assert.All(UniverseConstructions("public static class UniverseMaker { public static BlobContext Make() => new(" + UniverseBuilder + ".Options); }"), static construction => Assert.False(construction.DesignTimeOnly));
    }

    // ---- The interceptor set of an unfollowable receiver is the union of the registrations and the allocations.

    private bool UniverseCallbackAdmitted(string registrationInterceptors, string helpers, string callback, string statement = "HookOpaque.Context.SaveChanges();")
    {
        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".UseModel(BlobInput.Model)" + registrationInterceptors,
            statement,
            helpers: HookOpaqueContext + helpers);

        return CallbackAdmitted(result, callback);
    }

    [Fact]
    public void TheInterceptorsOfAnUnfollowableReceiverAreTheUnionOfTheRegistrationsAndTheAllocations()
    {
        string allocation = "public static class UniverseMaker { public static BlobContext Make() => new(" + UniverseBuilder + ".UseModel(BlobInput.Model).AddInterceptors(new CommandInterceptorProbe()).Options); }";

        // The registration adds the connection interceptor and an allocation adds the command interceptor: either can be
        // raised by a save on a context nobody can follow.
        Assert.True(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", allocation, "ConnectionOpened"));

        Assert.True(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", allocation, "ReaderExecuting"));

        // The twin: with no such allocation the command interceptor is not one the context can hold.
        Assert.True(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", string.Empty, "ConnectionOpened"));

        Assert.False(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", string.Empty, "ReaderExecuting"));
    }

    [Theory]
    [InlineData("allocation-options-cannot-be-followed", "public static class UniverseMaker { public static BlobContext Make(Microsoft.EntityFrameworkCore.DbContextOptions<BlobContext> supplied) => new(supplied); }")]
    [InlineData("allocation-adds-an-opaque-interceptor", "public static class UniverseMaker { public static BlobContext Make() => new(" + UniverseBuilder + ".UseModel(BlobInput.Model).AddInterceptors(InterceptorInput.Value).Options); }")]
    public void AnAllocationWhoseInterceptorsCannotBeNamedLeavesTheSetOfAnUnfollowableReceiverOpen(string shape, string allocation)
    {
        output.WriteLine("UNIVERSE " + shape);

        HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
            ".UseModel(BlobInput.Model).AddInterceptors(new ConnectionInterceptorProbe())",
            "HookOpaque.Context.SaveChanges();",
            helpers: HookOpaqueContext + allocation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("Microsoft.EntityFrameworkCore.DbContext.SaveChanges", StringComparison.Ordinal));
    }

    [Fact]
    public void ADesignTimeOnlyAllocationAddsNoInterceptorToTheUniverse()
    {
        const string factory = "public sealed class UniverseDesignFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<BlobContext> { public BlobContext CreateDbContext(string[] args) => new(" + UniverseBuilder + ".AddInterceptors(new CommandInterceptorProbe()).Options); }";

        Assert.False(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", factory, "ReaderExecuting"));

        Assert.True(UniverseCallbackAdmitted(".AddInterceptors(new ConnectionInterceptorProbe())", factory, "ConnectionOpened"));
    }

    // ---- The shipped universe.

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_context_universe_is_the_registrations_and_the_installation_reset_allocation()
    {
        HostedGrimoireProducerInventory.ContextUniverseResult universe = HostedGrimoireProducerInventory.ProbeContextUniverse(
            HostedGrimoireProducerInventory.ProductionCompilations, "RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext");

        // Both registrations and the one allocation the host can run (the installation reset's stopped-host context) hand
        // EF the compiled model, and the only interceptor any of them adds is the reviewed enrolment interceptor.
        Assert.True(universe.ModelSupplied);

        Assert.True(universe.InterceptorsClosed);

        Assert.Equal(["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionEnrolmentInterceptor"], universe.Interceptors.Select(static interceptor => interceptor.Type.ToDisplayString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        Assert.True(universe.Registrations >= 1);

        Assert.Equal(1, universe.Constructions);
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_allocates_the_context_in_the_installation_reset_and_in_a_design_time_factory_only()
    {
        HostedGrimoireProducerInventory.ContextConstruction[] constructions = [.. HostedGrimoireProducerInventory.ProbeContextConstructions(HostedGrimoireProducerInventory.ProductionCompilations)];

        string[] files = [.. constructions.Select(static construction => Path.GetFileName(construction.Tree.FilePath) + ":" + construction.DesignTimeOnly).Order(StringComparer.Ordinal)];

        // The design-time factory is run by EF tooling in its own process and nothing in the host names it.
        Assert.Equal(["ArcanumDbContextFactory.cs:True", "InstallationResetExistingGrimoire.cs:False"], files);
    }
}
