using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// A manually owned Work lease is held until its own disposal. The shipped recovery shell disposes the container scope (and the contexts it
// built) in a nested finally that ends with the lease's disposal, so a scope disposal is judged under the lease it runs inside. The
// ownership proof used to cover only the protected block, so the callbacks the disposal raises lost their Work. These fixtures pin that a
// site in the finally that runs before the lease's disposal is under the lease, and that a site after it, a lease whose disposal is not on
// every path of the finally, and a lease the protected block already ended are not.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string FinallyOwnedGate = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate other = null!; "
        + "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory factory = null!; ";

    // The lease the shell holds, acquired in the shapes the shipped code uses: inline with an out variable (the recovery settlement), or
    // returned by an awaited helper that owns the retry loop (the discovery page).
    private const string FinallyOwnedInlineAcquisition = """
        if (!other.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? shellAdmitted))
        {
            continue;
        }

        RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease shellLease = shellAdmitted!;
        """;

    private const string FinallyOwnedReturnedAcquisition = """
        RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease shellLease = await FinallyOwnedAcquirer.AcquireAsync(other, token).ConfigureAwait(false);
        """;

    private const string FinallyOwnedHelpers = """
        public static class FinallyOwnedAcquirer
        {
            public static async System.Threading.Tasks.Task<RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease> AcquireAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate gate, System.Threading.CancellationToken token)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    if (gate.TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.WorkspaceIndexing, out RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? lease))
                    {
                        return lease!;
                    }

                    await System.Threading.Tasks.Task.Yield();
                }
            }
        }
        """;

    private const string FinallyOwnedLocals = """
        System.IDisposable? shellGroup = null;
        Microsoft.Extensions.DependencyInjection.AsyncServiceScope shellScope = default;
        bool shellScopeCreated = false;
        """;

    private const string FinallyOwnedProtected = """
        shellScope = factory.CreateAsyncScope();
        shellScopeCreated = true;
        return;
        """;

    private const string FinallyOwnedScopeDisposal = "if (shellScopeCreated) { await shellScope.DisposeAsync().ConfigureAwait(false); }";

    private const string FinallyOwnedGroupDisposal = "if (shellGroup is not null) { shellGroup.Dispose(); }";

    private const string FinallyOwnedLeaseDisposal = "await shellLease.DisposeAsync().ConfigureAwait(false);";

    private static string FinallyOwnedShell(string acquisition, string protectedBlock, string finallyBlock) => "while (true) { "
        + acquisition + " " + FinallyOwnedLocals + " try { " + protectedBlock + " } finally { " + finallyBlock + " } }";

    // The shipped finally: the scope, then the group, then the lease, each in the finally of the one before it, so the lease is disposed last
    // on every path.
    private static string FinallyOwnedNested(string inner) =>
        "try { try { " + inner + " } finally { " + FinallyOwnedGroupDisposal + " } } finally { " + FinallyOwnedLeaseDisposal + " }";

    [Theory]
    [InlineData("inline", "scope-then-lease")]
    [InlineData("returned", "scope-then-lease")]
    public void AScopeDisposedInTheFinallyBeforeTheLeaseIsDisposedRunsItsCallbacksUnderTheLease(string acquisition, string shape)
    {
        string shell = FinallyOwnedShell(
            acquisition == "inline" ? FinallyOwnedInlineAcquisition : FinallyOwnedReturnedAcquisition,
            FinallyOwnedProtected,
            FinallyOwnedNested(FinallyOwnedScopeDisposal));

        EachQueryMode(production =>
        {
            output.WriteLine("FINALLY-OWNED " + acquisition + " " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                early: FinallyOwnedGate + shell,
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + FinallyOwnedHelpers,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.True(CallbackAdmitted(result, "ConnectionDisposedAsync"));

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    // The soundness twins. Each keeps the scope disposal and the callback it raises, and changes only what the lease is doing when the
    // scope is disposed: it is already disposed (the finally disposes it first, or the protected block ended it), so no Work is held at
    // runtime and the callback must not be admitted.
    [Theory]
    [InlineData("inline", "scope-after-lease")]
    [InlineData("returned", "scope-after-lease")]
    [InlineData("inline", "lease-ended-in-protected-block")]
    [InlineData("returned", "lease-ended-in-protected-block")]
    public void AScopeDisposedAfterTheLeaseIsGoneRunsItsCallbacksWithoutWork(string acquisition, string shape)
    {
        string protectedBlock = shape == "lease-ended-in-protected-block"
            ? FinallyOwnedLeaseDisposal + " " + FinallyOwnedProtected
            : FinallyOwnedProtected;

        string finallyBlock = shape switch
        {
            // The lease is disposed last on every path, and then the scope: the scope is disposed after the lease.
            "scope-after-lease" => "try { try { " + FinallyOwnedGroupDisposal + " } finally { } } finally { " + FinallyOwnedLeaseDisposal + " } " + FinallyOwnedScopeDisposal,

            // The shipped finally, over a lease the protected block disposed before the scope is.
            _ => FinallyOwnedNested(FinallyOwnedScopeDisposal),
        };

        string shell = FinallyOwnedShell(
            acquisition == "inline" ? FinallyOwnedInlineAcquisition : FinallyOwnedReturnedAcquisition,
            protectedBlock,
            finallyBlock);

        EachQueryMode(production =>
        {
            output.WriteLine("FINALLY-OWNED " + acquisition + " " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                early: FinallyOwnedGate + shell,
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + FinallyOwnedHelpers,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.False(CallbackAdmitted(result, "ConnectionDisposedAsync"));

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    // The conservative refusal. At runtime the scope here is disposed before the lease, so Work is held; the site is refused because
    // the ownership proof needs the lease's release to be guaranteed, and a statement able to throw before the disposal leaves the
    // lease unreleased on that path. This pins the proof's requirement, not a runtime fact: a proof that guaranteed the release some
    // other way would admit the site, and this row would then move to the held rows.
    [Theory]
    [InlineData("inline")]
    [InlineData("returned")]
    public void AScopeDisposedBeforeALeaseReleaseThatIsNotGuaranteedIsRefusedConservatively(string acquisition)
    {
        string shell = FinallyOwnedShell(
            acquisition == "inline" ? FinallyOwnedInlineAcquisition : FinallyOwnedReturnedAcquisition,
            FinallyOwnedProtected,
            FinallyOwnedScopeDisposal + " " + FinallyOwnedLeaseDisposal);

        EachQueryMode(production =>
        {
            output.WriteLine("FINALLY-OWNED " + acquisition + " release-not-guaranteed " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                early: FinallyOwnedGate + shell,
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + FinallyOwnedHelpers,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.False(CallbackAdmitted(result, "ConnectionDisposedAsync"));

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        });
    }

    // A lease can be ended by code that never names it as a call receiver in the member that holds it: a local function or a lambda that
    // captures it, a method group made from one of its members, a helper the lease is handed to. The closures and delegates below do
    // that before the scope is disposed, in the finalizer and in the protected block, over the inline and the returned acquisition. Each
    // refusal is the lifetime rule's own, a missing Work frontier at the callback, and not a by-product of some other diagnostic.
    private const string FinallyOwnedClosureHelpers = """
        public static class FinallyOwnedRelease
        {
            public static async System.Threading.Tasks.Task InvokeAsync(System.Func<System.Threading.Tasks.ValueTask> release)
            {
                await release().ConfigureAwait(false);
            }

            public static async System.Threading.Tasks.Task ReleaseAsync(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            public static bool Probe(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) => lease.TryBeginExternalEffectGroup(out _);
        }

        public static class FinallyOwnedStore
        {
            public static RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? Kept;
        }
        """;

    private const string FinallyOwnedReleaseLocal = "async System.Threading.Tasks.ValueTask ReleaseLocalAsync() { await shellLease.DisposeAsync().ConfigureAwait(false); }";

    private const string FinallyOwnedRelease = "await ReleaseLocalAsync().ConfigureAwait(false);";

    // The code that runs before the scope's disposal, the code that runs after it, and the declarations that follow the try statement.
    private static (string Before, string After, string Tail) FinallyOwnedClosureShape(string shape) => shape switch
    {
        "local-function-disposes" => (FinallyOwnedRelease, string.Empty, FinallyOwnedReleaseLocal),

        "local-function-disposes-through-a-helper" => ("await ReleaseViaAsync().ConfigureAwait(false);", string.Empty,
            "async System.Threading.Tasks.ValueTask ReleaseViaAsync() { await FinallyOwnedRelease.ReleaseAsync(shellLease).ConfigureAwait(false); }"),

        "local-function-two-hops" => ("await OuterAsync().ConfigureAwait(false);", string.Empty,
            "async System.Threading.Tasks.ValueTask OuterAsync() { await InnerAsync().ConfigureAwait(false); } async System.Threading.Tasks.ValueTask InnerAsync() { await shellLease.DisposeAsync().ConfigureAwait(false); }"),

        "local-function-stores-the-lease" => ("Keep();", string.Empty, "void Keep() { FinallyOwnedStore.Kept = shellLease; }"),

        "local-function-returns-the-lease" => ("_ = Hand();", string.Empty, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease Hand() => shellLease;"),

        "local-function-method-group" => ("System.Func<System.Threading.Tasks.ValueTask> release = ReleaseLocalAsync; await release().ConfigureAwait(false);", string.Empty, FinallyOwnedReleaseLocal),

        "local-function-recursion-disposes" => ("await CountDownAsync(2).ConfigureAwait(false);", string.Empty,
            "async System.Threading.Tasks.ValueTask CountDownAsync(int remaining) { if (remaining > 0) { await CountDownAsync(remaining - 1).ConfigureAwait(false); } else { await shellLease.DisposeAsync().ConfigureAwait(false); } }"),

        "local-function-returns-a-disposing-lambda" => ("await Make()().ConfigureAwait(false);", string.Empty,
            "System.Func<System.Threading.Tasks.ValueTask> Make() => () => shellLease.DisposeAsync();"),

        "lambda-in-a-lambda-disposes" => ("System.Func<System.Func<System.Threading.Tasks.ValueTask>> make = () => () => shellLease.DisposeAsync(); await make()().ConfigureAwait(false);", string.Empty, string.Empty),

        "alias-captured-by-a-lambda" => ("var alias = shellLease; System.Func<System.Threading.Tasks.ValueTask> release = () => alias.DisposeAsync(); await release().ConfigureAwait(false);", string.Empty, string.Empty),

        "lambda-disposes" => ("System.Func<System.Threading.Tasks.ValueTask> release = async () => await shellLease.DisposeAsync().ConfigureAwait(false); await release().ConfigureAwait(false);", string.Empty, string.Empty),

        "lambda-passed-to-a-caller" => ("await FinallyOwnedRelease.InvokeAsync(() => shellLease.DisposeAsync()).ConfigureAwait(false);", string.Empty, string.Empty),

        "method-group-dispose-async" => ("System.Func<System.Threading.Tasks.ValueTask> release = shellLease.DisposeAsync; await release().ConfigureAwait(false);", string.Empty, string.Empty),

        "method-group-sync-dispose" => ("System.Action release = shellLease.Dispose; release();", string.Empty, string.Empty),

        "method-group-through-a-cast" => ("System.Func<System.Threading.Tasks.ValueTask> release = ((System.IAsyncDisposable)shellLease).DisposeAsync; await release().ConfigureAwait(false);", string.Empty, string.Empty),

        "conditional-access-dispose" => ("await (shellLease?.DisposeAsync() ?? default).ConfigureAwait(false);", string.Empty, string.Empty),

        "method-group-argument" => ("await FinallyOwnedRelease.InvokeAsync(shellLease.DisposeAsync).ConfigureAwait(false);", string.Empty, string.Empty),

        // The held rows: a closure that only probes the lease (the one call that keeps its receiver), one that calls itself and still
        // only probes it, one that captures nothing, a method group made from a value that is not the lease, and a closure that ends the
        // lease only after the scope is disposed.
        "recursive-local-function-probes-the-lease" => ("_ = ProbeDeep(2);", string.Empty,
            "bool ProbeDeep(int depth) => depth > 0 ? ProbeDeep(depth - 1) : shellLease.TryBeginExternalEffectGroup(out _);"),

        "static-local-function" => ("_ = Twice(1);", string.Empty, "static int Twice(int value) => value * 2;"),

        "local-function-probes-the-lease" => ("_ = Probe();", string.Empty, "bool Probe() => shellLease.TryBeginExternalEffectGroup(out _);"),

        "local-function-hands-the-lease-to-a-prober" => ("_ = Probe();", string.Empty, "bool Probe() => FinallyOwnedRelease.Probe(shellLease);"),

        "lambda-probes-the-lease" => ("System.Func<bool> probe = () => shellLease.TryBeginExternalEffectGroup(out _); _ = probe();", string.Empty, string.Empty),

        "method-group-over-an-unrelated-value" => ("string unrelated = \"unrelated\"; System.Func<int> hash = unrelated.GetHashCode; _ = hash();", string.Empty, string.Empty),

        "local-function-disposes-after-the-scope" => (string.Empty, FinallyOwnedRelease, FinallyOwnedReleaseLocal),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static readonly string[] FinallyOwnedClosureRefusalShapes =
    [
        "local-function-disposes",
        "local-function-disposes-through-a-helper",
        "local-function-two-hops",
        "local-function-stores-the-lease",
        "local-function-returns-the-lease",
        "local-function-method-group",
        "local-function-recursion-disposes",
        "local-function-returns-a-disposing-lambda",
        "lambda-in-a-lambda-disposes",
        "alias-captured-by-a-lambda",
        "lambda-disposes",
        "lambda-passed-to-a-caller",
        "method-group-dispose-async",
        "method-group-sync-dispose",
        "method-group-through-a-cast",
        "method-group-argument",
        "conditional-access-dispose",
    ];

    private static readonly string[] FinallyOwnedClosureHeldShapes =
    [
        "local-function-probes-the-lease",
        "local-function-hands-the-lease-to-a-prober",
        "lambda-probes-the-lease",
        "recursive-local-function-probes-the-lease",
        "static-local-function",
        "method-group-over-an-unrelated-value",
        "local-function-disposes-after-the-scope",
    ];

    private static TheoryData<string, string, string> FinallyOwnedClosureRows(string[] shapes)
    {
        TheoryData<string, string, string> rows = [];

        foreach (string acquisition in new[] { "inline", "returned" })
        {
            foreach (string place in new[] { "finalizer", "protected" })
            {
                foreach (string shape in shapes)
                {
                    rows.Add(acquisition, place, shape);
                }
            }
        }

        return rows;
    }

    public static TheoryData<string, string, string> FinallyOwnedClosureRefusalRows => FinallyOwnedClosureRows(FinallyOwnedClosureRefusalShapes);

    public static TheoryData<string, string, string> FinallyOwnedClosureHeldRows => FinallyOwnedClosureRows(FinallyOwnedClosureHeldShapes);

    private void RunFinallyOwnedClosure(string acquisition, string place, string shape, bool underLease)
    {
        (string before, string after, string tail) = FinallyOwnedClosureShape(shape);

        string protectedBlock;

        string finallyBlock;

        if (place == "finalizer")
        {
            protectedBlock = FinallyOwnedProtected;

            finallyBlock = FinallyOwnedNested(before + " " + FinallyOwnedScopeDisposal + " " + after);
        }
        else
        {
            protectedBlock = before + " shellScope = factory.CreateAsyncScope(); shellScopeCreated = true; " + FinallyOwnedScopeDisposal + " " + after + " return;";

            finallyBlock = FinallyOwnedLeaseDisposal;
        }

        string shell = "while (true) { " + (acquisition == "inline" ? FinallyOwnedInlineAcquisition : FinallyOwnedReturnedAcquisition) + " "
            + FinallyOwnedLocals + " try { " + protectedBlock + " } finally { " + finallyBlock + " } " + tail + " }";

        EachQueryMode(production =>
        {
            output.WriteLine("FINALLY-OWNED-CLOSURE " + acquisition + " " + place + " " + shape + " " + (production ? "production" : "strict"));

            HostedProducerDiscovery<HostedProducerSite> result = InterceptorDiscover(
                ".AddInterceptors(new DisposeProbe())",
                string.Empty,
                early: FinallyOwnedGate + shell,
                contextMembers: HookRows,
                helpers: DisposeProbeTypes + FinallyOwnedHelpers + FinallyOwnedClosureHelpers,
                production: production);

            Assert.True(CallbackTraversed(result, "ConnectionDisposedAsync"));

            Assert.Equal(underLease, CallbackAdmitted(result, "ConnectionDisposedAsync"));

            Assert.Equal(!underLease, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"));
        });
    }

    [Theory]
    [MemberData(nameof(FinallyOwnedClosureRefusalRows))]
    public void ALeaseEndedThroughAClosureOrADelegateBeforeTheScopeIsDisposedRunsItsCallbacksWithoutWork(string acquisition, string place, string shape)
    {
        RunFinallyOwnedClosure(acquisition, place, shape, underLease: false);
    }

    [Theory]
    [MemberData(nameof(FinallyOwnedClosureHeldRows))]
    public void AClosureThatDoesNotEndTheLeaseBeforeTheScopeIsDisposedLeavesItsCallbacksUnderTheLease(string acquisition, string place, string shape)
    {
        RunFinallyOwnedClosure(acquisition, place, shape, underLease: true);
    }
}
