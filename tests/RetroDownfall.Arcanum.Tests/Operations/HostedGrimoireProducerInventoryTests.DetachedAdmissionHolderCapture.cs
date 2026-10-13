using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Detached_callback_cannot_hide_parent_admission_in_a_non_disposable_holder()
    {
        const string holder = "internal sealed class CallbackLeaseHolder(RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease captured) { internal RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease Handle => captured; }";

        string source = R2Source(R2Admission
            + " var box = new CallbackLeaseHolder(lease);"
            + " using var registration = token.Register(() => box.Handle.Dispose());"
            + " System.IO.File.Exists(\"after-registration\"); await Task.CompletedTask;", holder);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        SyntaxNode syntax = tree.GetRoot();

        VariableDeclaratorSyntax lease = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static declaration => declaration.Identifier.ValueText == "lease");

        ILocalSymbol leaseSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(lease));

        VariableDeclaratorSyntax box = Assert.Single(syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            static declaration => declaration.Identifier.ValueText == "box");

        ILocalSymbol boxSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(box));

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(box.Initializer!.Value));

        Assert.True(SymbolEqualityComparer.Default.Equals(leaseSymbol,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(Assert.Single(creation.Arguments).Value).Local));

        INamedTypeSymbol holderType = Assert.IsAssignableFrom<INamedTypeSymbol>(boxSymbol.Type);

        Assert.False(holderType.IsGenericType);

        Assert.DoesNotContain(holderType.AllInterfaces, static contract => contract.ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable");

        PropertyDeclarationSyntax property = Assert.Single(syntax.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            static declaration => declaration.Identifier.ValueText == "Handle");

        IPropertySymbol propertySymbol = Assert.IsAssignableFrom<IPropertySymbol>(model.GetDeclaredSymbol(property));

        IParameterReferenceOperation stored = Assert.IsAssignableFrom<IParameterReferenceOperation>(model.GetOperation(property.ExpressionBody!.Expression));

        Assert.True(SymbolEqualityComparer.Default.Equals(Assert.Single(creation.Constructor!.Parameters), stored.Parameter));

        InvocationExpressionSyntax registration = Assert.Single(syntax.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "token.Register");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(registration));

        Assert.Equal("System.Threading.CancellationToken", selected.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(CancellationToken).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        Assert.DoesNotContain(selected.TargetMethod.Locations, static location => location.IsInSource);

        Assert.Equal("System.Action", Assert.Single(selected.TargetMethod.Parameters).Type.ToDisplayString());

        IAnonymousFunctionOperation callback = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(
            Assert.IsAssignableFrom<IDelegateCreationOperation>(Assert.Single(selected.Arguments).Value).Target);

        IInvocationOperation disposal = Assert.Single(callback.Body.DescendantsAndSelf().OfType<IInvocationOperation>());

        Assert.Equal("Dispose", disposal.TargetMethod.Name);

        IPropertyReferenceOperation capturedHandle = Assert.IsAssignableFrom<IPropertyReferenceOperation>(disposal.Instance);

        Assert.True(SymbolEqualityComparer.Default.Equals(propertySymbol, capturedHandle.Property));

        Assert.True(SymbolEqualityComparer.Default.Equals(boxSymbol,
            Assert.IsAssignableFrom<ILocalReferenceOperation>(capturedHandle.Instance).Local));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite after = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Null(after.WorkFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Identity == after.OperationId);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Identity.EndsWith("/callback@" + tree.FilePath + ":" + registration.SpanStart, StringComparison.Ordinal)
            && diagnostic.Detail.StartsWith("System.Threading.CancellationToken.Register;", StringComparison.Ordinal));
    }
}
