using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("owned-lease")]
    [InlineData("missing-first-group")]
    [InlineData("early-lease-disposal")]
    [InlineData("early-binding-disposal")]
    [InlineData("detached-helper")]
    public void Readiness_explicit_lease_shell_requires_actual_ownership_binding_and_join(string shape)
    {
        string source = ExplicitReadinessSelfAdmissionFixture(shape);

        AssertExplicitReadinessLeasePremises(source, "PerUnitRecoveryHandler");

        AssertExplicitReadinessDiscovery(source, "PerUnitRecoveryHandler", shape);
    }

    [Theory]
    [InlineData("inner-scope")]
    [InlineData("foreign-prebinding")]
    [InlineData("constructor-prebinding")]
    [InlineData("other-handler-prebinding")]
    [InlineData("early-scope-disposal")]
    [InlineData("detached-settlement")]
    public void Readiness_explicit_lease_shell_uses_actual_inner_provider_and_fresh_holder_storage(string shape)
    {
        string source = ExplicitReadinessScopedFixture(shape);

        AssertExplicitReadinessLeasePremises(source, "PerUnitCoordinator");

        CSharpCompilation compilation = Compile(source);

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax services = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "GetServices" }
            && call.Ancestors().OfType<MethodDeclarationSyntax>().Any(static method => method.Identifier.ValueText == "ReconcileAsync"));

        AnonymousFunctionExpressionSyntax callback = Assert.Single(services.Ancestors().OfType<AnonymousFunctionExpressionSyntax>());

        InvocationExpressionSyntax creation = Assert.Single(callback.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "CreateAsyncScope" });

        LocalDeclarationStatementSyntax declaration = creation.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();

        Assert.NotEqual(0, declaration.AwaitKeyword.RawKind);

        Assert.NotEqual(0, declaration.UsingKeyword.RawKind);

        Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(creation.Expression).Expression).Symbol);

        AssertExplicitReadinessDiscovery(source, "PerUnitCoordinator", shape == "inner-scope" ? "owned-lease" : shape);
    }

    private void AssertExplicitReadinessLeasePremises(string source, string owner)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax holder = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRestWorkAdmission");

        Assert.DoesNotContain(holder.Members, static member => member is MethodDeclarationSyntax { Identifier.ValueText: "Enter" }
            or ClassDeclarationSyntax { Identifier.ValueText: "Entry" });

        MethodDeclarationSyntax acquire = Assert.Single(holder.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "Acquire");

        Assert.Equal("IGrimoireWorkLease", model.GetDeclaredSymbol(acquire)!.ReturnType.Name);

        InvocationExpressionSyntax acquisition = Assert.Single(acquire.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "TryAcquireWorkLease" });

        Assert.Equal("LongRestSweep", Assert.IsAssignableFrom<IFieldSymbol>(model.GetSymbolInfo(acquisition.ArgumentList.Arguments[0].Expression).Symbol).Name);

        MethodDeclarationSyntax recover = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), method =>
            method.Identifier.ValueText == "RecoverAsync" && method.Parent is ClassDeclarationSyntax type && type.Identifier.ValueText == owner);

        Assert.Single(recover.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(), static declaration =>
            declaration.UsingKeyword.RawKind != 0 && declaration.AwaitKeyword.RawKind != 0);

        MethodDeclarationSyntax admitted = Assert.Single(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), method =>
            method.Identifier.ValueText == "RecoverAdmittedAsync" && method.Parent is ClassDeclarationSyntax type && type.Identifier.ValueText == owner);

        Assert.Contains(model.GetDeclaredSymbol(admitted)!.Parameters, static parameter => parameter.Type.Name == "IGrimoireWorkLease");

        output.WriteLine($"R_EXPLICIT_READINESS_PREMISES={owner}|compilerAndActualReturnedLeaseAndOwnedShell=passed");
    }

    private static void AssertExplicitReadinessDiscovery(string source, string owner, string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture(source,
            hostedAuthority: HostedProducerAuthorityKind.PreReadinessStartup);

        HostedProducerSite[] sends = result.Items.Where(site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations." + owner
            && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(new[] { "SendFirstAsync", "SendSecondAsync" }, sends.Select(static send => send.Member).Distinct().Order().ToArray());

        if (shape == "owned-lease")
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code.StartsWith("HOSTED_RECOVERY_", StringComparison.Ordinal));

            HostedProducerSite ownWork = Assert.Single(result.Items, static site => site.RootType == "Worker"
                && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission"
                && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

            Assert.All(sends, send =>
            {
                Assert.Equal(ownWork.CapsuleId, send.WorkFrontierCapsuleId);

                Assert.NotNull(send.EffectFrontierCapsuleId);
            });

            Assert.Single(sends.Where(static send => send.Member == "SendFirstAsync").Select(static send => send.EffectFrontierCapsuleId).Distinct());

            Assert.Single(sends.Where(static send => send.Member == "SendSecondAsync").Select(static send => send.EffectFrontierCapsuleId).Distinct());

            Assert.Equal(2, sends.Select(static send => send.EffectFrontierCapsuleId).Distinct().Count());
        }
        else if (shape == "missing-first-group")
        {
            Assert.All(sends.Where(static send => send.Member == "SendFirstAsync"), static send => Assert.Null(send.EffectFrontierCapsuleId));

            Assert.All(sends.Where(static send => send.Member == "SendSecondAsync"), static send => Assert.NotNull(send.EffectFrontierCapsuleId));
        }
        else
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

            Assert.DoesNotContain(sends, static send => send.WorkFrontierCapsuleId is not null && send.EffectFrontierCapsuleId is not null);
        }
    }

    private static string ExplicitReadinessSelfAdmissionFixture(string shape)
    {
        Assert.Contains(shape, new[] { "owned-lease", "missing-first-group", "early-lease-disposal", "early-binding-disposal", "detached-helper" });

        string source = ReadinessSelfAdmissionFixture(shape == "missing-first-group" ? "missing-first-group" : "owned-entry");

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        ClassDeclarationSyntax holder = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRestWorkAdmission");

        holder = holder.WithMembers(SyntaxFactory.List(holder.Members.Where(static member => member is not
            MethodDeclarationSyntax { Identifier.ValueText: "Enter" } and not ClassDeclarationSyntax { Identifier.ValueText: "Entry" })))
            .AddMembers(SyntaxFactory.ParseMemberDeclaration("internal bool HasLease => current is not null;")!,
                SyntaxFactory.ParseMemberDeclaration("""
                    internal IGrimoireWorkLease Acquire(CancellationToken ct)
                    {
                        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease lease))
                        {
                            ct.ThrowIfCancellationRequested();

                            throw new OperationCanceledException(ct);
                        }

                        if (lease is null) throw new InvalidOperationException();

                        return lease;
                    }
                    """)!);

        unit = unit.ReplaceNode(Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRestWorkAdmission"), holder);

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        MethodDeclarationSyntax recover = Assert.Single(handler.Members.OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "RecoverAsync");

        string release = shape switch
        {
            "early-lease-disposal" => "await acquired.DisposeAsync();",
            "early-binding-disposal" => "binding.Dispose();",
            _ => "",
        };

        string join = shape == "detached-helper" ? "_ =" : "await";

        MethodDeclarationSyntax shell = recover.WithBody((BlockSyntax)SyntaxFactory.ParseStatement($$"""
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (workAdmission.HasLease)
                {
                    await RecoverAdmittedAsync(operation, workAdmission.Lease, cancellationToken);

                    return;
                }

                await using IGrimoireWorkLease acquired = workAdmission.Acquire(cancellationToken);

                using IDisposable binding = workAdmission.Bind(acquired);

                {{release}}

                {{join}} RecoverAdmittedAsync(operation, acquired, cancellationToken);
            }
            """));

        MethodDeclarationSyntax admitted = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("""
            private async Task RecoverAdmittedAsync(LongRunningOperation operation, IGrimoireWorkLease admission, CancellationToken cancellationToken)
            {
                await SendFirstAsync();

                await SendSecondAsync();
            }
            """)!;

        return unit.ReplaceNode(handler, handler.ReplaceNode(recover, shell).AddMembers(admitted)).ToFullString();
    }

    private static string ExplicitReadinessScopedFixture(string shape)
    {
        Assert.Contains(shape, new[] { "inner-scope", "foreign-prebinding", "constructor-prebinding", "other-handler-prebinding", "early-scope-disposal", "detached-settlement" });

        string source = ExplicitReadinessSelfAdmissionFixture("owned-lease");

        void ReplaceOne(string before, string after)
        {
            Assert.Equal(1, source.Split(before, StringSplitOptions.None).Length - 1);

            source = source.Replace(before, after, StringComparison.Ordinal);
        }

        ReplaceOne("public class Worker : IHostedService", "public class Worker(IServiceScopeFactory scopeFactory) : IHostedService");

        ReplaceOne("new Worker();", "new Worker(provider.GetRequiredService<IServiceScopeFactory>());");

        const string handlerRegistration = "services.AddScoped<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler, RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitRecoveryHandler>();";

        ReplaceOne(handlerRegistration, handlerRegistration + " " + """
            services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>();
            services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.PerUnitCoordinator>();
            services.AddScoped<RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler>(static sp =>
                new RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler(
                    sp.GetServices<RetroDownfall.Arcanum.Core.Operations.ILongRunningOperationRecoveryHandler>(),
                    sp.GetRequiredService<IServiceScopeFactory>()));
            """);

        CompilationUnitSyntax unit = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax start = Assert.Single(unit.DescendantNodes().OfType<MethodDeclarationSyntax>(), static method =>
            method.Identifier.ValueText == "StartAsync" && method.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        unit = unit.ReplaceNode(start, start.WithBody((BlockSyntax)SyntaxFactory.ParseStatement("""
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var operation = new RetroDownfall.Arcanum.Core.Operations.LongRunningOperation(
                    RetroDownfall.Arcanum.Core.Operations.LongRunningOperationKinds.PerUnit, 0);
                await scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationReconciler>()
                    .ReconcileAsync(operation);
            }
            """)));

        ClassDeclarationSyntax handler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "PerUnitRecoveryHandler");

        ClassDeclarationSyntax coordinator = handler.WithIdentifier(SyntaxFactory.Identifier("PerUnitCoordinator"))
            .WithBaseList(null)
            .WithMembers(SyntaxFactory.List(handler.Members.Where(static member => member is not PropertyDeclarationSyntax)));

        if (shape == "constructor-prebinding")
        {
            coordinator = coordinator.AddMembers(SyntaxFactory.ParseMemberDeclaration("""
                private readonly IDisposable foreignBinding = workAdmission.Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());
                """)!);
        }

        ClassDeclarationSyntax forwarding = (ClassDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("""
            internal sealed class PerUnitRecoveryHandler(PerUnitCoordinator coordinator) : ILongRunningOperationRecoveryHandler
            {
                public string Kind => LongRunningOperationKinds.PerUnit;

                public int SupportedCheckpointVersion => 0;

                public Task RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken) =>
                    coordinator.RecoverAsync(operation, cancellationToken);
            }
            """)!;

        unit = unit.ReplaceNode(handler, new[] { coordinator, forwarding });

        ClassDeclarationSyntax reconciler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
            type.Identifier.ValueText == "LongRunningOperationReconciler");

        string prebinding = shape == "foreign-prebinding"
            ? "using var foreign = operationScope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission>().Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());" : "";

        string release = shape == "early-scope-disposal" ? "await operationScope.DisposeAsync();" : "";

        string join = shape == "detached-settlement" ? "_ =" : "await";

        ClassDeclarationSyntax selected = (ClassDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($$"""
            internal sealed class LongRunningOperationReconciler
            {
                private readonly IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> _handlers;

                private readonly IServiceScopeFactory? _scopeFactory;

                internal LongRunningOperationReconciler(object? discovery = null, object? classifiedLeaseAcquisition = null, object? scopeFactory = null, RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission? workAdmission = null)
                {
                    _handlers = new Dictionary<string, ILongRunningOperationRecoveryHandler>();
                }

                internal LongRunningOperationReconciler(IEnumerable<ILongRunningOperationRecoveryHandler> handlers, IServiceScopeFactory scopeFactory)
                {
                    ILongRunningOperationRecoveryHandler[] materializedHandlers = [.. handlers];

                    _handlers = materializedHandlers.ToDictionary(static handler => handler.Kind, StringComparer.Ordinal);

                    _scopeFactory = scopeFactory;
                }

                internal async Task SettleDiscoveredRuntimeAsync(LongRunningOperation discovered)
                {
                    LongRunningRecoveryAdmissionDecision before = LongRunningOperationRecoveryAdmission.Classify(discovered, null);

                    if (before.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner) throw new InvalidOperationException();

                    LongRunningRecoveryAdmissionDecision after = LongRunningOperationRecoveryAdmission.Classify(discovered, null);

                    if (after.Kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion) return;

                    await SettleLeasedAsync(_handlers, discovered);
                }

                internal async Task SettleExactlyAsync(LongRunningOperation operation, LongRunningRecoveryOwnerEvidence ownerEvidence)
                {
                    LongRunningRecoveryAdmissionDecision decision = LongRunningOperationRecoveryAdmission.Classify(operation, ownerEvidence);

                    if (decision.Kind is not LongRunningRecoveryAdmissionKind.OwnerBoundOffline) return;

                    await SettleLeasedAsync(_handlers, operation);
                }

                internal async Task ReconcileAsync(LongRunningOperation discovered)
                {
                    async Task SettleAsync(IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> handlersForOperation, LongRunningOperation operation)
                    {
                        LongRunningRecoveryAdmissionDecision admission = LongRunningOperationRecoveryAdmission.Classify(operation, null);

                        if (admission.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner) return;

                        await (admission.Kind is LongRunningRecoveryAdmissionKind.UnsupportedCheckpointVersion
                            ? Task.CompletedTask
                            : SettleLeasedAsync(handlersForOperation, operation));
                    }

                    await Parallel.ForEachAsync(new[] { discovered }, async (operation, ct) =>
                    {
                        if (_scopeFactory is null)
                        {
                            await SettleAsync(_handlers, operation);

                            return;
                        }

                        await using var operationScope = _scopeFactory.CreateAsyncScope();

                        {{prebinding}}

                        Dictionary<string, ILongRunningOperationRecoveryHandler> scopedHandlers = operationScope.ServiceProvider
                            .GetServices<ILongRunningOperationRecoveryHandler>()
                            .ToDictionary(static handler => handler.Kind, StringComparer.Ordinal);

                        {{release}}

                        {{join}} SettleAsync(scopedHandlers, operation);
                    });
                }

                private async Task SettleLeasedAsync(IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> handlersForOperation, LongRunningOperation operation) =>
                    await RecoverOneAsync(handlersForOperation, operation);

                private async Task RecoverOneAsync(IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> handlersForOperation, LongRunningOperation operation)
                {
                    if (!handlersForOperation.TryGetValue(operation.Kind, out ILongRunningOperationRecoveryHandler? handler)) return;

                    await handler.RecoverAsync(operation, CancellationToken.None);
                }
            }
            """)!;

        unit = unit.ReplaceNode(reconciler, selected);

        if (shape == "other-handler-prebinding")
        {
            ClassDeclarationSyntax otherHandler = Assert.Single(unit.DescendantNodes().OfType<ClassDeclarationSyntax>(), static type =>
                type.Identifier.ValueText == "ExternalRecoveryHandler");

            unit = unit.ReplaceNode(otherHandler, otherHandler.WithParameterList(SyntaxFactory.ParseParameterList(
                "(RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission workAdmission)"))
                .AddMembers(SyntaxFactory.ParseMemberDeclaration("""
                    private readonly IDisposable foreignBinding = workAdmission.Bind(new RetroDownfall.Arcanum.Infrastructure.Data.LongRest.ReadinessWorkLease());
                    """)!));
        }

        return "using System.Collections.Generic; using System.Linq; using Microsoft.Extensions.DependencyInjection;\n" + unit.ToFullString();
    }
}
