using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantConnectionDrainTests
{
    // This remains a file-level syntax inventory, not a complete opener call graph.
    // A fresh builder consumed only by its DataSource getter observes a string; it
    // cannot compose the connection string that controls pooling. Any ambiguous use
    // retains the old requirement, independently of actual connection construction.
    private static bool RequiresPoolingDeclaration(ProductionSource source)
    {
        if (source.Names("new SqliteConnection(") || source.Names("SqliteConnection connection = new(")) return true;

        if (!source.Names("new SqliteConnectionStringBuilder")) return false;

        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source.Text).GetCompilationUnitRoot();

        if (root.ContainsDiagnostics || root.ContainsDirectives
            || root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(static directive => directive.Alias is not null)
            || root.Externs.Count != 0) return true;

        ObjectCreationExpressionSyntax[] constructions = [.. root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()];

        if (constructions.Any(static creation => TypeName(creation.Type) == "SqliteConnection")) return true;

        foreach (ImplicitObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            TypeSyntax? declared = creation.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax variable } } => variable.Type,
                ReturnStatementSyntax => creation.Ancestors().FirstOrDefault(static node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax) switch
                {
                    MethodDeclarationSyntax method => method.ReturnType,
                    LocalFunctionStatementSyntax local => local.ReturnType,
                    _ => null,
                },
                _ => null,
            };

            if (declared is null || TypeName(declared) is null or "var" or "SqliteConnection" or "SqliteConnectionStringBuilder") return true;
        }

        ObjectCreationExpressionSyntax[] builders = [.. constructions.Where(static creation => TypeName(creation.Type) == "SqliteConnectionStringBuilder")];

        if (builders.Length == 0) return true;

        // Declared builder locals, aliases, casts and other target-typed uses cannot
        // borrow a different immediate getter's exemption in the same file.
        if (root.DescendantNodes().OfType<SimpleNameSyntax>().Any(name => name.Identifier.ValueText == "SqliteConnectionStringBuilder"
                && !builders.Any(builder => builder.Type.Span.Contains(name.Span)))) return true;

        return builders.Any(static creation => creation.Initializer is not null
            || creation.ArgumentList?.Arguments.Count != 1
            || creation.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DataSource" } access
            || access.Expression != creation
            || access.Parent is AssignmentExpressionSyntax { Left: { } left } && left == access
            || access.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 }
            || access.Parent is RefExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax);
    }

    private static string? TypeName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
        _ => null,
    };

    [Fact]
    public void ConnectionStringObservation_ActualCensusValidationDoesNotComposeAConnectionString()
    {
        ProductionSource source = Assert.Single(ProductionSourceInventory.Sources(), static source => source.Is("GrimoireSchemaQuiescentExecution.cs"));

        Assert.False(RequiresPoolingDeclaration(source));
    }

    [Theory]
    [InlineData("string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource;", false)]
    [InlineData("string a = new SqliteConnectionStringBuilder(first).DataSource; string b = new SqliteConnectionStringBuilder(second).DataSource;", false)]
    [InlineData("string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource; var opened = new SqliteConnection(path);", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource; SqliteConnection connection = new(path);", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource; var opened = new Microsoft.Data.Sqlite.SqliteConnection(path);", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource; Microsoft.Data.Sqlite.SqliteConnection other = new(path);", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(first).DataSource; SqliteConnectionStringBuilder escaping = new(second);", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(first).DataSource; var escaping = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(second);", true)]
    [InlineData("var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);", true)]
    [InlineData("return new SqliteConnectionStringBuilder(connection.ConnectionString);", true)]
    [InlineData("Consume(new SqliteConnectionStringBuilder(connection.ConnectionString));", true)]
    [InlineData("var value = (object)new SqliteConnectionStringBuilder(connection.ConnectionString);", true)]
    [InlineData("var value = new SqliteConnectionStringBuilder(connection.ConnectionString) { Pooling = true }.DataSource;", true)]
    [InlineData("var value = new SqliteConnectionStringBuilder(connection.ConnectionString).ToString();", true)]
    [InlineData("var value = new SqliteConnectionStringBuilder(connection.ConnectionString).ConnectionString;", true)]
    [InlineData("new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource = path;", true)]
    [InlineData("new SqliteConnectionStringBuilder(connection.ConnectionString).DataSource += path;", true)]
    [InlineData("string path = new SqliteConnectionStringBuilder(first).DataSource; var escaping = new SqliteConnectionStringBuilder(second);", true)]
    public void ConnectionStringObservation_OnlyImmediateReadOnlyDataSourceUseAvoidsCompositionInventory(string body, bool composes)
    {
        ProductionSource source = new("synthetic/Observation.cs", "class Observation { void Read() { " + body + " } }");

        Assert.Equal(composes, RequiresPoolingDeclaration(source));
    }
}
