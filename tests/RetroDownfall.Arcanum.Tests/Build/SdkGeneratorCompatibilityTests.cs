using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class SdkGeneratorCompatibilityTests
{
    [Fact]
    public void Selected_sdk_json_and_configuration_generators_produce_the_real_production_members()
    {
        IReadOnlyList<Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations = HostedGrimoireProducerInventory.ProductionCompilations;

        Compilation core = Assert.Single(compilations, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Core");

        INamedTypeSymbol context = Assert.IsAssignableFrom<INamedTypeSymbol>(core.GetTypeByMetadataName("RetroDownfall.Arcanum.Core.Serialization.GrimoireJsonContext"));

        Assert.NotEmpty(context.GetMembers("Default"));

        Assert.Contains(compilations.SelectMany(static compilation => compilation.SyntaxTrees), static tree =>
            tree.FilePath.Contains("System.Text.Json.SourceGeneration", StringComparison.Ordinal));

        Assert.Contains(compilations.SelectMany(static compilation => compilation.SyntaxTrees), static tree =>
            tree.FilePath.Contains("Microsoft.Extensions.Configuration.Binder.SourceGeneration", StringComparison.Ordinal));

        Assert.Equal(typeof(Compilation).Assembly.GetName().Version,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version);
    }
}
