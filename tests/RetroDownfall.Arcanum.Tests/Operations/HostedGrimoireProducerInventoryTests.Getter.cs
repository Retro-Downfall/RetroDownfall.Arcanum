using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("read-or-disposed", false)]
    [InlineData("effectful-throw-argument", true)]
    [InlineData("foreign-exception", true)]
    [InlineData("effectful-field-source", true)]
    public void FieldGetterWithFrameworkDisposedExceptionPreservesOwnedCleanup(string shape, bool traversed)
    {
        string value = shape switch
        {
            "effectful-throw-argument" => "_resource ?? throw new ObjectDisposedException(System.IO.File.Exists(\"check-throw\") ? \"y\" : \"n\")",
            "foreign-exception" => "_resource ?? throw new OtherNamespace.ObjectDisposedException(nameof(Reader))",
            "effectful-field-source" => "ReadWithEffect() ?? throw new ObjectDisposedException(nameof(Reader))",
            _ => "_resource ?? throw new ObjectDisposedException(nameof(Reader))",
        };

        string source = R2Source(R2Admission + "new Reader().Value.Dispose(); await Task.CompletedTask;", $$"""
            internal sealed class Reader
            {
                private readonly OwnedResource? _resource = new OwnedResource();

                internal OwnedResource Value => {{value}};

                private OwnedResource? ReadWithEffect()
                {
                    _ = System.IO.File.Exists("getter-body");
                    return _resource;
                }

                internal sealed class OwnedResource : IDisposable
                {
                    public void Dispose() => _ = System.IO.File.Exists("caller-owned-cleanup");
                }
            }

            namespace OtherNamespace
            {
                internal sealed class ObjectDisposedException : System.Exception
                {
                    internal ObjectDisposedException(string name)
                    {
                        _ = System.IO.File.Exists("other-throw");
                    }
                }
            }
            """);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        List<HostedProducerAdmittedStateObservation> states = [];

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [], admittedStateObserver: states.Add);

        Assert.Contains(result.Items, static site =>
            site.EnclosingType.EndsWith(".OwnedResource", StringComparison.Ordinal) && site.Callee == "System.IO.File.Exists");

        if (traversed)
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists"
                && !site.EnclosingType.EndsWith(".OwnedResource", StringComparison.Ordinal));
        }

        Assert.Equal(traversed, states.Any(static state => state.Method.Contains("Reader.get_Value", StringComparison.Ordinal)));
    }
}
