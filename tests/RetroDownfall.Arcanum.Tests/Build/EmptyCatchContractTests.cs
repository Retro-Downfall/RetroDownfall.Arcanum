using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A test does not swallow an exception without saying why, and never hides a failure around asynchronous work
/// behind an empty catch-all.
/// </summary>
/// <remarks>
/// An empty catch-all around an <c>await</c> (or an <c>await foreach</c>, an <c>await using</c>, or a blocking
/// <c>Wait</c>/<c>GetResult</c>/<c>Result</c>) hides exactly the failures a test exists to see: a
/// <see cref="TimeoutException"/> from a wait, a cancellation that arrived early, a fault in the code under
/// test. A typed catch of <see cref="OperationCanceledException"/> is the ordinary end of a cancelled wait and
/// is outside this contract. A typed catch that swallows <see cref="TimeoutException"/> on purpose is allowed,
/// but its comment has to name why the lapse is the expected end of the wait, or who reports it instead (a
/// cleanup wait either reports its own lapse to the diagnostic sink or says which assertion already did); the
/// contract bans the filter-less catch-all and demands that comment. The empty catch-all that stays
/// (best-effort cleanup of a temp file, a probe that may legitimately fail) has to carry a comment naming the
/// swallowed path so the next reader can tell intent from an oversight. The check is syntactic, so a catch
/// clause that only appears inside a string literal (the Roslyn analysis fixtures) is not a violation.
/// </remarks>
public sealed class EmptyCatchContractTests
{
    [Fact]
    public void No_test_swallows_an_exception_around_asynchronous_work_with_an_empty_catch_all()
    {
        string[] violations = TestTreeSites.Value
            .Where(static site => site.IsCatchAll && site.WrapsAsynchronousWork)
            .Select(static site => site.Describe("wraps asynchronous work; narrow it to OperationCanceledException or TimeoutException"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Every_empty_catch_all_in_a_test_names_the_path_it_swallows()
    {
        string[] violations = TestTreeSites.Value
            .Where(static site => site.IsCatchAll && !site.HasComment)
            .Select(static site => site.Describe("has no comment saying why the exception is swallowed"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Every_empty_catch_of_a_timeout_names_why_it_is_the_expected_end_of_the_wait()
    {
        string[] violations = TestTreeSites.Value
            .Where(static site => !site.IsCatchAll && !site.HasComment)
            .Select(static site => site.Describe(
                "swallows a timeout without a comment naming why it is the expected end of the wait (or who reports it)"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Theory]
    [InlineData("catch (TimeoutException) { }", true)]
    [InlineData("catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }", true)]
    [InlineData("catch (OperationCanceledException) { }", false)]
    [InlineData("catch (IOException) { }", false)]
    [InlineData("catch (Exception ex) when (ex is IOException) { }", false)]
    [InlineData("catch (TimeoutException) { Report(); }", false)]
    public void Only_an_empty_catch_naming_a_timeout_is_a_site_of_that_kind(
        string catchClause,
        bool isSite)
    {
        string source = $$"""
            class Sample
            {
                async Task Run()
                {
                    try { await Task.Delay(1); }
                    {{catchClause}}
                }
            }
            """;

        EmptyCatchSite[] sites = [.. FindEmptyCatches(source, "Sample.cs").Where(static site => !site.IsCatchAll)];

        Assert.Equal(isSite, sites.Length == 1);

        Assert.All(sites, static site => Assert.False(site.HasComment));
    }

    [Fact]
    public void An_empty_catch_of_a_timeout_is_commented_when_the_comment_sits_inside_its_block()
    {
        const string Source = """
            class Sample
            {
                async Task Run()
                {
                    try { await Task.Delay(1); }
                    catch (TimeoutException)
                    {
                        // The caller's assertion already reported the lapse.
                    }
                }
            }
            """;

        EmptyCatchSite site = Assert.Single(FindEmptyCatches(Source, "Sample.cs"));

        Assert.False(site.IsCatchAll);

        Assert.True(site.HasComment);
    }

    [Theory]
    [InlineData("await Task.Delay(1);")]
    [InlineData("await foreach (int item in Items()) { }")]
    [InlineData("await using (new Resource()) { }")]
    [InlineData("await using Resource resource = new();")]
    [InlineData("Task.Delay(1).Wait();")]
    [InlineData("Task.WaitAll(Task.Delay(1));")]
    [InlineData("Task.Delay(1).GetAwaiter().GetResult();")]
    [InlineData("int value = Task.FromResult(1).Result;")]
    public void An_empty_catch_all_around_any_form_of_asynchronous_work_is_a_violation(string statement)
    {
        string source = $$"""
            class Sample
            {
                async Task Run()
                {
                    try { {{statement}} }
                    catch { }
                }
            }
            """;

        EmptyCatchSite site = Assert.Single(FindEmptyCatchAlls(source, "Sample.cs"));

        Assert.True(site.WrapsAsynchronousWork, $"'{statement}' was not recognised as asynchronous work.");

        Assert.False(site.HasComment);
    }

    [Theory]
    [InlineData("File.Delete(path);", "catch { }")]
    [InlineData("await Task.Delay(1);", "catch (TimeoutException) { }")]
    [InlineData("await Task.Delay(1);", "catch (Exception ex) when (ex is TimeoutException) { }")]
    [InlineData("await Task.Delay(1);", "catch { Report(); }")]
    public void Only_a_filterless_empty_catch_all_around_asynchronous_work_is_reported_as_wrapping_it(
        string statement,
        string catchClause)
    {
        string source = $$"""
            class Sample
            {
                async Task Run()
                {
                    try { {{statement}} }
                    {{catchClause}}
                }
            }
            """;

        EmptyCatchSite[] sites = [.. FindEmptyCatchAlls(source, "Sample.cs")];

        Assert.DoesNotContain(sites, static site => site.WrapsAsynchronousWork);
    }

    [Fact]
    public void A_comment_inside_the_catch_block_is_recognised_and_a_catch_inside_a_string_is_not_a_site()
    {
        const string Source = """
            class Sample
            {
                const string Fixture = "try { } catch { }";

                void Run()
                {
                    try { File.Delete("x"); }
                    catch
                    {
                        // The file may already be gone.
                    }
                }
            }
            """;

        EmptyCatchSite site = Assert.Single(FindEmptyCatchAlls(Source, "Sample.cs"));

        Assert.True(site.HasComment);

        Assert.False(site.WrapsAsynchronousWork);
    }

    /// <summary>The scan of the whole test tree, parsed once and shared by both contract facts.</summary>
    private static readonly Lazy<IReadOnlyList<EmptyCatchSite>> TestTreeSites = new(
        static () => FindEmptyCatchesInTestTree().ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static IEnumerable<EmptyCatchSite> FindEmptyCatchesInTestTree()
    {
        string testsRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "tests");

        string separator = Path.DirectorySeparatorChar.ToString();

        foreach (string path in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (EmptyCatchSite site in FindEmptyCatches(
                File.ReadAllText(path),
                Path.GetRelativePath(testsRoot, path)))
            {
                yield return site;
            }
        }
    }

    private static IEnumerable<EmptyCatchSite> FindEmptyCatchAlls(string source, string file) =>
        FindEmptyCatches(source, file).Where(static site => site.IsCatchAll);

    /// <summary>
    /// Every catch clause with an empty block that either swallows everything (a filter-less
    /// <c>catch</c> or <c>catch (Exception)</c>, the contract's catch-all) or names a
    /// <see cref="TimeoutException"/>, which a typed catch may swallow on purpose as the expected end of a
    /// wait. A typed catch of a cancellation alone is that wait's ordinary end and is outside the contract.
    /// </summary>
    private static IEnumerable<EmptyCatchSite> FindEmptyCatches(string source, string file)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (CatchClauseSyntax clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (clause.Block.Statements.Count != 0)
            {
                continue;
            }

            bool isCatchAll = clause.Filter is null && CatchesEverything(clause.Declaration);

            if (!isCatchAll && !NamesTimeout(clause))
            {
                continue;
            }

            bool wrapsAsynchronousWork = isCatchAll
                && clause.Parent is TryStatementSyntax tryStatement
                && WrapsAsynchronousWork(tryStatement.Block);

            bool hasComment = clause.Block.DescendantTrivia().Any(static trivia =>
                trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));

            int line = clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            yield return new EmptyCatchSite(file, line, isCatchAll, wrapsAsynchronousWork, hasComment);
        }
    }

    /// <summary>
    /// Whether the clause's exception type or filter names a <see cref="TimeoutException"/>, the outcome a
    /// wait reports that a typed catch is allowed to swallow only when its comment says why.
    /// </summary>
    private static bool NamesTimeout(CatchClauseSyntax clause) =>
        $"{clause.Declaration?.Type} {clause.Filter?.FilterExpression}"
            .Contains("TimeoutException", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="block"/> awaits (an <c>await</c> expression, an <c>await foreach</c>, an
    /// <c>await using</c> statement or declaration) or blocks on a task (<c>Wait</c>, <c>WaitAll</c>,
    /// <c>WaitAny</c>, <c>GetResult</c>, <c>Result</c>). Each of those is where a timeout, a cancellation
    /// or the code under test's own fault surfaces, so each counts the same.
    /// </summary>
    private static bool WrapsAsynchronousWork(BlockSyntax block) =>
        block.DescendantNodes().Any(static node => node switch
        {
            AwaitExpressionSyntax => true,
            CommonForEachStatementSyntax forEach => forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            UsingStatementSyntax usingStatement => usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            LocalDeclarationStatementSyntax declaration => declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } =>
                member.Name.Identifier.ValueText is "Wait" or "WaitAll" or "WaitAny" or "GetResult",
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "Result",
            _ => false,
        });

    private static bool CatchesEverything(CatchDeclarationSyntax? declaration) =>
        declaration is null
        || declaration.Type.ToString() is "Exception" or "System.Exception" or "global::System.Exception";

    private sealed record EmptyCatchSite(string File, int Line, bool IsCatchAll, bool WrapsAsynchronousWork, bool HasComment)
    {
        internal string Describe(string reason) => $"{File}:{Line} {reason}.";
    }
}
