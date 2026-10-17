using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("captured", false)]
    [InlineData("captured-alias", false)]
    [InlineData("reacquired", true)]
    [InlineData("nameof-only", true)]
    [InlineData("captured-and-reacquired", false)]
    public void Detached_callback_parent_capture_is_not_its_own_admission(string shape, bool parentRetained)
    {
        bool acquires = shape is "reacquired" or "captured-and-reacquired";

        string ownAdmission = R2Admission
            .Replace("gate", "callbackGate", StringComparison.Ordinal)
            .Replace("work", "callbackWork", StringComparison.Ordinal)
            .Replace("lease", "callbackLease", StringComparison.Ordinal)
            .Replace("group", "callbackGroup", StringComparison.Ordinal)
            .Replace("held", "callbackHeld", StringComparison.Ordinal);

        string callback = shape switch
        {
            "captured" or "captured-and-reacquired" => "lease.Dispose();",
            "captured-alias" => "alias.Dispose();",
            "nameof-only" => "_ = nameof(lease);",
            _ => "",
        };

        if (acquires) callback += ownAdmission + " System.IO.File.Delete(\"callback-owned\");";

        string source = R2Source(R2Admission
            + (shape == "captured-alias" ? "var alias = lease;" : "")
            + " using var registration = token.Register(() => { " + callback + " });"
            + " System.IO.File.Exists(\"after-registration\"); await Task.CompletedTask;");

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        InvocationExpressionSyntax registration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "token.Register");

        IInvocationOperation selected = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(registration));

        Assert.Equal("System.Threading.CancellationToken", selected.TargetMethod.ContainingType.ToDisplayString());

        Assert.Equal(typeof(CancellationToken).Assembly.GetName().Name, selected.TargetMethod.ContainingAssembly.Name);

        Assert.DoesNotContain(selected.TargetMethod.Locations, static location => location.IsInSource);

        Assert.Equal("System.Action", Assert.Single(selected.TargetMethod.Parameters).Type.ToDisplayString());

        IDelegateCreationOperation converted = Assert.IsAssignableFrom<IDelegateCreationOperation>(Assert.Single(selected.Arguments).Value);

        IAnonymousFunctionOperation callbackBody = Assert.IsAssignableFrom<IAnonymousFunctionOperation>(converted.Target);

        if (!parentRetained)
        {
            string capturedName = shape == "captured-alias" ? "alias" : "lease";

            VariableDeclaratorSyntax capturedDeclaration = Assert.Single(tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                declaration => declaration.Identifier.ValueText == capturedName);

            ILocalSymbol capturedSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(capturedDeclaration));

            IInvocationOperation disposal = Assert.Single(callbackBody.Body.DescendantsAndSelf().OfType<IInvocationOperation>(),
                static call => call.TargetMethod.Name == "Dispose");

            Assert.True(SymbolEqualityComparer.Default.Equals(capturedSymbol,
                Assert.IsAssignableFrom<ILocalReferenceOperation>(disposal.Instance).Local));

            Assert.False(callbackBody.Syntax.Span.Contains(capturedDeclaration.Span));
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        HostedProducerSite after = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.Equal(parentRetained, after.WorkFrontierCapsuleId is not null);

        InvocationExpressionSyntax afterCall = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.Expression.ToString() == "System.IO.File.Exists");

        string afterCoordinate = "/site@" + tree.FilePath + ":" + afterCall.SpanStart;

        Assert.Equal("Worker", after.RootType);

        Assert.Equal("Worker.StartAsync", after.AuthorityOperationId);

        Assert.Equal(tree.FilePath, after.SourcePath);

        Assert.EndsWith(afterCoordinate, after.OperationId, StringComparison.Ordinal);

        string registrationCoordinate = "/callback@" + tree.FilePath + ":" + registration.SpanStart;

        if (parentRetained)
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
                "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_WORK_FRONTIER_MISSING" or "HOSTED_SITE_EFFECT_FRONTIER_MISSING");

            if (acquires)
            {
                HostedProducerSite owned = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

                Assert.NotNull(owned.WorkFrontierCapsuleId);

                Assert.NotNull(owned.EffectFrontierCapsuleId);
            }
        }
        else
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
                && diagnostic.Identity.EndsWith(registrationCoordinate, StringComparison.Ordinal)
                && diagnostic.Detail.StartsWith("System.Threading.CancellationToken.Register;", StringComparison.Ordinal));

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
                && diagnostic.Identity == after.AuthorityOperationId + afterCoordinate
                && diagnostic.Detail == "System.IO.File.Exists");
        }
    }
}
