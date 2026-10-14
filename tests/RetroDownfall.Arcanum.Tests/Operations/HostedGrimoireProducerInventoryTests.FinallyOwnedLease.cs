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

    // The twins. Each keeps the scope disposal and the callback it raises, and changes only what the lease is doing when the scope is
    // disposed: it is disposed first, its disposal is not on every path of the finally, or the protected block ended it already.
    [Theory]
    [InlineData("inline", "scope-after-lease")]
    [InlineData("returned", "scope-after-lease")]
    [InlineData("inline", "lease-disposal-not-on-every-path")]
    [InlineData("returned", "lease-disposal-not-on-every-path")]
    [InlineData("inline", "lease-ended-in-protected-block")]
    [InlineData("returned", "lease-ended-in-protected-block")]
    public void AScopeDisposedAfterTheLeaseIsGoneOrWithoutOwnershipRunsItsCallbacksWithoutWork(string acquisition, string shape)
    {
        string protectedBlock = shape == "lease-ended-in-protected-block"
            ? FinallyOwnedLeaseDisposal + " " + FinallyOwnedProtected
            : FinallyOwnedProtected;

        string finallyBlock = shape switch
        {
            // The lease is disposed last on every path, and then the scope: the scope is disposed after the lease.
            "scope-after-lease" => "try { try { " + FinallyOwnedGroupDisposal + " } finally { } } finally { " + FinallyOwnedLeaseDisposal + " } " + FinallyOwnedScopeDisposal,

            // A statement that can throw comes first, so the lease is not disposed on every path.
            "lease-disposal-not-on-every-path" => FinallyOwnedScopeDisposal + " " + FinallyOwnedLeaseDisposal,

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
}
