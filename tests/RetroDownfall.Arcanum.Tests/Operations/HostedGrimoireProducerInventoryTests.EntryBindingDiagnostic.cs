using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The direct-null row distinguishes a correctly source-bound empty constructor
    // value from an unknown interface receiver. No runtime ownership fix is implied.
    [Theory]
    [InlineData("literal-null")]
    [InlineData("direct-local-null")]
    [InlineData("direct-null")]
    [InlineData("factory-empty")]
    public void EntryBindingKnownEmptyConstructorDoesNotRequireACleanupTarget(string shape)
    {
        string body = shape switch
        {
            "literal-null" => "((IDisposable?)null)?.Dispose();",
            "direct-local-null" => "IDisposable? binding = null; binding?.Dispose();",
            "factory-empty" => "await using IAsyncDisposable entry = BindingDiagnosticOwner.Empty();",
            _ => "await using IAsyncDisposable entry = new BindingDiagnosticOwner.Entry(null, null);",
        };

        HostedProducerDiscovery<HostedProducerSite> result = EntryBindingDiagnosticDiscover(body);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType.EndsWith("BindingDiagnosticOwner.Binding", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("direct-known")]
    [InlineData("factory-known")]
    [InlineData("factory-two-return")]
    public void EntryBindingExactConstructorRetainsItsSelectedBindingEffect(string shape)
    {
        string body = shape switch
        {
            "direct-known" => "await using IAsyncDisposable entry = new BindingDiagnosticOwner.Entry(new BindingDiagnosticOwner.Binding(), null);",
            "factory-known" => "await using IAsyncDisposable entry = BindingDiagnosticOwner.Known();",
            _ => "await using IAsyncDisposable entry = BindingDiagnosticOwner.Enter(token.CanBeCanceled);",
        };

        HostedProducerDiscovery<HostedProducerSite> result = EntryBindingDiagnosticDiscover(body);

        HostedProducerSite effect = Assert.Single(result.Items, static site =>
            site.EnclosingType.EndsWith("BindingDiagnosticOwner.Binding", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Delete");

        HostedProducerSite acquired = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Equal(acquired.CapsuleId, effect.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, effect.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("replaced-capture")]
    [InlineData("ref-escaped-capture")]
    public void EntryBindingUnknownConstructorValueCannotAcquireAnExactTarget(string shape)
    {
        string source = shape == "unknown" ? "BindingDiagnosticInput.Unknown()" : "null";

        string replacement = shape switch
        {
            "replaced-capture" => "binding = BindingDiagnosticInput.Unknown();",
            "ref-escaped-capture" => "BindingDiagnosticInput.Replace(ref binding);",
            _ => string.Empty,
        };

        HostedProducerDiscovery<HostedProducerSite> result = EntryBindingDiagnosticDiscover(
            "await using IAsyncDisposable entry = new BindingDiagnosticOwner.Entry(" + source + ", null);",
            replacement: replacement);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType.EndsWith("BindingDiagnosticOwner.Binding", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Delete");
    }

    [Fact]
    public void EntryBindingUnconditionalNullCallCannotUseConditionalAbsenceProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = EntryBindingDiagnosticDiscover(
            "await using IAsyncDisposable entry = new BindingDiagnosticOwner.Entry(null, null);",
            conditional: false);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType.EndsWith("BindingDiagnosticOwner.Binding", StringComparison.Ordinal)
                && site.Callee == "System.IO.File.Delete");
    }

    private static HostedProducerDiscovery<HostedProducerSite> EntryBindingDiagnosticDiscover(
        string body,
        string replacement = "",
        bool conditional = true)
    {
        string cleanup = conditional ? "binding?.Dispose();" : "binding!.Dispose();";

        string source = R2Source(R2Admission + body, $$"""
            internal static class BindingDiagnosticOwner
            {
                internal static IDisposable Bind() => new Binding();

                internal static IAsyncDisposable Empty() => new Entry(null, null);

                internal static IAsyncDisposable Known() => new Entry(Bind(), null);

                internal static IAsyncDisposable Enter(bool empty)
                {
                    if (empty) return new Entry(null, null);

                    return new Entry(Bind(), null);
                }

                internal sealed class Binding : IDisposable
                {
                    public void Dispose() => System.IO.File.Delete("selected-entry-binding");
                }

                internal sealed class Entry(IDisposable? binding, IAsyncDisposable? lease) : IAsyncDisposable
                {
                    public ValueTask DisposeAsync()
                    {
                        {{replacement}}

                        {{cleanup}}

                        return ValueTask.CompletedTask;
                    }
                }
            }

            internal static class BindingDiagnosticInput
            {
                internal static extern IDisposable Unknown();

                internal static extern void Replace(ref IDisposable? value);
            }
            """);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));

        return R2Discover(source);
    }
}
