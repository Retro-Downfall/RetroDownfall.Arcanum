using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct-cast")]
    [InlineData("constructor-cast")]
    [InlineData("constructor-implicit")]
    public void EntryBindingAuthoredConversionOfNullCannotCertifyAnAbsentCleanup(string shape)
    {
        string body = shape switch
        {
            "constructor-cast" => "await using IAsyncDisposable entry = new ConversionNullEntry((AbstractDisposable)(NullSource?)null);",
            "constructor-implicit" => "await using IAsyncDisposable entry = new ConversionImplicitEntry((NullSource?)null);",
            _ => "((IDisposable)(AbstractDisposable)(NullSource?)null)?.Dispose();",
        };

        string source = R2Source(R2Admission + body, """
            internal abstract class AbstractDisposable : IDisposable
            {
                public abstract void Dispose();
            }

            internal readonly struct NullSource
            {
                public static implicit operator AbstractDisposable(NullSource? value) =>
                    NullConversionInput.Unknown();
            }

            internal static class NullConversionInput
            {
                internal static extern AbstractDisposable Unknown();
            }

            internal sealed class ConversionNullEntry(IDisposable? binding) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    binding?.Dispose();

                    return ValueTask.CompletedTask;
                }
            }

            internal sealed class ConversionImplicitEntry(AbstractDisposable? binding) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    ((IDisposable?)binding)?.Dispose();

                    return ValueTask.CompletedTask;
                }
            }
            """);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal));
    }
}
