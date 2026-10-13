using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("same-scoped-dependency", true)]
    [InlineData("foreign-holder", false)]
    [InlineData("different-scope", false)]
    [InlineData("reassigned-dependency", false)]
    [InlineData("early-binding-disposal", false)]
    [InlineData("early-lease-disposal", false)]
    [InlineData("unjoined-dependency", false)]
    [InlineData("dependency-ended-lease", false)]
    [InlineData("prior-dependency-ended-lease", false)]
    [InlineData("root-prior-unobserved", true)]
    [InlineData("root-prior-ended-lease", false)]
    [InlineData("root-holder-ended-lease", false)]
    [InlineData("materialized-coordinator-ended-lease", false)]
    public void OrdinaryConstructorDependencyRequiresTheSameRetainedScopedHolder(string shape, bool admitted)
    {
        const string ownerNamespace = "RetroDownfall.Arcanum.Infrastructure.Operations.LongRest";

        const string holderType = "RetroDownfall.Arcanum.Infrastructure.Data.LongRest.LongRestWorkAdmission";

        const string contract = ownerNamespace + ".IOrdinaryMaintenance";

        const string concrete = ownerNamespace + ".OrdinaryMaintenance";

        string source = OrdinaryScopedHolderFixture(shape is "different-scope" or "early-binding-disposal" or "early-lease-disposal"
            ? shape : "same-scoped-lease");

        ClassDeclarationSyntax original = Assert.Single(CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>(), static type => type.Identifier.ValueText == "LongRestSweepCoordinator");

        string sends = string.Join(global::System.Environment.NewLine, original.Members.OfType<MethodDeclarationSyntax>()
            .Where(static method => method.Identifier.ValueText is "SendFirstAsync" or "SendSecondAsync")
            .Select(static method => method.ToFullString()));

        string reassign = shape == "reassigned-dependency"
            ? "maintenance = new OrdinaryMaintenance(new LongRestWorkAdmission());" : "";

        string foreign = shape == "foreign-holder" ? "workAdmission = new LongRestWorkAdmission();" : "";

        string dependencyCleanup = shape == "dependency-ended-lease" ? "workAdmission.Lease.Dispose();" : "";

        string prior = shape == "prior-dependency-ended-lease" ? "maintenance.EndLease();" : "";

        string dispatch = shape == "unjoined-dependency"
            ? "_ = maintenance.ProcessAsync(ct); await Task.CompletedTask;"
            : "await maintenance.ProcessAsync(ct).ConfigureAwait(false);";

        string replacement = $$"""
            internal sealed class LongRestSweepCoordinator(LongRestWorkAdmission workAdmission, IOrdinaryMaintenance maintenance) : ILongRestSweepCoordinator
            {
                public async Task ResumeAsync(Guid operation, object? capability, CancellationToken ct)
                {
                    IGrimoireWorkLease admission = workAdmission.Lease;

                    {{reassign}}

                    {{prior}}

                    {{dispatch}}
                }

                public void EndLease() => maintenance.EndLease();
            }

            internal interface IOrdinaryMaintenance
            {
                Task ProcessAsync(CancellationToken ct);

                void EndLease();
            }

            internal sealed class OrdinaryMaintenance(LongRestWorkAdmission workAdmission) : IOrdinaryMaintenance
            {
                public async Task ProcessAsync(CancellationToken ct)
                {
                    {{foreign}}

                    {{dependencyCleanup}}

                    IGrimoireWorkLease admission = workAdmission.Lease;

                    await SendFirstAsync(ct).ConfigureAwait(false);

                    await SendSecondAsync(ct).ConfigureAwait(false);
                }

                public void EndLease() => workAdmission.Lease.Dispose();

                {{sends}}
            }
            """;

        source = source.ReplaceSdkFixtureText(original.ToFullString(), replacement, StringComparison.Ordinal);

        source = source.ReplaceSdkFixtureText("public static void Configure(IServiceCollection services) { ",
            "public static void Configure(IServiceCollection services) { services.AddScoped<" + contract + ", " + concrete + ">(); ",
            StringComparison.Ordinal);

        if (shape is "root-prior-unobserved" or "root-prior-ended-lease" or "root-holder-ended-lease")
        {
            string rootHistory = shape == "root-holder-ended-lease"
                ? "holder.Lease.Dispose();"
                : contract + " priorMaintenance = scope.ServiceProvider.GetRequiredService<" + contract + ">();"
                    + (shape == "root-prior-ended-lease" ? " priorMaintenance.EndLease();" : "");

            source = source.ReplaceSdkFixtureText("using IDisposable binding = holder.Bind(lease);",
                "using IDisposable binding = holder.Bind(lease); " + rootHistory, StringComparison.Ordinal);
        }

        // The selected coordinator forwarder is present but unused in every positive.
        source = source.ReplaceSdkFixtureText("Task ResumeAsync(Guid operation, object? capability, CancellationToken ct);",
            "Task ResumeAsync(Guid operation, object? capability, CancellationToken ct); void EndLease();",
            StringComparison.Ordinal);

        if (shape == "materialized-coordinator-ended-lease")
            source = source.ReplaceSdkFixtureText("await coordinator.ResumeAsync(operation, null, ct).ConfigureAwait(false);",
                "coordinator.EndLease(); await coordinator.ResumeAsync(operation, null, ct).ConfigureAwait(false);",
                StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SyntaxNode syntax = tree.GetRoot();

        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax Type(string name) => Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == name);

        MethodDeclarationSyntax Method(string owner, string name) => Assert.Single(Type(owner).Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == name);

        ClassDeclarationSyntax coordinator = Type("LongRestSweepCoordinator");

        IParameterSymbol dependency = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
            Assert.Single(coordinator.ParameterList!.Parameters, static parameter => parameter.Identifier.ValueText == "maintenance")));

        Assert.Equal(contract, dependency.Type.ToDisplayString());

        Assert.Equal(MethodKind.Constructor, Assert.IsAssignableFrom<IMethodSymbol>(dependency.ContainingSymbol).MethodKind);

        MethodDeclarationSyntax resume = Method("LongRestSweepCoordinator", "ResumeAsync");

        InvocationExpressionSyntax call = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => model.GetOperation(invocation) is IInvocationOperation { TargetMethod.Name: "ProcessAsync" });

        var invoked = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(call));

        Assert.Equal(contract, invoked.TargetMethod.ContainingType.ToDisplayString());

        Assert.True(SymbolEqualityComparer.Default.Equals(dependency,
            Assert.IsAssignableFrom<IParameterReferenceOperation>(invoked.Instance).Parameter));

        Assert.Equal(shape != "unjoined-dependency", call.Ancestors().OfType<AwaitExpressionSyntax>().Any());

        InvocationExpressionSyntax registration = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "AddScoped" } method
                && method.TypeArguments.FirstOrDefault()?.ToDisplayString() == contract);

        IMethodSymbol add = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(registration).Symbol);

        Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", add.ContainingType.ToDisplayString());

        Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", add.ContainingAssembly.Identity.Name);

        Assert.Empty(add.DeclaringSyntaxReferences);

        Assert.Equal(concrete, add.TypeArguments[1].ToDisplayString());

        IParameterSymbol holder = Assert.IsAssignableFrom<IParameterSymbol>(model.GetDeclaredSymbol(
            Assert.Single(Type("OrdinaryMaintenance").ParameterList!.Parameters)));

        Assert.Equal(holderType, holder.Type.ToDisplayString());

        foreach (string memberName in new[] { "ProcessAsync", "SendFirstAsync", "SendSecondAsync" })
        {
            MemberAccessExpressionSyntax[] reads = Method("OrdinaryMaintenance", memberName).DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>().Where(static access => access.Name.Identifier.ValueText == "Lease").ToArray();

            Assert.Equal(shape == "dependency-ended-lease" && memberName == "ProcessAsync" ? 2 : 1, reads.Length);

            foreach (MemberAccessExpressionSyntax read in reads)
            {
                var property = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(read));

                Assert.Equal(holderType, property.Property.ContainingType.ToDisplayString());

                Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                    Assert.IsAssignableFrom<IParameterReferenceOperation>(property.Instance).Parameter));
            }
        }

        if (shape is "foreign-holder" or "reassigned-dependency")
        {
            MethodDeclarationSyntax owner = shape == "foreign-holder" ? Method("OrdinaryMaintenance", "ProcessAsync") : resume;

            AssignmentExpressionSyntax write = Assert.Single(owner.DescendantNodes().OfType<AssignmentExpressionSyntax>());

            Assert.True(SymbolEqualityComparer.Default.Equals(shape == "foreign-holder" ? holder : dependency,
                model.GetSymbolInfo(write.Left).Symbol));

            Assert.Contains(write.Right.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>(),
                creation => model.GetTypeInfo(creation).Type?.ToDisplayString() == holderType);
        }

        MethodDeclarationSyntax run = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RunOneAsync");

        InvocationExpressionSyntax rootDispatch = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => model.GetOperation(invocation) is IInvocationOperation { TargetMethod.Name: "ResumeAsync" });

        Assert.Contains(rootDispatch.Ancestors(), static node => node is AwaitExpressionSyntax);

        if (shape == "different-scope")
        {
            VariableDeclaratorSyntax resolved = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                static variable => variable.Identifier.ValueText == "coordinator");

            InvocationExpressionSyntax resolution = Assert.IsType<InvocationExpressionSyntax>(resolved.Initializer!.Value);

            MemberAccessExpressionSyntax provider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression);

            ILocalSymbol other = Assert.IsAssignableFrom<ILocalSymbol>(model.GetSymbolInfo(provider.Expression).Symbol);

            Assert.Equal("otherScope", other.Name);
        }

        if (shape is "early-binding-disposal" or "early-lease-disposal")
        {
            string resource = shape == "early-binding-disposal" ? "binding" : "lease";

            InvocationExpressionSyntax cleanup = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    && model.GetSymbolInfo(access.Expression).Symbol is ILocalSymbol local && local.Name == resource
                    && access.Name.Identifier.ValueText is "Dispose" or "DisposeAsync");

            Assert.True(cleanup.Span.End < rootDispatch.SpanStart);
        }

        if (shape is "dependency-ended-lease" or "prior-dependency-ended-lease" or "root-prior-ended-lease")
        {
            MethodDeclarationSyntax owner = Method("OrdinaryMaintenance",
                shape == "dependency-ended-lease" ? "ProcessAsync" : "EndLease");

            InvocationExpressionSyntax disposal = Assert.Single(owner.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "Dispose" });

            var operation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposal));

            Assert.Equal("System.IDisposable", operation.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(operation.TargetMethod.DeclaringSyntaxReferences);

            ExpressionSyntax receiver = Assert.IsType<MemberAccessExpressionSyntax>(disposal.Expression).Expression;

            var read = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(receiver));

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease", read.Property.Type.ToDisplayString());

            Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(read.Instance).Parameter));

            if (shape == "prior-dependency-ended-lease")
            {
                InvocationExpressionSyntax previous = Assert.Single(resume.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "EndLease" });

                var previousCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(previous));

                Assert.True(SymbolEqualityComparer.Default.Equals(dependency,
                    Assert.IsAssignableFrom<IParameterReferenceOperation>(previousCall.Instance).Parameter));

                Assert.True(previous.Span.End < call.SpanStart);
            }
        }

        if (shape is "root-prior-unobserved" or "root-prior-ended-lease" or "root-holder-ended-lease")
        {
            InvocationExpressionSyntax rootBinding = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "Bind" });

            var bindingCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rootBinding));

            ILocalSymbol rootHolder = Assert.IsAssignableFrom<ILocalReferenceOperation>(bindingCall.Instance).Local;

            Assert.Equal(holderType, rootHolder.Type.ToDisplayString());

            Assert.Equal(holderType, bindingCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, bindingCall.TargetMethod.ContainingAssembly.Identity);

            IArgumentOperation rootBindingArgument = Assert.Single(bindingCall.Arguments);

            Assert.Equal(ArgumentKind.Explicit, rootBindingArgument.ArgumentKind);

            Assert.True(rootBindingArgument.InConversion.IsIdentity && rootBindingArgument.OutConversion.IsIdentity
                && rootBindingArgument.Parameter!.RefKind == RefKind.None);

            VariableDeclaratorSyntax rootLeaseDeclaration = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                static variable => variable.Identifier.ValueText == "lease");

            Assert.True(SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(rootLeaseDeclaration),
                Assert.IsAssignableFrom<ILocalReferenceOperation>(rootBindingArgument.Value).Local));

            VariableDeclaratorSyntax coordinatorLocal = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                static variable => variable.Identifier.ValueText == "coordinator");

            InvocationExpressionSyntax coordinatorResolution = Assert.IsType<InvocationExpressionSyntax>(coordinatorLocal.Initializer!.Value);

            MemberAccessExpressionSyntax coordinatorProvider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(coordinatorResolution.Expression).Expression);

            ILocalSymbol actualScope = Assert.IsAssignableFrom<ILocalSymbol>(model.GetSymbolInfo(coordinatorProvider.Expression).Symbol);

            Assert.Equal("scope", actualScope.Name);

            VariableDeclaratorSyntax rootHolderDeclaration = Assert.IsType<VariableDeclaratorSyntax>(
                Assert.Single(rootHolder.DeclaringSyntaxReferences).GetSyntax());

            InvocationExpressionSyntax rootHolderResolution = Assert.IsType<InvocationExpressionSyntax>(rootHolderDeclaration.Initializer!.Value);

            IMethodSymbol rootHolderResolver = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rootHolderResolution)).TargetMethod;

            Assert.Equal("GetRequiredService", rootHolderResolver.Name);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", rootHolderResolver.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", rootHolderResolver.ContainingAssembly.Identity.Name);

            Assert.Empty(rootHolderResolver.DeclaringSyntaxReferences);

            Assert.Equal(holderType, Assert.Single(rootHolderResolver.TypeArguments).ToDisplayString());

            MemberAccessExpressionSyntax rootHolderProvider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(rootHolderResolution.Expression).Expression);

            Assert.Equal("ServiceProvider", rootHolderProvider.Name.Identifier.ValueText);

            Assert.True(SymbolEqualityComparer.Default.Equals(actualScope, model.GetSymbolInfo(rootHolderProvider.Expression).Symbol));

            Assert.True(rootHolderResolution.Span.End < rootBinding.SpanStart);

            if (shape == "root-holder-ended-lease")
            {
                InvocationExpressionSyntax disposal = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                    invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "Dispose" });

                var callOperation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposal));

                Assert.Equal("System.IDisposable", callOperation.TargetMethod.ContainingType.ToDisplayString());

                Assert.Empty(callOperation.TargetMethod.DeclaringSyntaxReferences);

                ExpressionSyntax disposedReceiver = Assert.IsType<MemberAccessExpressionSyntax>(disposal.Expression).Expression;

                var property = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(disposedReceiver));

                Assert.Equal(holderType, property.Property.ContainingType.ToDisplayString());

                Assert.True(SymbolEqualityComparer.Default.Equals(rootHolder,
                    Assert.IsAssignableFrom<ILocalReferenceOperation>(property.Instance).Local));

                Assert.True(rootBinding.Span.End < disposal.SpanStart && disposal.Span.End < coordinatorResolution.SpanStart);
            }
            else
            {
                VariableDeclaratorSyntax priorLocal = Assert.Single(run.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                    static variable => variable.Identifier.ValueText == "priorMaintenance");

                ILocalSymbol priorSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(priorLocal));

                InvocationExpressionSyntax priorResolution = Assert.IsType<InvocationExpressionSyntax>(priorLocal.Initializer!.Value);

                IMethodSymbol resolver = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(priorResolution).Symbol);

                Assert.Equal("GetRequiredService", resolver.Name);

                Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolver.ContainingType.ToDisplayString());

                Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", resolver.ContainingAssembly.Identity.Name);

                Assert.Empty(resolver.DeclaringSyntaxReferences);

                Assert.Equal(contract, Assert.Single(resolver.TypeArguments).ToDisplayString());

                MemberAccessExpressionSyntax provider = Assert.IsType<MemberAccessExpressionSyntax>(
                    Assert.IsType<MemberAccessExpressionSyntax>(priorResolution.Expression).Expression);

                Assert.True(SymbolEqualityComparer.Default.Equals(actualScope, model.GetSymbolInfo(provider.Expression).Symbol));

                Assert.True(rootBinding.Span.End < priorResolution.SpanStart && priorResolution.Span.End < coordinatorResolution.SpanStart);

                IdentifierNameSyntax[] priorUses = run.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, priorSymbol)).ToArray();

                if (shape == "root-prior-unobserved") Assert.Empty(priorUses);
                else
                {
                    InvocationExpressionSyntax priorCleanup = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                        invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "EndLease" });

                    var priorCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(priorCleanup));

                    Assert.Equal(contract, priorCall.TargetMethod.ContainingType.ToDisplayString());

                    Assert.True(SymbolEqualityComparer.Default.Equals(priorSymbol,
                        Assert.IsAssignableFrom<ILocalReferenceOperation>(priorCall.Instance).Local));

                    Assert.Single(priorUses);

                    Assert.True(priorResolution.Span.End < priorCleanup.SpanStart && priorCleanup.Span.End < coordinatorResolution.SpanStart);
                }
            }
        }

        if (shape == "materialized-coordinator-ended-lease")
        {
            const string coordinatorContract = ownerNamespace + ".ILongRestSweepCoordinator";

            const string coordinatorType = ownerNamespace + ".LongRestSweepCoordinator";

            InvocationExpressionSyntax cleanup = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "EndLease" });

            var cleanupCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(cleanup));

            var dispatchCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(rootDispatch));

            ILocalSymbol coordinatorValue = Assert.IsAssignableFrom<ILocalReferenceOperation>(cleanupCall.Instance).Local;

            Assert.True(SymbolEqualityComparer.Default.Equals(coordinatorValue,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(dispatchCall.Instance).Local));

            Assert.Equal(coordinatorContract, coordinatorValue.Type.ToDisplayString());

            Assert.Equal(coordinatorContract, cleanupCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, cleanupCall.TargetMethod.ContainingAssembly.Identity);

            Assert.True(cleanupCall.TargetMethod.ReturnsVoid);

            Assert.Empty(cleanupCall.Arguments);

            VariableDeclaratorSyntax materialized = Assert.IsType<VariableDeclaratorSyntax>(
                Assert.Single(coordinatorValue.DeclaringSyntaxReferences).GetSyntax());

            InvocationExpressionSyntax resolution = Assert.IsType<InvocationExpressionSyntax>(materialized.Initializer!.Value);

            IMethodSymbol resolver = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(resolution)).TargetMethod;

            Assert.Equal("GetRequiredService", resolver.Name);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions", resolver.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", resolver.ContainingAssembly.Identity.Name);

            Assert.Empty(resolver.DeclaringSyntaxReferences);

            Assert.Equal(coordinatorContract, Assert.Single(resolver.TypeArguments).ToDisplayString());

            InvocationExpressionSyntax coordinatorRegistration = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "AddScoped" } method
                    && method.TypeArguments.FirstOrDefault()?.ToDisplayString() == coordinatorContract);

            IMethodSymbol selectedRegistration = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(coordinatorRegistration).Symbol);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions", selectedRegistration.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", selectedRegistration.ContainingAssembly.Identity.Name);

            Assert.Empty(selectedRegistration.DeclaringSyntaxReferences);

            Assert.Equal(coordinatorType, selectedRegistration.TypeArguments[1].ToDisplayString());

            MemberAccessExpressionSyntax provider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(resolution.Expression).Expression);

            var providerRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(provider));

            Assert.Equal("ServiceProvider", providerRead.Property.Name);

            Assert.Equal("Microsoft.Extensions.DependencyInjection.AsyncServiceScope", providerRead.Property.ContainingType.ToDisplayString());

            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions", providerRead.Property.ContainingAssembly.Identity.Name);

            ILocalSymbol scope = Assert.IsAssignableFrom<ILocalReferenceOperation>(providerRead.Instance).Local;

            Assert.Equal("scope", scope.Name);

            InvocationExpressionSyntax bind = Assert.Single(run.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { Name: "Bind" });

            var bindCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(bind));

            ILocalSymbol boundHolder = Assert.IsAssignableFrom<ILocalReferenceOperation>(bindCall.Instance).Local;

            InvocationExpressionSyntax holderResolution = Assert.IsType<InvocationExpressionSyntax>(
                Assert.IsType<VariableDeclaratorSyntax>(Assert.Single(boundHolder.DeclaringSyntaxReferences).GetSyntax()).Initializer!.Value);

            MemberAccessExpressionSyntax holderProvider = Assert.IsType<MemberAccessExpressionSyntax>(
                Assert.IsType<MemberAccessExpressionSyntax>(holderResolution.Expression).Expression);

            Assert.True(SymbolEqualityComparer.Default.Equals(scope, model.GetSymbolInfo(holderProvider.Expression).Symbol));

            Assert.True(bind.Span.End < resolution.SpanStart && resolution.Span.End < cleanup.SpanStart
                && cleanup.Span.End < rootDispatch.SpanStart);

            IMethodSymbol forwarder = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(
                Method("LongRestSweepCoordinator", "EndLease")));

            Assert.Equal(coordinatorType, forwarder.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, forwarder.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(forwarder,
                forwarder.ContainingType.FindImplementationForInterfaceMember(cleanupCall.TargetMethod)));

            InvocationExpressionSyntax forwarded = Assert.Single(Method("LongRestSweepCoordinator", "EndLease")
                .DescendantNodes().OfType<InvocationExpressionSyntax>());

            var forwardedCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(forwarded));

            Assert.Equal(contract, forwardedCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, forwardedCall.TargetMethod.ContainingAssembly.Identity);

            Assert.Equal("EndLease", forwardedCall.TargetMethod.Name);

            Assert.True(SymbolEqualityComparer.Default.Equals(dependency,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(forwardedCall.Instance).Parameter));

            IMethodSymbol maintenanceCleanup = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(
                Method("OrdinaryMaintenance", "EndLease")));

            Assert.True(SymbolEqualityComparer.Default.Equals(maintenanceCleanup,
                maintenanceCleanup.ContainingType.FindImplementationForInterfaceMember(forwardedCall.TargetMethod)));

            InvocationExpressionSyntax disposed = Assert.Single(Method("OrdinaryMaintenance", "EndLease")
                .DescendantNodes().OfType<InvocationExpressionSyntax>());

            var disposedCall = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(disposed));

            Assert.Equal("System.IDisposable", disposedCall.TargetMethod.ContainingType.ToDisplayString());

            Assert.Empty(disposedCall.TargetMethod.DeclaringSyntaxReferences);

            ExpressionSyntax disposedReceiver = Assert.IsType<MemberAccessExpressionSyntax>(disposed.Expression).Expression;

            var leaseRead = Assert.IsAssignableFrom<IPropertyReferenceOperation>(model.GetOperation(disposedReceiver));

            Assert.Equal(holderType, leaseRead.Property.ContainingType.ToDisplayString());

            Assert.Equal(compilation.Assembly.Identity, leaseRead.Property.ContainingAssembly.Identity);

            Assert.True(SymbolEqualityComparer.Default.Equals(holder,
                Assert.IsAssignableFrom<IParameterReferenceOperation>(leaseRead.Instance).Parameter));
        }

        output.WriteLine("ORDINARY_CONSTRUCTOR_DEPENDENCY_PREMISES shape=" + shape + " compilerScopedActivationAndCapturedReceiver=passed");

        HostedProducerDiscovery<string> hosted = HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]);

        Assert.Empty(hosted.Diagnostics);

        Assert.Contains("Worker", hosted.Items);

        HostedProducerOperationEntry root = OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRestSweep };

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], hosted, [new("Worker", [root])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        HostedProducerSite[] physical = result.Items.Where(static site => site.RootType == "Worker"
            && site.EnclosingType == concrete && site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, physical.Length);

        HostedProducerSite acquisition = Assert.Single(result.Items, static site => site.RootType == "Worker"
            && site.EnclosingType == "Worker" && site.Member == "RunOneAsync"
            && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease");

        Assert.Equal(GrimoireWorkKind.LongRestSweep, acquisition.AdmittedWorkKind);

        foreach (HostedProducerSite send in physical)
        {
            if (admitted)
            {
                Assert.Equal(acquisition.CapsuleId, send.WorkFrontierCapsuleId);

                HostedProducerSite group = Assert.Single(result.Items, site => site.RootType == "Worker"
                    && site.EnclosingType == concrete && site.Member == send.Member
                    && site.Callee == "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup");

                Assert.Equal(group.CapsuleId, send.EffectFrontierCapsuleId);
            }
            else
            {
                if (shape is "root-prior-ended-lease" or "root-holder-ended-lease" or "materialized-coordinator-ended-lease")
                    Assert.Null(send.WorkFrontierCapsuleId);

                Assert.True(send.WorkFrontierCapsuleId is null || send.EffectFrontierCapsuleId is null,
                    "A foreign, replaced, expired or detached dependency cannot certify both physical frontiers.");

                string coordinate = send.OperationId[send.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..];

                if (shape == "materialized-coordinator-ended-lease")
                    Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                        && diagnostic.Detail == send.Callee && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));

                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "HOSTED_SITE_WORK_FRONTIER_MISSING"
                    or "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == send.Callee
                    && diagnostic.Identity.EndsWith(coordinate, StringComparison.Ordinal));
            }
        }

        if (admitted)
        {
            Assert.NotEqual(physical[0].EffectFrontierCapsuleId, physical[1].EffectFrontierCapsuleId);

            Assert.Empty(result.Diagnostics);
        }
    }
}
