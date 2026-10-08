using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// No production source constructs or calls a <see cref="System.Text.RegularExpressions.Regex"/> that can
/// run forever on hostile input.
/// </summary>
/// <remarks>
/// A backtracking pattern with no match timeout can be driven into catastrophic backtracking by text the
/// process did not write (a prompt, a file, a model reply). A hand-constructed Regex therefore has to
/// pass a <see cref="TimeSpan"/> timeout, and so does every static <c>Regex.IsMatch</c>-style call; the
/// alternative is a source-generated <c>[GeneratedRegex(..., matchTimeoutMilliseconds: n)]</c> partial
/// method, which this scan does not look at. The check is syntactic, so it sees explicit
/// <c>new Regex(...)</c>, the target-typed <c>new(...)</c> where the declared type is visibly
/// <c>Regex</c> (a field, a local, a property initializer, an expression-bodied member, a
/// <c>return</c> in a method that returns one), and the static calls. A target-typed <c>new(...)</c>
/// whose type only the compiler knows (an argument, an assignment to a variable declared elsewhere) is
/// beyond a syntactic scan.
/// </remarks>
public sealed class RegexConventionTests
{
    [Fact]
    public void NoNewRegexWithoutTimeoutOrGenerated()
    {
        string[] violations = FindRegexSites()
            .Where(static site => !site.HasTimeout)
            .Select(static site => $"{site.RelativePath}:{site.Line} {site.Describe}")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Regex sites without a match timeout (use [GeneratedRegex(..., matchTimeoutMilliseconds: n)] "
                + "or pass a TimeSpan):\n"
                + string.Join("\n", violations));
    }

    /// <summary>
    /// The scan is only worth anything if it sees every shape it claims to, so each shape is run against a
    /// fixture that has the defect and one that does not.
    /// </summary>
    [Theory]
    [InlineData("void M() { var ok = Regex.IsMatch(a, b); }", 1)]
    [InlineData("void M() { var ok = Regex.IsMatch(a, b, RegexOptions.None); }", 1)]
    [InlineData("void M() { var ok = Regex.IsMatch(a, b, RegexOptions.None, T); }", 0)]
    [InlineData("void M() { var m = Regex.Match(a, b); }", 1)]
    [InlineData("void M() { var m = Regex.Matches(a, b, RegexOptions.None); }", 1)]
    [InlineData("void M() { var m = Regex.Matches(a, b, RegexOptions.None, T); }", 0)]
    [InlineData("void M() { var s = Regex.Split(a, b); }", 1)]
    [InlineData("void M() { var s = Regex.Split(a, b, RegexOptions.None, T); }", 0)]
    [InlineData("void M() { var n = Regex.Count(a, b); }", 1)]
    [InlineData("void M() { var r = Regex.Replace(a, b, c); }", 1)]
    [InlineData("void M() { var r = Regex.Replace(a, b, c, RegexOptions.None); }", 1)]
    [InlineData("void M() { var r = Regex.Replace(a, b, c, RegexOptions.None, T); }", 0)]
    [InlineData("void M() { var e = Regex.Escape(a); var u = Regex.Unescape(a); }", 0)]
    [InlineData("void M() { var ok = System.Text.RegularExpressions.Regex.IsMatch(a, b); }", 1)]
    [InlineData("Regex field = new(\"x\");", 1)]
    [InlineData("Regex field = new(\"x\", RegexOptions.None, T);", 0)]
    [InlineData("public Regex Pattern { get; } = new(\"x\");", 1)]
    [InlineData("public Regex Pattern { get; } = new(\"x\", RegexOptions.None, T);", 0)]
    [InlineData("public Regex? Pattern { get; } = new(\"x\");", 1)]
    [InlineData("public Regex Pattern => new(\"x\");", 1)]
    [InlineData("public Regex Pattern => new(\"x\", RegexOptions.None, T);", 0)]
    [InlineData("Regex Make() => new(\"x\");", 1)]
    [InlineData("Regex Make() { return new(\"x\"); }", 1)]
    [InlineData("Regex Make() { return new(\"x\", RegexOptions.None, T); }", 0)]
    [InlineData("Regex Make() => new Regex(\"x\");", 1)]
    [InlineData("object Make() => new Foo(\"x\");", 0)]
    public void The_scan_sees_every_shape_it_claims_to(string member, int expectedViolations)
    {
        string source = "class Fixture { " + member + " }";

        int violations = FindRegexSitesInSource("fixture.cs", source).Count(static site => !site.HasTimeout);

        Assert.Equal(expectedViolations, violations);
    }

