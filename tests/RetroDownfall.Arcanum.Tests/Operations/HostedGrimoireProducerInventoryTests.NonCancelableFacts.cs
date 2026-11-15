using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string NonCancelableFactEntry = "sealed class FactEntry { internal async Task CancelAsync() { await Task.CompletedTask; System.IO.File.Delete(\"none-fact-child\"); } } ";

    private static string NonCancelableFactHelpers(string beforeWait = "") => NonCancelableFactEntry
        + "static class FactInput { internal static CancellationToken Unknown; } "
        + "static class FactWaiter { internal static Task Relay(FactEntry[] entries, CancellationToken stop) => Drain(entries, stop); "
        + "internal static async Task Drain(FactEntry[] entries, CancellationToken stop) { " + beforeWait
        + "await Task.WhenAll(System.Linq.Enumerable.Select(entries, static async entry => await entry.CancelAsync().ConfigureAwait(false)))"
        + ".WaitAsync(stop).ConfigureAwait(false); } "
        + "private static void Replace(ref CancellationToken value) { value = FactInput.Unknown; } "
        + "private static void Observe(in CancellationToken value) { } }";

    private static (CSharpCompilation Compilation, HostedProducerDiscovery<HostedProducerSite> Discovery, HostedProducerAdmittedStateObservation[] States)
        DiscoverNonCancelableFacts(string calls, string helpers)
    {
        CSharpCompilation compilation = Compile(R2Source(R2Admission
            + "var entries = new FactEntry[] { new FactEntry() }; " + calls + " await Task.CompletedTask;", helpers));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> states = new();

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [], admittedStateObserver: states.Enqueue);

        return (compilation, result, [.. states]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void None_and_unknown_aggregate_wait_callers_keep_distinct_completion_evidence_in_both_orders(bool noneFirst, bool forwarded)
    {
        string method = forwarded ? "Relay" : "Drain";

        string none = "await FactWaiter." + method + "(entries, CancellationToken.None); ";

        string unknown = "await FactWaiter." + method + "(entries, token); ";

        var (compilation, result, states) = DiscoverNonCancelableFacts(noneFirst ? none + unknown : unknown + none, NonCancelableFactHelpers());

        HostedProducerSite[] effects = EnumerationDeleteSites(result);

        Assert.Contains(effects, static site => site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.Contains(effects, static site => site.WorkFrontierCapsuleId is null && site.EffectFrontierCapsuleId is null);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        // A physical helper evaluated with different demanded facts must not hit an
        // empty/token/equivalent/fallback state certified for the other invocation.
        HostedProducerAdmittedStateObservation[] drains = CanonicalizationStatesOf(compilation, states, "FactWaiter", "Drain");

        // Automatic lifecycle and declared passes may revisit an unproved context.
        // The demanded semantic environments, rather than observer event count, stay distinct.
        Assert.Equal(2, drains.Select(static state => (state.EvaluationEnvironmentToken, state.FallbackEvaluationEnvironment)).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_demanded_None_facts_have_no_supplier_callsite_identity(bool forwarded)
    {
        string method = forwarded ? "Relay" : "Drain";

        string calls = string.Concat(Enumerable.Repeat("await FactWaiter." + method + "(entries, CancellationToken.None); ", 6));

        var (compilation, result, states) = DiscoverNonCancelableFacts(calls, NonCancelableFactHelpers());

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), static site =>
        {
            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.NotNull(site.EffectFrontierCapsuleId);
        });

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.Single(CanonicalizationStatesOf(compilation, states, "FactWaiter", "Drain"));
    }

    [Theory]
    [InlineData("stop = FactInput.Unknown; ")]
    [InlineData("Replace(ref stop); ")]
    [InlineData("Observe(in stop); ")]
    [InlineData("{ ref CancellationToken alias = ref stop; alias = FactInput.Unknown; } ")]
    public void Mutation_or_byref_escape_refuses_a_supplied_None_fact(string beforeWait)
    {
        var (_, result, _) = DiscoverNonCancelableFacts("await FactWaiter.Relay(entries, CancellationToken.None); ", NonCancelableFactHelpers(beforeWait));

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.All(EnumerationDeleteSites(result), static site =>
        {
            Assert.Null(site.WorkFrontierCapsuleId);

            Assert.Null(site.EffectFrontierCapsuleId);
        });

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void Captured_None_parameter_does_not_borrow_an_ordinary_members_fact()
    {
        const string aggregate = "await Task.WhenAll(System.Linq.Enumerable.Select(entries, static async entry => await entry.CancelAsync())).WaitAsync(stop); ";

        string helpers = NonCancelableFactEntry
            + "static class FactWaiter { internal static async Task Drain(FactEntry[] entries, CancellationToken stop) { "
            + aggregate + "Func<Task> captured = async () => { " + aggregate + " }; await captured(); } }";

        var (_, result, _) = DiscoverNonCancelableFacts("await FactWaiter.Drain(entries, CancellationToken.None); ", helpers);

        Assert.Contains(EnumerationDeleteSites(result), static site => site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.Contains(EnumerationDeleteSites(result), static site => site.WorkFrontierCapsuleId is null && site.EffectFrontierCapsuleId is null);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void Nondemanded_token_helpers_still_collapse_None_and_unknown_suppliers()
    {
        var (result, states) = CanonicalizationDiscover(
            "Linker.Link(CancellationToken.None); Linker.Link(Handed.Token); Linker.Link(CancellationToken.None);",
            TokenSinkHelper, "Linker", "Link");

        Assert.Empty(result.Diagnostics);

        Assert.Single(states);
    }
}
