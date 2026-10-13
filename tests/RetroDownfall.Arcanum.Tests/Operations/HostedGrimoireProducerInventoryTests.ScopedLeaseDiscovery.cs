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
    [Theory]
    [InlineData("local-direct", true)]
    [InlineData("di-direct", true)]
    [InlineData("local-stable-out", true)]
    [InlineData("di-stable-out", true)]
    [InlineData("local-reassigned-out", false)]
    [InlineData("local-opaque-out", false)]
    [InlineData("di-ambiguous", false)]
    [InlineData("converted-gate", false)]
    public void ScopedLeaseDiscoveryRequiresExactGateAndStableSuccessfulOutSource(string shape, bool preserves)
    {
        const string gateContract = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate";

        const string leaseContract = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

        const string proofCallee = leaseContract + ".ProveDrainedMaintenance";

        string source = OrdinaryScopedHolderFixture("same-scoped-lease");

        const string protocol = "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); }";

        source = source.ReplaceSdkFixtureText(protocol,
            "public interface IGrimoireWorkLease : System.IDisposable, System.IAsyncDisposable { bool TryBeginExternalEffectGroup(out System.IDisposable group); GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration) => null; } public sealed class GrimoireConnectionAdmissionGate { public sealed class DrainedMaintenanceProof { } }", StringComparison.Ordinal);

        bool fromDi = shape is "di-direct" or "di-stable-out" or "di-ambiguous";

        bool localOut = shape is "local-stable-out" or "di-stable-out" or "local-reassigned-out" or "local-opaque-out";

        string gateValue = fromDi ? "injectedGate" : shape == "converted-gate" ? "(DiscoveryGate)new GateWrapper()" : "new DiscoveryGate()";

        source = source.ReplaceSdkFixtureText(gateContract + " gate = null!;", gateContract + " gate = " + gateValue + ";", StringComparison.Ordinal);

        string outValue = "lease = new DiscoveryLease();";

        if (localOut)
        {
            string mutation = shape switch
            {
                "local-reassigned-out" => "candidate = new DiscoveryLease();",
                "local-opaque-out" => "candidate = UnknownLease;",
                _ => "",
            };

            outValue = "DiscoveryLease candidate = new DiscoveryLease(); " + mutation + " lease = ((" + leaseContract + ")candidate)!;";
        }

        string gateBody = "if (kind != RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind.LongRestSweep) { lease = null; return false; } "
            + outValue + " return true;";

        ClassDeclarationSyntax oldLease = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "ForeignLease" && type.Parent is ClassDeclarationSyntax { Identifier.ValueText: "Worker" });

        source = source.ReplaceSdkFixtureText(oldLease.ToFullString(), $$"""
            public sealed class DiscoveryGate : {{gateContract}}
            {
                public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out {{leaseContract}} lease)
                {
                    {{gateBody}}
                }
            }

            public sealed class DiscoveryLease : {{leaseContract}}
            {
                public bool TryBeginExternalEffectGroup(out IDisposable group) { group = new ForeignGroup(); return true; }

                public RetroDownfall.Arcanum.Infrastructure.Data.GrimoireConnectionAdmissionGate.DrainedMaintenanceProof? ProveDrainedMaintenance(long previousGeneration)
                {
                    System.IO.File.Delete("discovery-lease-body");

                    return null;
                }

                public void Dispose() { }

                public ValueTask DisposeAsync() => default;
            }
            """, StringComparison.Ordinal);

        if (shape == "local-opaque-out")
            source = source.ReplaceSdkFixtureText("public sealed class DiscoveryGate : ",
                "private static DiscoveryLease UnknownLease { get; set; } = null!; public sealed class DiscoveryGate : ", StringComparison.Ordinal);

        if (shape == "converted-gate")
            source = source.ReplaceSdkFixtureText("public sealed class DiscoveryGate : ",
                "public sealed class GateWrapper { public static implicit operator DiscoveryGate(GateWrapper value) => new DiscoveryGate(); } public sealed class DiscoveryGate : ", StringComparison.Ordinal);

        if (fromDi)
        {
            source = source.ReplaceSdkFixtureText("public class Worker(IServiceScopeFactory scopeFactory) : IHostedService",
                "public class Worker(IServiceScopeFactory scopeFactory, " + gateContract + " injectedGate) : IHostedService", StringComparison.Ordinal);

            source = source.ReplaceSdkFixtureText("new Worker(provider.GetRequiredService<IServiceScopeFactory>());",
                "new Worker(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<" + gateContract + ">());", StringComparison.Ordinal);

            string registrations = "services.AddSingleton<Worker.DiscoveryGate>(); services.AddSingleton<" + gateContract
                + ">(static sp => sp.GetRequiredService<Worker.DiscoveryGate>()); ";

            if (shape == "di-ambiguous")
            {
                source = source.ReplaceSdkFixtureText("public sealed class DiscoveryGate : ",
                    "public sealed class AlternateDiscoveryGate : " + gateContract
                    + " { public bool TryAcquireWorkLease(RetroDownfall.Arcanum.Infrastructure.Data.GrimoireWorkKind kind, out " + leaseContract
                    + " lease) { " + gateBody + " } } public sealed class DiscoveryGate : ", StringComparison.Ordinal);

                registrations += "services.AddSingleton<Worker.AlternateDiscoveryGate>(); services.AddSingleton<" + gateContract
                    + ">(static sp => sp.GetRequiredService<Worker.AlternateDiscoveryGate>()); ";
            }

            source = source.ReplaceSdkFixtureText("public static void Configure(IServiceCollection services) { ",
                "public static void Configure(IServiceCollection services) { " + registrations, StringComparison.Ordinal);
        }

        source = source.ReplaceSdkFixtureText("await SendSecondAsync(ct).ConfigureAwait(false);",
            "_ = workAdmission.Lease.ProveDrainedMaintenance(0L); await SendSecondAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        SyntaxNode syntax = tree.GetRoot();

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(), type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == name);

        ILocalSymbol Local(MethodDeclarationSyntax body, string name) => Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(
            Assert.Single(body.DescendantNodes().OfType<VariableDeclaratorSyntax>(), variable => variable.Identifier.ValueText == name)));

        MethodDeclarationSyntax run = Method("Worker", "RunOneAsync");

        InvocationExpressionSyntax acquisitionSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "TryAcquireWorkLease" });

        IInvocationOperation acquisition = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(acquisitionSyntax));

        Assert.Equal(gateContract, acquisition.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(compilation.Assembly.Identity, acquisition.TargetMethod.ContainingAssembly.Identity);

        Assert.True(SymbolEqualityComparer.Default.Equals(Local(run, "gate"), Assert.IsAssignableFrom<ILocalReferenceOperation>(acquisition.Instance).Local));

        VariableDeclaratorSyntax gateSyntax = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(), static variable => variable.Identifier.ValueText == "gate");

        INamedTypeSymbol leaseType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("DiscoveryLease")));

        Assert.True(leaseType.IsSealed && !leaseType.IsGenericType);

        IMethodSymbol leaseConstructor = Assert.Single(leaseType.InstanceConstructors);

        Assert.True(leaseConstructor.IsImplicitlyDeclared);

        Assert.Empty(leaseConstructor.Parameters);

        Assert.DoesNotContain(Type("DiscoveryLease").Members, static member => member is ConstructorDeclarationSyntax or BaseFieldDeclarationSyntax or PropertyDeclarationSyntax);

        INamedTypeSymbol gateType = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("DiscoveryGate")));

        Assert.True(gateType.IsSealed && !gateType.IsGenericType);

        if (fromDi)
        {
            IParameterSymbol injected = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
                Assert.Single(Type("Worker").ParameterList!.Parameters, static parameter => parameter.Identifier.ValueText == "injectedGate")));

            Assert.True(SymbolEqualityComparer.Default.Equals(acquisition.TargetMethod.ContainingType, injected.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(injected, model.GetSymbolInfo(gateSyntax.Initializer!.Value).Symbol));

            InvocationExpressionSyntax[] aliases = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
                model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "AddSingleton" } registration
                && registration.TargetMethod.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(registration.TargetMethod.TypeArguments[0], injected.Type)).ToArray();

            Assert.Equal(shape == "di-ambiguous" ? 2 : 1, aliases.Length);

            HashSet<INamedTypeSymbol> registered = new(SymbolEqualityComparer.Default);

            foreach (InvocationExpressionSyntax alias in aliases)
            {
                IInvocationOperation registration = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(alias));

                Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", registration.TargetMethod.ContainingType.ToDisplayString());

                AssertMetadataReferenceOwns(compilation, registration.TargetMethod.ContainingAssembly);

                SimpleLambdaExpressionSyntax factory = Assert.IsType<SimpleLambdaExpressionSyntax>(Assert.Single(alias.ArgumentList.Arguments).Expression);

                Assert.Contains(factory.Modifiers, static token => token.IsKind(SyntaxKind.StaticKeyword));

                InvocationExpressionSyntax resolution = Assert.IsType<InvocationExpressionSyntax>(factory.Body);

                IInvocationOperation resolve = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resolution));

                Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolve.TargetMethod.ContainingType.ToDisplayString());

                Assert.Equal("GetRequiredService", resolve.TargetMethod.Name);

                AssertMetadataReferenceOwns(compilation, resolve.TargetMethod.ContainingAssembly);

                IParameterSymbol provider = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(factory.Parameter));

                Assert.True(SymbolEqualityComparer.Default.Equals(provider,
                    model.GetSymbolInfo(Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression).Symbol));

                INamedTypeSymbol concrete = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(resolve.TargetMethod.TypeArguments));

                Assert.True(concrete.IsSealed && !concrete.IsGenericType);

                Assert.Equal(compilation.Assembly.Identity, concrete.ContainingAssembly.Identity);

                Assert.True(registered.Add(concrete));

                InvocationExpressionSyntax direct = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
                    model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "AddSingleton" } add
                    && add.TargetMethod.TypeArguments.Length == 1 && call.ArgumentList.Arguments.Count == 0
                    && SymbolEqualityComparer.Default.Equals(add.TargetMethod.TypeArguments[0], concrete));

                AssertMetadataReferenceOwns(compilation, Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(direct).Symbol).ContainingAssembly);

                Assert.True(direct.Span.End < alias.SpanStart);

                Assert.Equal(direct.Ancestors().OfType<MethodDeclarationSyntax>().Single().Span, alias.Ancestors().OfType<MethodDeclarationSyntax>().Single().Span);

                Assert.DoesNotContain(alias.Ancestors(), static node => node is IfStatementSyntax or ForStatementSyntax or WhileStatementSyntax);
            }

            Assert.Contains(gateType, registered);

            if (shape == "di-ambiguous")
                Assert.Contains(Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type("AlternateDiscoveryGate"))), registered);

            BaseObjectCreationExpressionSyntax workerSyntax = Assert.Single(syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>(),
                created => model.GetTypeInfo(created).Type?.Name == "Worker");

            IObjectCreationOperation workerAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(workerSyntax));

            IArgumentOperation gateArgument = Assert.Single(workerAllocation.Arguments,
                argument => SymbolEqualityComparer.Default.Equals(argument.Parameter, injected));

            IInvocationOperation gateResolution = Assert.IsAssignableFrom<IInvocationOperation>(gateArgument.Value);

            Assert.Equal("GetRequiredService", gateResolution.TargetMethod.Name);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", gateResolution.TargetMethod.ContainingType.ToDisplayString());

            AssertMetadataReferenceOwns(compilation, gateResolution.TargetMethod.ContainingAssembly);

            Assert.True(SymbolEqualityComparer.Default.Equals(injected.Type, Assert.Single(gateResolution.TargetMethod.TypeArguments)));
        }
        else if (shape == "converted-gate")
        {
            CastExpressionSyntax cast = Assert.IsType<CastExpressionSyntax>(gateSyntax.Initializer!.Value);

            IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

            IMethodSymbol operation = Assert.IsAssignableFrom<IMethodSymbol>(conversion.OperatorMethod);

            Assert.True(conversion.Conversion.IsUserDefined);

            Assert.Equal(MethodKind.Conversion, operation.MethodKind);

            Assert.True(SymbolEqualityComparer.Default.Equals(gateType, operation.ReturnType));

            Assert.Equal(compilation.Assembly.Identity, operation.ContainingAssembly.Identity);

            ConversionOperatorDeclarationSyntax authored = Assert.IsType<ConversionOperatorDeclarationSyntax>(Assert.Single(operation.DeclaringSyntaxReferences).GetSyntax());

            IObjectCreationOperation returned = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(authored.ExpressionBody!.Expression));

            Assert.True(SymbolEqualityComparer.Default.Equals(gateType, returned.Type));
        }
        else
        {
            IObjectCreationOperation gateAllocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(gateSyntax.Initializer!.Value));

            Assert.True(SymbolEqualityComparer.Default.Equals(gateType, gateAllocation.Type));

            Assert.Empty(gateAllocation.Arguments);

            Assert.Null(gateAllocation.Initializer);
        }

        foreach (string owner in shape == "di-ambiguous" ? new[] { "DiscoveryGate", "AlternateDiscoveryGate" } : new[] { "DiscoveryGate" })
        {
            INamedTypeSymbol possibleGate = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetDeclaredSymbol(Type(owner)));

            MethodDeclarationSyntax body = Method(owner, "TryAcquireWorkLease");

            IMethodSymbol implementation = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(body));

            Assert.True(SymbolEqualityComparer.Default.Equals(implementation, possibleGate.FindImplementationForInterfaceMember(acquisition.TargetMethod)));

            IParameterSymbol outParameter = implementation.Parameters[1];

            Assert.Equal(RefKind.Out, outParameter.RefKind);

            ReturnStatementSyntax[] returns = body.DescendantNodes().OfType<ReturnStatementSyntax>().ToArray();

            Assert.Equal(2, returns.Length);

            ReturnStatementSyntax success = Assert.Single(returns, returned => model.GetConstantValue(returned.Expression!).Value is true);

            ReturnStatementSyntax failure = Assert.Single(returns, returned => model.GetConstantValue(returned.Expression!).Value is false);

            Assert.Equal(body.Body!.Span, success.Parent!.Span);

            Assert.IsType<IfStatementSyntax>(failure.Parent!.Parent);

            AssignmentExpressionSyntax[] outWrites = body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment =>
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, outParameter)).ToArray();

            Assert.Equal(2, outWrites.Length);

            AssignmentExpressionSyntax failedWrite = Assert.Single(outWrites, assignment => assignment.Right.IsKind(SyntaxKind.NullLiteralExpression));

            Assert.Equal(failure.Parent.Span, failedWrite.Parent!.Parent!.Span);

            Assert.True(failedWrite.Span.End < failure.SpanStart);

            AssignmentExpressionSyntax supplied = Assert.Single(outWrites, assignment => !assignment.Right.IsKind(SyntaxKind.NullLiteralExpression));

            Assert.True(supplied.Span.End < success.SpanStart);

            Assert.Equal(body.Body.Span, supplied.Parent!.Parent!.Span);

            BaseObjectCreationExpressionSyntax allocationSyntax;

            if (localOut)
            {
                PostfixUnaryExpressionSyntax suppression = Assert.IsType<PostfixUnaryExpressionSyntax>(supplied.Right);

                Assert.True(suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression));

                CastExpressionSyntax cast = Assert.IsType<CastExpressionSyntax>(Assert.IsType<ParenthesizedExpressionSyntax>(suppression.Operand).Expression);

                IConversionOperation conversion = Assert.IsAssignableFrom<IConversionOperation>(model.GetOperation(cast));

                Assert.Null(conversion.OperatorMethod);

                Assert.True(conversion.Conversion.IsReference && !conversion.Conversion.IsUserDefined);

                ILocalSymbol candidate = Local(body, "candidate");

                Assert.True(SymbolEqualityComparer.Default.Equals(candidate, Assert.IsAssignableFrom<ILocalReferenceOperation>(conversion.Operand).Local));

                Assert.True(SymbolEqualityComparer.Default.Equals(candidate.Type, leaseType));

                VariableDeclaratorSyntax declaration = Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(candidate.DeclaringSyntaxReferences).GetSyntax());

                allocationSyntax = Assert.IsAssignableFrom<BaseObjectCreationExpressionSyntax>(declaration.Initializer!.Value);

                AssignmentExpressionSyntax[] writes = body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment =>
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, candidate)).ToArray();

                Assert.Equal(shape is "local-reassigned-out" or "local-opaque-out" ? 1 : 0, writes.Length);

                if (writes.Length != 0)
                {
                    AssignmentExpressionSyntax replacement = Assert.Single(writes);

                    Assert.True(declaration.Span.End < replacement.SpanStart && replacement.Span.End < supplied.SpanStart);

                    Assert.True(replacement.IsKind(SyntaxKind.SimpleAssignmentExpression));

                    if (shape == "local-reassigned-out")
                        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(replacement.Right)).Type));
                    else
                    {
                        IPropertyReferenceOperation opaque = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(replacement.Right));

                        Assert.True(opaque.Property.IsStatic);

                        Assert.NotNull(opaque.Property.SetMethod);

                        Assert.Null(opaque.Instance);

                        Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, opaque.Type));
                    }
                }
            }
            else allocationSyntax = Assert.IsAssignableFrom<BaseObjectCreationExpressionSyntax>(supplied.Right);

            IObjectCreationOperation allocation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(allocationSyntax));

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseType, allocation.Type));

            Assert.True(SymbolEqualityComparer.Default.Equals(leaseConstructor, allocation.Constructor));

            Assert.Empty(allocation.Arguments);

            Assert.Null(allocation.Initializer);
        }

        ILocalSymbol admitted = Assert.IsAssignableFrom<ILocalReferenceOperation>(
            Assert.IsAssignableFrom<IDeclarationExpressionOperation>(acquisition.Arguments[1].Value).Expression).Local;

        VariableDeclaratorSyntax retained = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static variable => variable.Identifier.ValueText == "lease");

        Assert.True(SymbolEqualityComparer.Default.Equals(admitted, model.GetSymbolInfo(retained.Initializer!.Value).Symbol));

        LocalDeclarationStatementSyntax retainedDeclaration = Assert.IsType<LocalDeclarationStatementSyntax>(retained.Parent!.Parent);

        Assert.True(retainedDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
            && retainedDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));

        InvocationExpressionSyntax bindSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "Bind" });

        IInvocationOperation bind = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(bindSyntax));

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", bind.TargetMethod.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(retained),
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(bind.Arguments).Value).Local));

        InvocationExpressionSyntax dispatchSyntax = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "ResumeAsync" });

        Assert.True(bindSyntax.Span.End < dispatchSyntax.SpanStart);

        Assert.Contains(dispatchSyntax.Ancestors(), static node => node is AwaitExpressionSyntax);

        MethodDeclarationSyntax resume = Method("LongRestSweepCoordinator", "ResumeAsync");

        InvocationExpressionSyntax proofSyntax = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(), call =>
            model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "ProveDrainedMaintenance" });

        IInvocationOperation proofCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(proofSyntax));

        IPropertyReferenceOperation leaseRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(proofCall.Instance);

        Assert.Equal("Lease", leaseRead.Property.Name);

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission", leaseRead.Property.ContainingType.ToDisplayString());

        InvocationExpressionSyntax firstDispatch = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "SendFirstAsync" });

        InvocationExpressionSyntax secondDispatch = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => model.GetOperation(call) is IInvocationOperation { TargetMethod.Name: "SendSecondAsync" });

        Assert.True(firstDispatch.Span.End < proofSyntax.SpanStart && proofSyntax.Span.End < secondDispatch.SpanStart);

        IMethodSymbol selected = proofCall.TargetMethod;

        Assert.Equal(leaseContract, selected.ContainingType.ToDisplayString());

        Assert.True(selected.IsVirtual && !selected.IsAbstract && proofCall.IsVirtual);

        IMethodSymbol effective = Assert.IsAssignableFrom<IMethodSymbol>(leaseType.FindImplementationForInterfaceMember(selected));

        Assert.True(SymbolEqualityComparer.Default.Equals(effective, model.GetDeclaredSymbol(Method("DiscoveryLease", "ProveDrainedMaintenance"))));

        MethodDeclarationSyntax leaseBody = Method("DiscoveryLease", "ProveDrainedMaintenance");

        Assert.True(Assert.Single(leaseBody.DescendantNodes().OfType<ReturnStatementSyntax>()).Expression!.IsKind(SyntaxKind.NullLiteralExpression));

        InvocationExpressionSyntax physicalSyntax = Assert.Single(leaseBody.DescendantNodes().OfType<InvocationExpressionSyntax>());

        IInvocationOperation physicalCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(physicalSyntax));

        Assert.Equal("System.IO.File", physicalCall.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal("Delete", physicalCall.TargetMethod.Name);

        AssertMetadataReferenceOwns(compilation, physicalCall.TargetMethod.ContainingAssembly);

        Assert.Equal("discovery-lease-body", Assert.Single(physicalCall.Arguments).Value.ConstantValue.Value);

        output.WriteLine("SCOPED_LEASE_DISCOVERY_PREMISES shape=" + shape + " nativeGateRouteSuccessfulOutSourcesOriginalWrappersAndActualLeaseBody=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [root])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        HostedProducerSite acquired = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        HostedProducerSite Send(string member) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == member && site.Callee == "System.IO.File.Delete");

        HostedProducerSite Group(string member) => Assert.Single(result.Items, site => site.RootType == "Worker"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest.LongRestSweepCoordinator"
            && site.Member == member && site.Callee == leaseContract + ".TryBeginExternalEffectGroup");

        HostedProducerSite first = Send("SendFirstAsync");

        HostedProducerSite second = Send("SendSecondAsync");

        Assert.NotNull(acquired.CapsuleId);

        Assert.Equal(acquired.CapsuleId, first.WorkFrontierCapsuleId);

        Assert.Equal(acquired.CapsuleId, Group("SendFirstAsync").WorkFrontierCapsuleId);

        Assert.NotNull(Group("SendFirstAsync").CapsuleId);

        Assert.Equal(Group("SendFirstAsync").CapsuleId, first.EffectFrontierCapsuleId);

        Assert.EndsWith("/site@" + tree.FilePath + ":" + acquisitionSyntax.SpanStart, acquired.OperationId);

        output.WriteLine("SCOPED_LEASE_DISCOVERY_FIRST_ANCHOR shape=" + shape + " passed");

        string bodyCoordinate = "/site@" + tree.FilePath + ":" + physicalSyntax.SpanStart;

        HostedProducerSite[] physical = result.Items.Where(site => site.RootType == "Worker"
            && site.EnclosingType == "Worker.DiscoveryLease" && site.Member == "ProveDrainedMaintenance"
            && site.Callee == "System.IO.File.Delete" && site.OperationId.EndsWith(bodyCoordinate, StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(physical);

        string callCoordinate = "/call@" + tree.FilePath + ":" + proofSyntax.SpanStart;

        if (preserves)
        {
            Assert.All(physical, site => Assert.Equal(acquired.CapsuleId, site.WorkFrontierCapsuleId));

            Assert.Equal(acquired.CapsuleId, second.WorkFrontierCapsuleId);

            Assert.NotNull(Group("SendSecondAsync").CapsuleId);

            Assert.Equal(Group("SendSecondAsync").CapsuleId, second.EffectFrontierCapsuleId);

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
                && diagnostic.Identity.EndsWith(callCoordinate, StringComparison.Ordinal));

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
                && diagnostic.Identity == tree.FilePath + ":" + proofSyntax.SpanStart);
        }
        else
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_ADMISSION_HANDLE_ESCAPE"
                && diagnostic.Identity.StartsWith(acquired.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(callCoordinate, StringComparison.Ordinal)
                && diagnostic.Detail.StartsWith(proofCallee + ";", StringComparison.Ordinal));

            Assert.All(physical, site =>
            {
                Assert.Null(site.WorkFrontierCapsuleId);

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    && diagnostic.Detail == "System.IO.File.Delete"
                    && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                    && diagnostic.Identity.EndsWith(bodyCoordinate, StringComparison.Ordinal));
            });

            Assert.Null(second.WorkFrontierCapsuleId);

            InvocationExpressionSyntax secondSyntax = Assert.Single(Method("LongRestSweepCoordinator", "SendSecondAsync")
                .DescendantNodes().OfType<InvocationExpressionSyntax>(), static call => call.Expression.ToString() == "System.IO.File.Delete");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(second.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith("/site@" + tree.FilePath + ":" + secondSyntax.SpanStart, StringComparison.Ordinal));
        }

        Assert.All(physical, site =>
        {
            Assert.Null(site.EffectFrontierCapsuleId);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
                && diagnostic.Detail == "System.IO.File.Delete"
                && diagnostic.Identity.StartsWith(site.AuthorityOperationId + "/", StringComparison.Ordinal)
                && diagnostic.Identity.EndsWith(bodyCoordinate, StringComparison.Ordinal));
        });
    }
}
