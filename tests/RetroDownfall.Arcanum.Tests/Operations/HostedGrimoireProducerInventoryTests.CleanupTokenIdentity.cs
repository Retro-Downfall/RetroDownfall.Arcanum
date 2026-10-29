using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupBoundTokenCallbackDoesNotRetainSupplierIdentity(bool throughForwarders)
    {
        const string helper = TokenSinkHelper + """

            public static class Forwarders
            {
                public static void A(System.Action<System.Threading.CancellationToken> work, System.Threading.CancellationToken token) => work(token);

                public static void B(System.Action<System.Threading.CancellationToken> work, System.Threading.CancellationToken token) => work(token);
            }
            """;

        string body = throughForwarders
            ? "Forwarders.A(Linker.Link, Handed.Token); Forwarders.B(Linker.Link, Handed.Token);"
            : "System.Action<System.Threading.CancellationToken> work = Linker.Link; work(Handed.Token); work(Handed.Token);";

        var (compilation, result, states) = ClosedValueDiscover(body, helper);

        Assert.Empty(result.Diagnostics);

        Assert.Single(CanonicalizationStatesOf(compilation, states, "Linker", "Link"));
    }

    [Theory]
    [InlineData("System.Threading.CancellationTokenSource", "LinkSource", "Source")]
    [InlineData("System.Threading.CancellationTokenRegistration", "Settle", "Registration")]
    public void CleanupBoundDisposableTokenRelativesKeepSupplierIdentity(string type, string method, string property)
    {
        string body = "System.Action<" + type + "> work = Linker." + method + "; work(Handed." + property + "); work(Handed." + property + ");";

        var (compilation, _, states) = ClosedValueDiscover(body, TokenSinkHelper);

        Assert.Equal(2, CanonicalizationStatesOf(compilation, states, "Linker", method).Length);
    }

    [Fact]
    public void CleanupBoundTokenLambdaDoesNotRetainTheInvocationSite()
    {
        const string helper = """
            public static class Forwarders
            {
                public static void Run(System.Action<System.Threading.CancellationToken> work, System.Threading.CancellationToken token)
                {
                    work(token);

                    work(token);
                }
            }
            """;

        const string body = "Forwarders.Run(supplied => { using System.Threading.CancellationTokenSource linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(supplied); }, token);";

        var (compilation, result, states) = ClosedValueDiscover(body, helper);

        Assert.Empty(result.Diagnostics);

        var lambda = Assert.Single(compilation.SyntaxTrees.SelectMany(static tree => tree.GetRoot().DescendantNodes()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.SimpleLambdaExpressionSyntax>());

        Assert.Single(states, state => state.SpanStart == lambda.Body.SpanStart && state.SpanLength == lambda.Body.Span.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupBoundTokenSourceAndCallbackEffectsKeepAdmissionRequirements(bool admitted)
    {
        const string helper = """
            public static class TokenEffects
            {
                public static System.Threading.CancellationToken Supply()
                {
                    System.IO.File.Delete("source");

                    return default;
                }

                public static void Consume(System.Threading.CancellationToken supplied)
                {
                    System.IO.File.Delete("callback");

                    using System.Threading.CancellationTokenSource linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(supplied);
                }
            }
            """;

        string body = (admitted ? R2Admission : "") + "System.Action<System.Threading.CancellationToken> callback = TokenEffects.Consume; callback(TokenEffects.Supply()); callback(TokenEffects.Supply());";

        var (_, result, _) = ClosedValueDiscover(body, helper);

        string suffix = " work=" + admitted + " effect=" + admitted;

        Assert.Equal(new[] { "TokenEffects.Consume" + suffix, "TokenEffects.Supply" + suffix }, CanonicalizationSendVerdict(result, "System.IO.File.Delete").Where(static item => !item.StartsWith("HOSTED_", StringComparison.Ordinal)));

        Assert.Equal(
            admitted ? [] : new[] { "HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING" },
            result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CleanupBoundCounterfeitTokenKeepsDisposableSourceAndEffects()
    {
        const string helper = """
            namespace Counterfeit
            {
                public sealed class CancellationToken : System.IDisposable
                {
                    public void Dispose() { System.IO.File.Delete("counterfeit"); }
                }

                public static class TokenEffects
                {
                    public static CancellationToken Current { get; set; } = null!;

                    public static void Consume(CancellationToken supplied) { using (supplied) { } }
                }
            }
            """;

        var (compilation, result, states) = ClosedValueDiscover("System.Action<Counterfeit.CancellationToken> work = Counterfeit.TokenEffects.Consume; work(Counterfeit.TokenEffects.Current); work(Counterfeit.TokenEffects.Current);", helper);

        Assert.Equal(2, CanonicalizationStatesOf(compilation, states, "Counterfeit.TokenEffects", "Consume").Length);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("mutated")]
    [InlineData("byref")]
    public void CleanupBoundTokenUnknownOrMutableCallableDoesNotAcquireAuthority(string shape)
    {
        const string helper = """
            public static class TokenEffects
            {
                public delegate void ByRef(ref System.Threading.CancellationToken token);

                public static System.Action<System.Threading.CancellationToken> Unknown { get; set; } = null!;

                public static void Quiet(System.Threading.CancellationToken token) { }

                public static void Loud(System.Threading.CancellationToken token) { System.IO.File.Delete("loud"); }

                public static void Mutate(ref System.Threading.CancellationToken token)
                {
                    token = default;

                    System.IO.File.Delete("mutation");
                }
            }
            """;

        string body = shape switch
        {
            "opaque" => "TokenEffects.Unknown(token);",
            "mutated" => "System.Action<System.Threading.CancellationToken> work = TokenEffects.Quiet; work = TokenEffects.Loud; work(token);",
            "byref" => "TokenEffects.ByRef work = TokenEffects.Mutate; work(ref token);",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var (_, result, _) = ClosedValueDiscover(body, helper);

        Assert.Equal(
            shape == "byref"
                ? new[] { "HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING" }
                : ["HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"],
            result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete" && (site.WorkFrontierCapsuleId is not null || site.EffectFrontierCapsuleId is not null));
    }

    [Fact]
    public void CleanupBoundSameMetadataNameSourceTokenIsNotTheFrameworkToken()
    {
        const string source = """
            public sealed class Worker
            {
                public System.Threading.Tasks.Task StartAsync()
                {
                    System.Action<System.Threading.CancellationToken> callback = TokenEffects.Consume;

                    callback(TokenEffects.Current);

                    callback(TokenEffects.Current);

                    return System.Threading.Tasks.Task.CompletedTask;
                }
            }

            namespace System.Threading
            {
                public sealed class CancellationToken : System.IDisposable
                {
                    public void Dispose() { System.IO.File.Delete("counterfeit"); }
                }
            }

            public static class TokenEffects
            {
                public static System.Threading.CancellationToken Current { get; set; } = null!;

                public static void Consume(System.Threading.CancellationToken supplied) { using (supplied) { } }
            }
            """;

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

        var result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            [],
            admittedStateObserver: observed.Enqueue);

        Assert.Equal(2, CanonicalizationStatesOf(compilation, [.. observed], "TokenEffects", "Consume").Length);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }
}
