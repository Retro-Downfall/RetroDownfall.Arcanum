using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A test does not swallow an exception without saying why, and never swallows a timeout.
/// </summary>
/// <remarks>
/// An empty catch-all around an <c>await</c> hides exactly the failures a test exists to see: a
/// <see cref="TimeoutException"/> from a wait, a cancellation that arrived early, a fault in the code
/// under test. The empty catch-all that stays (best-effort cleanup of a temp file, a probe that may
/// legitimately fail) has to carry a comment naming the swallowed path so the next reader can tell
/// intent from an oversight. The check is syntactic, so a catch clause that only appears inside a
/// string literal (the Roslyn analysis fixtures) is not a violation.
/// </remarks>
public sealed class EmptyCatchContractTests
{
    [Fact]
    public void No_test_swallows_an_exception_around_an_await_with_an_empty_catch_all()
    {
        string[] violations = FindEmptyCatchAlls()
            .Where(static site => site.WrapsAwait)
            .Select(static site => site.Describe("wraps an await; narrow it to OperationCanceledException or TimeoutException"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Every_empty_catch_all_in_a_test_names_the_path_it_swallows()
    {
        string[] violations = FindEmptyCatchAlls()
            .Where(static site => !site.HasComment)
            .Select(static site => site.Describe("has no comment saying why the exception is swallowed"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("\n", violations));
    }

    private static IEnumerable<EmptyCatchSite> FindEmptyCatchAlls()
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

            SyntaxNode root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

            foreach (CatchClauseSyntax clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                if (clause.Filter is not null
                    || clause.Block.Statements.Count != 0
                    || !CatchesEverything(clause.Declaration))
                {
                    continue;
                }

                bool wrapsAwait = clause.Parent is TryStatementSyntax tryStatement
                    && tryStatement.Block.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();

                bool hasComment = clause.Block.DescendantTrivia().Any(static trivia =>
                    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));

                int line = clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                yield return new EmptyCatchSite(
                    Path.GetRelativePath(testsRoot, path),
                    line,
                    wrapsAwait,
                    hasComment);
            }
        }
    }

    private static bool CatchesEverything(CatchDeclarationSyntax? declaration) =>
        declaration is null
        || declaration.Type.ToString() is "Exception" or "System.Exception" or "global::System.Exception";

    private sealed record EmptyCatchSite(string File, int Line, bool WrapsAwait, bool HasComment)
    {
        internal string Describe(string reason) => $"{File}:{Line} {reason}.";
    }
}
