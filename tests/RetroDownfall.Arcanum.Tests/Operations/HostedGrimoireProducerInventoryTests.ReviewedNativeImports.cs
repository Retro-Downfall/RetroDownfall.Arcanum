using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string NativeImportReader = "RetroDownfall.Arcanum.Cli.Services.FileDescriptorBackupPassphraseReader";

    private static string NativeImportSource(
        string duplicate = "[System.Runtime.InteropServices.DllImport(\"libc\", EntryPoint = \"dup\", SetLastError = true)] private static extern int DuplicateFileDescriptor(int fileDescriptor);",
        string handle = "[System.Runtime.InteropServices.DllImport(\"msvcrt.dll\", EntryPoint = \"_get_osfhandle\", SetLastError = true)] private static extern nint GetOperatingSystemHandle(int fileDescriptor);",
        string type = "FileDescriptorBackupPassphraseReader",
        string call = "DuplicateFileDescriptor(fileDescriptor)") => $$"""
            namespace RetroDownfall.Arcanum.Cli.Services
            {
                internal sealed class {{type}}
                {
                    internal static int Probe(int fileDescriptor)
                    {
                        nint operatingSystem = GetOperatingSystemHandle(fileDescriptor);

                        _ = operatingSystem;

                        return (int){{call}};
                    }

                    {{duplicate}}

                    {{handle}}
                }
            }
            """;

    private static CSharpCompilation NativeImportFixture(string extra, string assemblyName = "RetroDownfall.Arcanum.Cli")
    {
        string probe = extra.Contains("class FileDescriptorBackupPassphraseReader", StringComparison.Ordinal)
            ? "RetroDownfall.Arcanum.Cli.Services.FileDescriptorBackupPassphraseReader"
            : "RetroDownfall.Arcanum.Cli.Services.LookalikeReader";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source("_ = " + probe + ".Probe(3); await Task.CompletedTask;", extra),
            "RetroDownfall.Arcanum.Cli").WithAssemblyName(assemblyName);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static string[] NativeImportUnclassified(CSharpCompilation compilation)
    {
        Assert.Single(compilation.SyntaxTrees);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Empty(result.Items);

        return
        [
            .. result.Diagnostics
                .Where(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED")
                .Select(diagnostic => diagnostic.Detail),
        ];
    }

    [Fact]
    public void TheShippingCliNativeImportsAreReviewedNonProducers()
    {
        CSharpCompilation compilation = NativeImportFixture(NativeImportSource());

        INamedTypeSymbol reader = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(NativeImportReader));

        Assert.Equal("RetroDownfall.Arcanum.Cli", reader.ContainingAssembly.Name);

        IMethodSymbol duplicate = Assert.Single(reader.GetMembers("DuplicateFileDescriptor").OfType<IMethodSymbol>());

        Assert.True(duplicate.IsExtern);

        Assert.Equal("libc", duplicate.GetDllImportData()!.ModuleName);

        Assert.Empty(NativeImportUnclassified(compilation));
    }

    [Fact]
    public void TheReviewedNativeImportsMatchTheShippingCliDeclarations()
    {
        string source = File.ReadAllText(Path.Combine(
            global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(),
            "src", "RetroDownfall.Arcanum.Cli", "Services", "BackupPassphraseReader.cs"));

        Assert.Contains("[DllImport(\"libc\", EntryPoint = \"dup\", SetLastError = true)]", source, StringComparison.Ordinal);

        Assert.Contains("private static extern int DuplicateFileDescriptor(int fileDescriptor);", source, StringComparison.Ordinal);

        Assert.Contains("[DllImport(\"msvcrt.dll\", EntryPoint = \"_get_osfhandle\", SetLastError = true)]", source, StringComparison.Ordinal);

        Assert.Contains("private static extern nint GetOperatingSystemHandle(int fileDescriptor);", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foreign-assembly")]
    [InlineData("foreign-type")]
    [InlineData("other-library")]
    [InlineData("other-entry-point")]
    [InlineData("extra-parameter")]
    [InlineData("other-result")]
    [InlineData("no-last-error")]
    [InlineData("public-accessibility")]
    [InlineData("other-member")]
    public void ALookalikeOfTheShippingCliNativeImportsStaysUnclassified(string shape)
    {
        const string libc = "[System.Runtime.InteropServices.DllImport(\"libc\", EntryPoint = \"dup\", SetLastError = true)] private static extern int DuplicateFileDescriptor(int fileDescriptor);";

        string duplicate = shape switch
        {
            "other-library" => libc.Replace("\"libc\"", "\"libother\"", StringComparison.Ordinal),
            "other-entry-point" => libc.Replace("\"dup\"", "\"dup2\"", StringComparison.Ordinal),
            "extra-parameter" => libc.Replace("(int fileDescriptor)", "(int fileDescriptor, int target)", StringComparison.Ordinal),
            "other-result" => libc.Replace("extern int ", "extern long ", StringComparison.Ordinal),
            "no-last-error" => libc.Replace(", SetLastError = true", string.Empty, StringComparison.Ordinal),
            "public-accessibility" => libc.Replace("private static", "public static", StringComparison.Ordinal),
            "other-member" => libc.Replace("DuplicateFileDescriptor", "DuplicateOther", StringComparison.Ordinal),
            _ => libc,
        };

        string type = shape == "foreign-type" ? "LookalikeReader" : "FileDescriptorBackupPassphraseReader";

        string call = shape switch
        {
            "other-member" => "DuplicateOther(fileDescriptor)",
            "extra-parameter" => "DuplicateFileDescriptor(fileDescriptor, 0)",
            _ => "DuplicateFileDescriptor(fileDescriptor)",
        };

        string source = NativeImportSource(duplicate: duplicate, type: type, call: call);

        CSharpCompilation compilation = NativeImportFixture(source, shape == "foreign-assembly" ? "InventoryForeignFixture" : "RetroDownfall.Arcanum.Cli");

        string[] unclassified = NativeImportUnclassified(compilation);

        string member = shape == "other-member" ? "DuplicateOther" : "DuplicateFileDescriptor";

        Assert.Contains("RetroDownfall.Arcanum.Cli.Services." + type + "." + member, unclassified);

        // A relocated type loses both imports; otherwise the other, unmodified import stays reviewed.
        string handle = "RetroDownfall.Arcanum.Cli.Services." + type + ".GetOperatingSystemHandle";

        if (shape is "foreign-assembly" or "foreign-type")
        {
            Assert.Contains(handle, unclassified);
        }
        else
        {
            Assert.DoesNotContain(handle, unclassified);
        }
    }

    [Fact]
    public void AManagedMethodWithTheNativeImportNameIsTraversedNotReviewed()
    {
        string source = NativeImportSource(
            duplicate: "private static int DuplicateFileDescriptor(int fileDescriptor) { System.IO.File.Delete(\"managed-lookalike\"); return fileDescriptor; }");

        CSharpCompilation compilation = NativeImportFixture(source);

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }
}
