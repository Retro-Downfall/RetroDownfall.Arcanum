using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // RED candidate: the selected interface disposal effect must remain inside
    // the worker's retained work/effect frontier through the exact owned return.
    [Theory]
    [InlineData("owned-return")]
    [InlineData("direct-await")]
    [InlineData("direct-await-as-task")]
    public void NullableDisposalCompletionRetainsSelectedEffectInsideCallerAdmission(string shape)
    {
        string body = shape switch
        {
            "direct-await" => "IAsyncDisposable? cleanup = new ConditionalCompletionLeaf(); await (cleanup?.DisposeAsync() ?? ValueTask.CompletedTask);",
            "direct-await-as-task" => "IAsyncDisposable? cleanup = new ConditionalCompletionLeaf(); await (cleanup?.DisposeAsync().AsTask() ?? Task.CompletedTask);",
            _ => "await ConditionalCompletionOwner.ReturnAsync();",
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(body);

        HostedProducerSite effect = Assert.Single(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Delete");

        HostedProducerSite acquired = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Equal(acquired.CapsuleId, effect.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, effect.EffectFrontierCapsuleId);

        Assert.Equal(GrimoireWorkKind.WorkspaceIndexing, acquired.AdmittedWorkKind);

        Assert.NotNull(effect.WorkFrontierCapsuleId);

        Assert.NotNull(effect.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                or "HOSTED_SITE_WORK_FRONTIER_MISSING"
                or "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                or "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Theory]
    [InlineData("abandoned-return")]
    [InlineData("discarded-inner")]
    [InlineData("wrong-return")]
    public void NullableDisposalCannotBorrowAdmissionWithoutJoiningItsActualCompletion(string shape)
    {
        string body = shape == "abandoned-return"
            ? "_ = ConditionalCompletionOwner.ReturnAsync();"
            : "await ConditionalCompletionOwner.ReturnAsync();";

        string terminal = shape switch
        {
            "discarded-inner" => "cleanup?.DisposeAsync(); return ValueTask.CompletedTask;",
            "wrong-return" => "ValueTask? abandoned = cleanup?.DisposeAsync(); return ValueTask.CompletedTask;",
            _ => "return cleanup?.DisposeAsync() ?? ValueTask.CompletedTask;",
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(body, terminal);

        HostedProducerSite effect = Assert.Single(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Delete");

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Null(effect.AdmittedWorkKind);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void NullableReceiverDoesNotJoinTheCleanupUsedToProduceThatReceiver()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(
            "IAsyncDisposable cleanup = new ConditionalCompletionLeaf(); await (cleanup.DisposeAsync().AsTask()?.IgnoreCompletion() ?? Task.CompletedTask);");

        HostedProducerSite effect = Assert.Single(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Delete");

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Null(effect.AdmittedWorkKind);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void UninvokedNullableDisposalCallableDoesNotInventTheSelectedEffect()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(
            "IAsyncDisposable? cleanup = new ConditionalCompletionLeaf(); Func<ValueTask> dormant = () => cleanup?.DisposeAsync() ?? ValueTask.CompletedTask;");

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf");
    }

    [Fact]
    public void KnownNullNullableDisposalDoesNotInventASelectedEffect()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(
            "IAsyncDisposable? cleanup = null; await (cleanup?.DisposeAsync() ?? ValueTask.CompletedTask);");

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf");

        // This packet changes completion only. Any existing null target diagnostic
        // is separate; null must never gain a fabricated concrete effect/frontier.
    }

    [Fact]
    public void JoinedNullableDisposalOfUnknownInterfaceValueRemainsTargetUnresolved()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(
            "IAsyncDisposable? cleanup = ConditionalCompletionInput.Unknown(); await (cleanup?.DisposeAsync() ?? ValueTask.CompletedTask);");

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf");
    }

    [Fact]
    public void ExplicitAwaitPreservesTheSelectedNullableDisposalEffect()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverConditionalCompletion(
            "await ConditionalCompletionOwner.ReturnAsync();",
            "if (cleanup is not null) { await cleanup.DisposeAsync().ConfigureAwait(false); }",
            asynchronousOwner: true);

        HostedProducerSite effect = Assert.Single(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Delete");

        HostedProducerSite acquired = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite group = Assert.Single(result.Items, static site =>
            site.RootType == "Worker"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Equal(acquired.CapsuleId, effect.WorkFrontierCapsuleId);

        Assert.Equal(group.CapsuleId, effect.EffectFrontierCapsuleId);

        Assert.Equal(GrimoireWorkKind.WorkspaceIndexing, acquired.AdmittedWorkKind);

        Assert.NotNull(effect.WorkFrontierCapsuleId);

        Assert.NotNull(effect.EffectFrontierCapsuleId);

        Assert.DoesNotContain(result.Items, static site =>
            site.EnclosingType == "ConditionalCompletionLeaf"
                && site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                or "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverConditionalCompletion(
        string body,
        string terminal = "return cleanup?.DisposeAsync() ?? ValueTask.CompletedTask;",
        bool asynchronousOwner = false)
    {
        string ownerModifier = asynchronousOwner ? "async " : string.Empty;

        string source = R2Source(R2Admission + body, $$"""
            internal sealed class ConditionalCompletionLeaf : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    System.IO.File.Exists("wrong-public-cleanup");

                    return ValueTask.CompletedTask;
                }

                ValueTask IAsyncDisposable.DisposeAsync()
                {
                    System.IO.File.Delete("selected-interface-cleanup");

                    return ValueTask.CompletedTask;
                }
            }

            internal static class ConditionalCompletionOwner
            {
                internal static {{ownerModifier}}ValueTask ReturnAsync()
                {
                    IAsyncDisposable? cleanup = new ConditionalCompletionLeaf();

                    {{terminal}}
                }
            }

            internal static class ConditionalCompletionExtensions
            {
                internal static Task IgnoreCompletion(this Task receiver) => Task.CompletedTask;
            }

            internal static class ConditionalCompletionInput
            {
                internal static extern IAsyncDisposable Unknown();
            }
            """);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));

        return R2Discover(source);
    }
}
