using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Proven Acquisition Grammar. The startup service's per-operation settlement (SettleAdmittedAsync) and the reconciler's
    // runtime claim (SettleDiscoveredRuntimeAsync) are the shape the checker already certifies for recovery handlers. The
    // only change from the base text is the Finisher Ambient Fix: the recovery lease is acquired inline in the method body,
    // so the gate's ambient lifetime stays in the caller's execution context. Nothing binds, stores or links the lease.
    private const string SettleAdmittedStartupType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService";

    private const string SettleAdmittedReconcilerType = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler";

    // Whitespace-insensitive SHA-256 of each method's text. SettleDiscoveredRuntimeAsync is byte-identical to the base
    // (b15d6960); SettleAdmittedAsync differs from it only by the inline acquisition hunk. Any other edit must be a decision.
    private const string SettleAdmittedTextDigest = "5A04B39544819B040A62C18B8BBE14B420C7371C5B9E8D2F4FC8ECEA185C7486";

    private const string SettleDiscoveredRuntimeTextDigest = "B312CA4CFAED8FEBA7FD7824631D2720EAEB7CAB7D42AB1A6CA81CD5736D51BA";

    [Fact]
    public void SettleAdmittedGrammarProductionTextIsThePinnedInlineAcquisitionShell()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        ActualLongRestMember settle = ActualLongRestMemberOf(infrastructure, SettleAdmittedStartupType, "SettleAdmittedAsync");

        ActualLongRestMember claim = ActualLongRestMemberOf(infrastructure, SettleAdmittedReconcilerType, "SettleDiscoveredRuntimeAsync");

        Assert.Equal(SettleAdmittedTextDigest, SettleGrammarDigest(settle.Syntax));

        Assert.Equal(SettleDiscoveredRuntimeTextDigest, SettleGrammarDigest(claim.Syntax));

        // The acquisition is inline: a statement of the loop body in this very method, never an awaited helper.
        InvocationExpressionSyntax acquisition = ActualLongRestCallOf(settle, ActualLongRestGateType, "TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRunningOperationRecovery, (GrimoireWorkKind)Convert.ToInt32(
            Assert.IsAssignableFrom<IFieldSymbol>(settle.Model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).ConstantValue));

        IfStatementSyntax refusal = Assert.IsType<IfStatementSyntax>(acquisition.Ancestors().OfType<StatementSyntax>().First());

        WhileStatementSyntax loop = Assert.IsType<WhileStatementSyntax>(refusal.Parent!.Parent);

        Assert.Same(settle.Syntax.Body, loop.Parent);

        Assert.DoesNotContain(settle.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText is "AcquireWorkLeaseAsync" or "Bind" or "LongRestWorkAdmission");

        // The lease is a plain local: it is never awaited-using'd, stored, captured, or handed to a callee. It is only the
        // group receiver, the generation read and the disposal in the outermost finally.
        VariableDeclaratorSyntax lease = Assert.Single(settle.Syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(), static variable => variable.Identifier.ValueText == "lease");

        ILocalSymbol leaseSymbol = Assert.IsAssignableFrom<ILocalSymbol>(settle.Model.GetDeclaredSymbol(lease));

        string[] uses = [.. settle.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(name => SymbolEqualityComparer.Default.Equals(settle.Model.GetSymbolInfo(name).Symbol, leaseSymbol))
            .Select(static name => name.Parent is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : "(bare)")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["DisposeAsync", "Generation", "TryBeginExternalEffectGroup"], uses);

        Assert.DoesNotContain(settle.Syntax.DescendantNodes(), static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        // One group guard, one claim, and the claim is the guard's else branch inside the try: the exact shape
        // RuntimeRecoveryDispatchRejectsNonGenericTuples and RecoveryEffectFrontierAt certify.
        InvocationExpressionSyntax group = ActualLongRestCallOf(settle, ActualLongRestLeaseType, "TryBeginExternalEffectGroup");

        IfStatementSyntax guard = Assert.IsType<IfStatementSyntax>(group.Ancestors().OfType<StatementSyntax>().First());

        Assert.Contains("OrdinaryExternalEffect", guard.Condition.ToString(), StringComparison.Ordinal);

        InvocationExpressionSyntax settled = Assert.Single(settle.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SettleDiscoveredRuntimeAsync" });

        Assert.True(guard.Else!.Span.Contains(settled.Span));

        Assert.Equal(["operation", "ownerId", "cancellationToken"], settled.ArgumentList.Arguments.Select(static argument => argument.Expression.ToString()));

        Assert.Contains(settled.Ancestors(), static ancestor => ancestor is TryStatementSyntax);

        Assert.Equal(["IGrimoireWorkLease lease = admitted!;"], settle.Syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(static declaration => declaration.Declaration.Variables.Any(static variable => variable.Identifier.ValueText == "lease"))
            .Select(static declaration => declaration.ToString()));
    }

    [Theory]
    [InlineData("inline-acquisition")]
    [InlineData("holder-bind-control")]
    public void SettleAdmittedGrammarFixtureCertifiesTheProductionShellAndALeaseHolderLosesWork(string shape)
    {
        string source = SettleAdmittedGrammarFixture(shape);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        // The background pass is ordinary hosted work whose declared kind is the recovery kind, exactly as the catalog says.
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery }])],
            [new("RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot.Run", "src/Fixture.cs",
                "RetroDownfall.Arcanum.Infrastructure.Operations.OwnerRecoveryRoot", "Run", HostedProducerAuthorityKind.OwnerBoundRecovery, "exact owner fixture", [])]);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite[] handlerSends = [.. result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType is "RetroDownfall.Arcanum.Infrastructure.Operations.ExternalRecoveryHandler"
                or "RetroDownfall.Arcanum.Infrastructure.Operations.DbRecoveryHandler")];

        Assert.True(handlerSends.Length == 2, $"Handler sends {handlerSends.Length}; items=[{string.Join(" | ", result.Items.Select(static site => site.RootType + ":" + site.EnclosingType + ":" + site.Callee))}] diagnostics=[{string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ":" + diagnostic.Identity + ":" + diagnostic.Detail))}]");

        if (shape == "inline-acquisition")
        {
            // The shell certifies: no recovery-matrix refusal, no missing frontier, and the handlers' effects carry the
            // inline recovery lease as their Work. Only the external-effect handler is inside the shell's group.
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal)
                || diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            HostedProducerSite group = Assert.Single(result.Items, static site => site.RootType == "Worker"
                && site.EnclosingType == "Worker" && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

            Assert.All(handlerSends, send => Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId));

            HostedProducerSite external = Assert.Single(handlerSends, static send => send.Callee == "System.IO.File.Delete");

            Assert.Equal(group.CapsuleId, external.EffectFrontierCapsuleId);

            Assert.Null(Assert.Single(handlerSends, static send => send.Callee == "System.IO.File.Exists").EffectFrontierCapsuleId);
        }
        else
        {
            // Control: re-adding a holder Bind (a heap store of the lease) ends the Work, so the handlers' effects lose their
            // admission and the checker refuses. This is the loss the holder produced and the inline shell does not.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

            Assert.Contains(handlerSends, static send => send.WorkFrontierCapsuleId is null);
        }
    }

    private static string SettleGrammarDigest(MethodDeclarationSyntax method) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(method.ToString().Where(static character => !char.IsWhiteSpace(character))))));

    // The recovery matrix fixture, with its hand-written startup shell replaced by the production text of
    // SettleAdmittedAsync (extracted from the real compilation) and the reconciler's claim given the production signature.
    private static string SettleAdmittedGrammarFixture(string shape)
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        string settle = ActualLongRestMemberOf(infrastructure, SettleAdmittedStartupType, "SettleAdmittedAsync").Syntax.ToString();

        if (shape == "holder-bind-control")
        {
            const string acquired = "IGrimoireWorkLease lease = admitted!;";

            Assert.Equal(1, settle.Split(acquired, StringSplitOptions.None).Length - 1);

            settle = settle.Replace(acquired, acquired + " holder.Bind(lease);", StringComparison.Ordinal);
        }

        string source = RecoveryMatrixFixture();

        string Replace(string original, string replacement)
        {
            Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

            return source = source.Replace(original, replacement, StringComparison.Ordinal);
        }

        _ = Replace(AdmissionTypes, """
            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRunningOperationRecovery = 14, LongRestSweep = 17 }

                public interface IGrimoireExternalEffectGroup : System.IAsyncDisposable { }

                public interface IGrimoireWorkLease : System.IAsyncDisposable
                {
                    long Generation { get; }

                    bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup);
                }

                public interface IGrimoireConnectionAdmissionGate
                {
                    long CurrentGeneration { get; }

                    bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease);

                    System.Threading.Tasks.Task<long> WaitForNextOpenGenerationAsync(long observedGeneration, System.Threading.CancellationToken cancellationToken);

                    System.Threading.Tasks.Task<long> WaitForOpenGenerationAfterRefusalAsync(long refusedGeneration, System.Threading.CancellationToken cancellationToken)
                    {
                        System.ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refusedGeneration);

                        return WaitForNextOpenGenerationAsync(refusedGeneration - 1, cancellationToken);
                    }
                }
            }
            """);

        int start = source.IndexOf("public async Task StartAsync(CancellationToken token) {", StringComparison.Ordinal);

        const string tail = "effectGroup.Dispose(); } }";

        int end = source.IndexOf(tail, start, StringComparison.Ordinal) + tail.Length;

        Assert.True(start >= 0 && end > start);

        string worker = """
            private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory = null!;

            private readonly RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate admissionGate = null!;

            private readonly Holder holder = new();

            public async Task StartAsync(CancellationToken token)
            {
                var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(RetroDownfall.Arcanum.Core.Operations.LongRunningOperationKinds.External, 0);

                var decision = RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationRecoveryAdmission.Classify(operation, null);

                _ = await SettleAdmittedAsync(operation, "owner", decision, token).ConfigureAwait(false);
            }

            """ + settle + """


            private sealed class Holder
            {
                private RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease? current;

                internal void Bind(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease lease) => current = lease;
            }
            """;

        source = source[..start] + worker + source[end..];

        _ = Replace("internal async Task SettleDiscoveredRuntimeAsync(LongRunningOperation discovered)",
            "internal async Task<LongRunningOperationSettlementOutcome> SettleDiscoveredRuntimeAsync(LongRunningOperation discovered, string ownerId, CancellationToken cancellationToken)");

        _ = Replace("/*unsupported-rejection*/ return;", "/*unsupported-rejection*/ return LongRunningOperationSettlementOutcome.RequiresAttention;");

        _ = Replace("await SettleLeasedAsync(discovered);", "await SettleLeasedAsync(discovered); return LongRunningOperationSettlementOutcome.Completed;");

        _ = Replace("internal sealed class LongRunningRecoveryOwnerEvidence { }",
            "internal sealed class LongRunningRecoveryOwnerEvidence { } internal enum LongRunningOperationSettlementOutcome : byte { OwnedInProcess = 1, Completed = 3, RequiresAttention = 6, ConcurrencyLost = 7 }");

        _ = Replace("services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.DbRecoveryHandler>();",
            "services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler>(); "
            + "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.DbRecoveryHandler>();");

        return "using RetroDownfall.Arcanum.Core.Operations; using RetroDownfall.Arcanum.Infrastructure.Data; using RetroDownfall.Arcanum.Infrastructure.Operations;\n" + source;
    }
}
