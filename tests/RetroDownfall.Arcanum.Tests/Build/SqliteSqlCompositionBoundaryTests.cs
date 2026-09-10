using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class SqliteSqlCompositionBoundaryTests
{
    [Theory]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/LocalErasureWorkItemStore.cs",
        "ceiling",
        "AddWithValue(\"$take\"")]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentRecoveryService.cs",
        "ceiling",
        "AddWithValue(\"$take\"")]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestInspector.cs",
        "indexName",
        "AddWithValue(\"$index\"")]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestInspector.cs",
        "tableName",
        "AddWithValue(\"$table\"")]
    public void Parameterizable_runtime_values_are_bound_instead_of_composed(
        string relativePath,
        string runtimeIdentifier,
        string expectedBinding)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        string[] composedExpressions = root.DescendantNodes()
            .OfType<InterpolationSyntax>()
            .Select(static interpolation => interpolation.Expression.ToString())
            .Where(expression => expression.Contains(runtimeIdentifier, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(composedExpressions);
        Assert.Contains(expectedBinding, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs",
        "busy_timeout",
        "BusyTimeout")]
    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs",
        "rekey",
        "Rekey")]
    public void Runtime_pragmas_that_sqlite_cannot_bind_route_through_the_validated_factory(
        string relativePath,
        string pragma,
        string factoryMethod)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        string[] composedPragmas = root.DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .Where(value => value.ToString().Contains($"PRAGMA {pragma}", StringComparison.OrdinalIgnoreCase))
            .Select(static value => value.ToString())
            .ToArray();

        Assert.Empty(composedPragmas);
        Assert.Contains($"SqlitePragmaStatementFactory.{factoryMethod}(", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", "..", ".."));
}