    private static IEnumerable<RegexSite> FindRegexSites()
    {
        string repositoryRoot = TestRepositoryPaths.RepositoryRoot();
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        string separator = Path.DirectorySeparatorChar.ToString();

        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

            foreach (RegexSite site in FindRegexSitesInSource(relativePath, File.ReadAllText(path)))
            {
                yield return site;
            }
        }
    }

    private static IEnumerable<RegexSite> FindRegexSitesInSource(string relativePath, string source)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (IsRegexTypeName(creation.Type))
            {
                yield return Site(relativePath, creation, "new Regex(...)", creation.ArgumentList?.Arguments.Count ?? 0, 3);
            }
        }

        foreach (ImplicitObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            if (IsDeclaredAsRegex(creation))
            {
                yield return Site(relativePath, creation, "Regex x = new(...)", creation.ArgumentList.Arguments.Count, 3);
            }
        }

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax access
                && IsRegexExpression(access.Expression)
                && StaticMatchingMethods.TryGetValue(access.Name.Identifier.Text, out int argumentsWithTimeout))
            {
                yield return Site(
                    relativePath,
                    invocation,
                    $"Regex.{access.Name.Identifier.Text}(...)",
                    invocation.ArgumentList.Arguments.Count,
                    argumentsWithTimeout);
            }
        }
    }

    /// <summary>
    /// The static matching methods and how many arguments the overload that carries a timeout takes:
    /// <c>IsMatch(input, pattern, options, timeout)</c> is four, <c>Replace(input, pattern, replacement,
    /// options, timeout)</c> is five. Fewer means the engine's infinite default.
    /// </summary>
    private static readonly Dictionary<string, int> StaticMatchingMethods = new(StringComparer.Ordinal)
    {
        ["IsMatch"] = 4,
        ["Match"] = 4,
        ["Matches"] = 4,
        ["Split"] = 4,
        ["Count"] = 4,
        ["EnumerateMatches"] = 4,
        ["Replace"] = 5,
    };

    /// <summary>
    /// <c>new Regex(pattern, options, timeout)</c> is the only constructor shape that carries a timeout,
    /// so fewer than three arguments means the engine's infinite default.
    /// </summary>
    private static RegexSite Site(
        string relativePath,
        SyntaxNode node,
        string describe,
        int argumentCount,
        int argumentsWithTimeout) =>
        new(
            relativePath,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            describe + " with no timeout argument",
            HasTimeout: argumentCount >= argumentsWithTimeout);

    private static bool IsRegexTypeName(TypeSyntax type) =>
        (type is NullableTypeSyntax nullable ? nullable.ElementType : type) switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == "Regex",
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "Regex",
            _ => false,
        };

    private static bool IsRegexExpression(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == "Regex",
            MemberAccessExpressionSyntax access => access.Name.Identifier.Text == "Regex",
            _ => false,
        };

    /// <summary>
    /// Whether a target-typed <c>new(...)</c> is visibly a Regex from where it sits: a field or local
    /// declaration, a property initializer, an expression-bodied property or method, or a <c>return</c>
    /// in a method that declares a Regex return type.
    /// </summary>
    private static bool IsDeclaredAsRegex(ImplicitObjectCreationExpressionSyntax creation) =>
        creation.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } =>
                IsRegexTypeName(declaration.Type),
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } =>
                IsRegexTypeName(property.Type),
            ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } =>
                IsRegexTypeName(property.Type),
            ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax method } =>
                IsRegexTypeName(method.ReturnType),
            ReturnStatementSyntax @return =>
                @return.Ancestors().FirstOrDefault(static node =>
                    node is MethodDeclarationSyntax or LocalFunctionStatementSyntax) switch
                {
                    MethodDeclarationSyntax method => IsRegexTypeName(method.ReturnType),
                    LocalFunctionStatementSyntax local => IsRegexTypeName(local.ReturnType),
                    _ => false,
                },
            _ => false,
        };

    private sealed record RegexSite(string RelativePath, int Line, string Describe, bool HasTimeout);
}
