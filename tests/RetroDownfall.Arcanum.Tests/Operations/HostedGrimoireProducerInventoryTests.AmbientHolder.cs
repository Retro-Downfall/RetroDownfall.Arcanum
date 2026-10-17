using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Ambient Holder Refusal. The lease travels by value, so a member that reads a lease out of a holder object has no business
    // holding the worker's Work: the holder's lease is ambient state, not the lease the worker minted and handed down. The refusal is
    // of the read. It does not depend on what the holder is called or on how it came by its lease: a binder named Bind, a binder named
    // anything else, no binder at all, a property setter, a constructor, a public field and a conditional access are all the same
    // read, and no witness lifts the refusal. Two things must hold when a send's member reads such a holder:
    //   - the send gets no Work, and says so with HOSTED_SITE_WORK_FRONTIER_MISSING at the send;
    //   - a group begun on the holder's lease is refused as HOSTED_ADMISSION_RECEIVER_MISMATCH, so it is nobody's Effect.
    // The lease-parameter row is the twin: the same chain, with every send on its lease formal, is granted in full and reports
    // nothing, so the refusals are about the holder read and not about the fixture. The second send never reads the holder in
    // any row, and is granted in all of them: the refusal is the reading member's, not the whole run's.
    //
    // The demand is exactly this: an instance property or field whose declared type is IGrimoireWorkLease, declared in authored
    // source, read through a member access or a conditional access in the member's own body (not in a lambda or local function of
    // it), under an ordinary hosted root. AmbientHolderLeftToTheReceiverProof pins what that leaves to the Lease Receiver Proof, and
    // AmbientHolderIsRefusedInARecoveryHandler pins that a recovery handler is no exception.
    [Theory]
    [InlineData("lease-parameter")]
    [InlineData("holder-read")]
    [InlineData("holder-group")]
    [InlineData("holder-read-renamed-binder")]
    [InlineData("holder-read-without-binder")]
    [InlineData("holder-read-property-setter")]
    [InlineData("holder-read-constructor-captured")]
    [InlineData("holder-read-field")]
    [InlineData("holder-read-conditional-access")]
    public void AmbientHolderIsRefused(string shape)
    {
        AmbientHolderRun run = RunAmbientHolder(shape);

        HostedProducerDiscovery<HostedProducerSite> result = run.Result;

        // The second send is begun on its lease formal in every row, and the worker's Work reaches it.
        InvocationExpressionSyntax secondGroupSyntax = Assert.Single(run.Groups(run.SecondSend));

        HostedProducerSite secondGroup = run.Site(secondGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite second = run.Site(run.PhysicalSend(run.SecondSend), "System.IO.File.Delete");

        Assert.Equal(run.Acquired.CapsuleId, secondGroup.WorkFrontierCapsuleId);

        Assert.Equal(run.Acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.Equal(secondGroup.CapsuleId, second.EffectFrontierCapsuleId);

        InvocationExpressionSyntax physicalFirst = run.PhysicalSend(run.FirstSend);

        HostedProducerSite first = run.Site(physicalFirst, "System.IO.File.Delete");

        string firstCoordinate = "/site@" + run.Tree.FilePath + ":" + physicalFirst.SpanStart;

        if (shape == "lease-parameter")
        {
            InvocationExpressionSyntax firstGroupSyntax = Assert.Single(run.Groups(run.FirstSend));

            HostedProducerSite firstGroup = run.Site(firstGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.Equal(run.Acquired.CapsuleId, firstGroup.WorkFrontierCapsuleId);

            Assert.Equal(run.Acquired.CapsuleId, first.WorkFrontierCapsuleId);

            Assert.Equal(firstGroup.CapsuleId, first.EffectFrontierCapsuleId);

            Assert.NotEqual(first.EffectFrontierCapsuleId, second.EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);

            return;
        }

        // A holder read drops the Work of the member that reads it, so the send has none.
        Assert.Null(first.WorkFrontierCapsuleId);

        Assert.Null(first.EffectFrontierCapsuleId);

        HostedProducerInventoryDiagnostic missingWork = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Equal("System.IO.File.Delete", missingWork.Detail);

        Assert.EndsWith(firstCoordinate, missingWork.Identity, StringComparison.Ordinal);

        HostedProducerInventoryDiagnostic missingEffect = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.EndsWith(firstCoordinate, missingEffect.Identity, StringComparison.Ordinal);

        if (shape != "holder-group")
        {
            // Reading the holder is enough: there is no group to refuse, and nothing else is reported.
            Assert.Empty(run.Groups(run.FirstSend));

            Assert.Equal(2, result.Diagnostics.Count);

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

            return;
        }

        // A group begun on the holder's lease is refused as a receiver that is no must-alias of the work lease, and grants nobody
        // an Effect: neither itself nor the send under it.
        InvocationExpressionSyntax holderGroupSyntax = Assert.Single(run.Groups(run.FirstSend));

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

        Assert.EndsWith("/call@" + run.Tree.FilePath + ":" + holderGroupSyntax.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        Assert.StartsWith("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup;", mismatch.Detail, StringComparison.Ordinal);

        HostedProducerSite holderGroup = run.Site(holderGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        Assert.Null(holderGroup.WorkFrontierCapsuleId);

        Assert.Null(holderGroup.EffectFrontierCapsuleId);

        Assert.Equal(3, result.Diagnostics.Count);
    }

    private sealed record AmbientHolderRun(
        SyntaxTree Tree, SemanticModel Model, HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerSite Acquired,
        MethodDeclarationSyntax FirstSend, MethodDeclarationSyntax SecondSend)
    {
        public InvocationExpressionSyntax[] Groups(MethodDeclarationSyntax send) => [.. send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => Model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" })];

        // Every group in the tree that is not begun on the lease formal `admission`, wherever it sits.
        public InvocationExpressionSyntax[] GroupsNotOnTheFormal() => [.. Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => Model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryBeginExternalEffectGroup" }
                && call.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "admission" } })];

        // Every physical send in the tree except the second send's, which never reads a holder.
        public HostedProducerSite[] SendsBeforeTheSecond() => [.. Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static call => call.Expression.ToString() == "System.IO.File.Delete" && call.ArgumentList.ToString() != "(\"lease-parameter-second\")")
            .Select(call => Site(call, "System.IO.File.Delete"))];

        public InvocationExpressionSyntax PhysicalSend(MethodDeclarationSyntax send) => Assert.Single(send.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Delete");

        public HostedProducerSite Site(SyntaxNode node, string callee) => Assert.Single(Result.Items, site => site.RootType == "Worker"
            && site.Callee == callee && site.OperationId.EndsWith("/site@" + Tree.FilePath + ":" + node.SpanStart, StringComparison.Ordinal));
    }

    private AmbientHolderRun RunAmbientHolder(string shape) => RunAmbientHolder(shape, OrdinaryLeaseParameterFixture(shape));

    private AmbientHolderRun RunAmbientHolder(string shape, string source)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], hosted, [new("Worker", [root])], []);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"AMBIENT_HOLDER_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name && method.Parent is ClassDeclarationSyntax type && type.Identifier.ValueText == owner);

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(Method("Worker", "RunOneAsync").DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        AmbientHolderRun run = new(tree, model, result, null!, Method("LongRestSweepCoordinator", "SendFirstAsync"), Method("LongRestSweepCoordinator", "SendSecondAsync"));

        HostedProducerSite acquired = run.Site(acquisitionSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquired.AdmittedWorkKind);

        return run with { Acquired = acquired };
    }

    // What the demand does not read is left to the Lease Receiver Proof, and each of these spellings is still refused there: a lease
    // read by a simple name inside the holder itself, from a slot whose type is a class that implements the interface, returned by a
    // method of the holder, read in a lambda, or taken out by a property pattern. The member that reads it keeps the worker's Work,
    // because the demand is on the two spellings of a slot read and not on every way a lease can come out of an object, but a group
    // begun on the value it read is HOSTED_ADMISSION_RECEIVER_MISMATCH and grants nobody an Effect, so the send under it holds no
    // Effect and the discovery is refused. The second send is on its lease formal and is granted in full, as in every other row.
    [Theory]
    [InlineData("simple-name-inside-the-holder")]
    [InlineData("concrete-lease-typed-slot")]
    [InlineData("method-return")]
    [InlineData("lambda-body")]
    [InlineData("property-pattern")]
    public void AmbientHolderLeftToTheReceiverProofIsStillRefusedAtItsGroup(string shape)
    {
        AmbientHolderRun run = RunAmbientHolder(shape, AmbientHolderLeftToTheReceiverProofSource(shape));

        InvocationExpressionSyntax secondGroupSyntax = Assert.Single(run.Groups(run.SecondSend));

        HostedProducerSite secondGroup = run.Site(secondGroupSyntax, "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

        HostedProducerSite second = run.Site(run.PhysicalSend(run.SecondSend), "System.IO.File.Delete");

        Assert.Equal(run.Acquired.CapsuleId, second.WorkFrontierCapsuleId);

        Assert.Equal(secondGroup.CapsuleId, second.EffectFrontierCapsuleId);

        InvocationExpressionSyntax refused = Assert.Single(run.GroupsNotOnTheFormal());

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

        Assert.EndsWith("/call@" + run.Tree.FilePath + ":" + refused.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        HostedProducerSite refusedGroup = Assert.Single(run.Result.Items, site => site.RootType == "Worker"
            && site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal)
            && site.OperationId.EndsWith("/site@" + run.Tree.FilePath + ":" + refused.SpanStart, StringComparison.Ordinal));

        Assert.Null(refusedGroup.EffectFrontierCapsuleId);

        Assert.All(run.SendsBeforeTheSecond(), static send => Assert.Null(send.EffectFrontierCapsuleId));

        Assert.Contains(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    private static string AmbientHolderLeftToTheReceiverProofSource(string shape)
    {
        const string read = "IGrimoireWorkLease ambient = holder.Lease;";

        const string group = "if (!ambient.TryBeginExternalEffectGroup(out IDisposable held)) return; using var kept = held; ";

        const string setter = "internal IGrimoireWorkLease Lease { get; set; } = null!;";

        string Sourced(string fixture, string before, string after) => ReplaceExactlyOnce(fixture, before, after);

        return shape switch
        {
            "simple-name-inside-the-holder" => Sourced(
                Sourced(OrdinaryLeaseParameterFixture("holder-read-property-setter"), setter,
                    setter + " internal async Task SendViaHolderAsync() { if (!Lease.TryBeginExternalEffectGroup(out IDisposable held)) return; using var kept = held; await Task.CompletedTask; System.IO.File.Delete(\"via-holder\"); }"),
                read, "await holder.SendViaHolderAsync();"),
            "concrete-lease-typed-slot" => Sourced(
                Sourced(OrdinaryLeaseParameterFixture("holder-read-property-setter"), setter,
                    "internal HolderLease Lease { get; set; } = null!; } internal sealed class HolderLease : IGrimoireWorkLease { public bool TryBeginExternalEffectGroup(out IDisposable group) { group = null!; return true; } public void Dispose() { } public ValueTask DisposeAsync() => default;"),
                read, "HolderLease ambient = holder.Lease; " + group),
            "method-return" => Sourced(
                Sourced(OrdinaryLeaseParameterFixture("holder-read-property-setter"), setter,
                    "private IGrimoireWorkLease? kept; internal IGrimoireWorkLease GetLease() => kept!;"),
                read, "IGrimoireWorkLease ambient = holder.GetLease(); " + group),
            "lambda-body" => Sourced(OrdinaryLeaseParameterFixture("holder-read-field"), read,
                "Func<IGrimoireWorkLease> read = () => holder.Lease; IGrimoireWorkLease ambient = read(); " + group),
            "property-pattern" => Sourced(OrdinaryLeaseParameterFixture("holder-read-field"), read,
                "if (holder is not { Lease: var ambient }) return; " + group),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }

    private static string ReplaceExactlyOnce(string source, string before, string after)
    {
        Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

        return source.Replace(before, after, StringComparison.Ordinal);
    }

    // The same refusal in a recovery handler. A recovery handler is dispatched from a shell that holds the worker's Work and begins
    // the recovery effect, and it carries a recovery context, which once made it the one member the refusal did not apply to. It is
    // judged like any other: a handler that reads a lease out of a holder takes no Work and no Effect from the shell, so its send
    // says so, and a group it begins on that lease is HOSTED_ADMISSION_RECEIVER_MISMATCH. The unmodified recovery matrix is the twin
    // (the handler's send holds the shell's Work and the shell's group as its Effect, and nothing is reported).
    [Theory]
    [InlineData("recovery-twin")]
    [InlineData("holder-read")]
    [InlineData("holder-group")]
    public void AmbientHolderIsRefusedInARecoveryHandler(string shape)
    {
        const string read = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease ambient = holder.Lease;";

        string source = shape switch
        {
            "recovery-twin" => RecoveryHandlerSource(""),
            "holder-read" => RecoveryHandlerSource(read + " _ = ambient;", holder: true),
            "holder-group" => RecoveryHandlerSource(read + RecoveryHandlerGroup, holder: true),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        RecoveryHandlerRun run = RunRecoveryHandler(shape, source);

        if (shape == "recovery-twin")
        {
            Assert.Empty(run.Result.Diagnostics);

            Assert.Equal(run.ShellGroup.CapsuleId, run.ShellGroup.EffectFrontierCapsuleId);

            Assert.NotNull(run.HandlerSend.WorkFrontierCapsuleId);

            Assert.Equal(run.ShellGroup.EffectFrontierCapsuleId, run.HandlerSend.EffectFrontierCapsuleId);

            return;
        }

        Assert.Null(run.HandlerSend.WorkFrontierCapsuleId);

        Assert.Null(run.HandlerSend.EffectFrontierCapsuleId);

        string sendCoordinate = "/site@" + run.Tree.FilePath + ":" + run.Send.SpanStart;

        HostedProducerInventoryDiagnostic missingWork = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

        Assert.Equal("System.IO.File.Delete", missingWork.Detail);

        Assert.EndsWith(sendCoordinate, missingWork.Identity, StringComparison.Ordinal);

        HostedProducerInventoryDiagnostic missingEffect = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

        Assert.EndsWith(sendCoordinate, missingEffect.Identity, StringComparison.Ordinal);

        if (shape == "holder-read")
        {
            Assert.Equal(2, run.Result.Diagnostics.Count);

            return;
        }

        InvocationExpressionSyntax groupSyntax = run.Group("ambient");

        HostedProducerInventoryDiagnostic mismatch = Assert.Single(run.Result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_ADMISSION_RECEIVER_MISMATCH");

        Assert.EndsWith("/call@" + run.Tree.FilePath + ":" + groupSyntax.SpanStart, mismatch.Identity, StringComparison.Ordinal);

        Assert.Equal(3, run.Result.Diagnostics.Count);
    }

    private const string ExternalRecoveryHandlerType = "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler";

    private const string RecoveryHandlerKind = "public string Kind => LongRunningOperationKinds.External;";

    private const string RecoveryHandlerSend = "System.IO.File.Delete(\"external\");";

    // A group begun on the local `ambient`, held for the rest of the handler's body.
    private const string RecoveryHandlerGroup = " if (!ambient.TryBeginExternalEffectGroup(out System.IDisposable group)) return Task.CompletedTask; using var held = group;";

    // A holder of a lease and a lease nobody acquired, for the recovery handler to read or to begin a group on.
    private const string RecoveryHolderTypes = """

        namespace RetroDownfall.Arcanum.Infrastructure.Operations
        {
            internal sealed class RecoveryLeaseHolder
            {
                internal RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease Lease { get; set; } = null!;
            }

            internal sealed class RecoveryFakeLease : RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease
            {
                public bool TryBeginExternalEffectGroup(out System.IDisposable group)
                {
                    group = null!;

                    return true;
                }

                public void Dispose() { }

                public System.Threading.Tasks.ValueTask DisposeAsync() => default;
            }
        }
        """;

    // The recovery matrix with `body` written before the external handler's send. A handler that reads a holder is given one as a field.
    private static string RecoveryHandlerSource(string body, bool holder = false)
    {
        string source = ReplaceExactlyOnce(RecoveryMatrixFixture() + RecoveryHolderTypes, RecoveryHandlerSend, body + " " + RecoveryHandlerSend);

        return holder ? ReplaceExactlyOnce(source, RecoveryHandlerKind, "private readonly RecoveryLeaseHolder holder = new(); " + RecoveryHandlerKind) : source;
    }

    private sealed record RecoveryHandlerRun(SyntaxTree Tree, SemanticModel Model, HostedProducerDiscovery<HostedProducerSite> Result, InvocationExpressionSyntax Send)
    {
        // The external handler's send, and the group the shell began for it.
        public HostedProducerSite HandlerSend => Assert.Single(Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == ExternalRecoveryHandlerType && site.Callee == LeaseReceiverSend);

        public HostedProducerSite HandlerGroup => Assert.Single(Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == ExternalRecoveryHandlerType && site.Callee == LeaseReceiverGroup);

        public HostedProducerSite ShellGroup => Assert.Single(Result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Callee == LeaseReceiverGroup);

        // The group the handler begins on the local of that name.
        public InvocationExpressionSyntax Group(string receiver) => Assert.Single(Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TryBeginExternalEffectGroup", Expression: IdentifierNameSyntax name }
                && name.Identifier.ValueText == receiver);
    }

    private RecoveryHandlerRun RunRecoveryHandler(string shape, string source)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        InvocationExpressionSyntax send = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.ToString() == "System.IO.File.Delete(\"external\")");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture([compilation]);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine($"RECOVERY_HANDLER_DIAGNOSTIC shape={shape} {diagnostic.Code} {diagnostic.Identity} {diagnostic.Detail}");
        }

        return new(tree, compilation.GetSemanticModel(tree), result, send);
    }
}
