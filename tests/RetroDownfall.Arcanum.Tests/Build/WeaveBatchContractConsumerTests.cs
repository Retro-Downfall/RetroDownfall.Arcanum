using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// <c>IWeaveService.EmbedBatchAsync</c> promises one vector per input, in input order, on success, and
/// <c>WeaveService</c> enforces that promise once at the provider boundary. A consumer that re-checks
/// the answer's length against its own input count is a second copy of that rule that can drift from
/// the first, so this inventory pins that the only count comparison in the tree lives in the service.
/// </summary>
/// <remarks>
/// The check follows the answer through the syntax tree rather than matching a spelling: in every body
/// that calls <c>EmbedBatchAsync</c> it tracks the local the awaited answer lands in, and every local
/// assigned from that one or from its <c>Value</c> (whatever the names), and flags any <c>Length</c> or
/// <c>Count</c> of a tracked value that is compared with anything. Width checks stay in the consumers:
/// <c>WeaveService</c> makes no promise about vector dimensions (a provider pool can answer two widths
/// across batches), and each consumer decides what a ragged answer means for its own store. A width
/// comparison such as <c>embedding.Vector.Length != width</c> reads the length of one vector, not of the
/// answer, so it is not a match, and neither is the count of another call's answer in the same body.
/// </remarks>
public sealed class WeaveBatchContractConsumerTests
{
    private const string ServiceRelativePath = "src/RetroDownfall.Arcanum.Api/Intelligence/WeaveService.cs";

    [Fact]
    public void Consumers_do_not_recheck_the_vector_count_the_service_already_guarantees()
    {
        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            if (!source.Names("EmbedBatchAsync(") || source.IsExactOwner(ServiceRelativePath))
            {
                continue;
            }

            foreach (string comparison in AnswerCountComparisons(source.Text))
            {
                offenders.Add($"{source.RelativePath} compares the vector count of an EmbedBatchAsync answer ({comparison})");
            }
        }

        // Named and de-duplicated so the file that regressed is not hidden below an ellipsis.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The detector finds a recheck whatever the answer's local is called and however many locals it passes
    /// through, so the gate above can fail for the spellings a consumer would plausibly write.
    /// </summary>
    [Theory]
    [InlineData("Result<Embedding<float>[]> batch = await weave.EmbedBatchAsync(inputs, ct); IReadOnlyList<Embedding<float>> vectors = batch.Value; if (vectors.Count != inputs.Count) { }")]
    [InlineData("Embedding<float>[] embeddings = (await weave.EmbedBatchAsync(chunks, ct)).Value; if (embeddings.Length != chunks.Length) { }")]
    [InlineData("var answer = await weave.EmbedBatchAsync(chunks, ct).ConfigureAwait(false); if (chunks.Length == answer.Value.Length) { }")]
    [InlineData("Result<Embedding<float>[]> answer; answer = await weave.EmbedBatchAsync(chunks, ct); var vectors = answer.Value!; if (vectors.Length < chunks.Length) { }")]
    [InlineData("if ((await weave.EmbedBatchAsync(chunks, ct)).Value.Length != chunks.Length) { }")]
    public void The_detector_flags_a_count_recheck_under_any_local_name(string body)
    {
        Assert.NotEmpty(AnswerCountComparisons(WrapInMethod(body)));
    }

    [Theory]
    [InlineData("Result<Embedding<float>[]> batch = await weave.EmbedBatchAsync(chunks, ct); int width = batch.Value[0].Vector.Length; if (Array.Exists(batch.Value, embedding => embedding.Vector.Length != width)) { }")]
    [InlineData("Result<string[]> chunked = await weave.ChunkAsync(text, ct); if (chunked.Value.Length == 0) { } Result<Embedding<float>[]> batch = await weave.EmbedBatchAsync(chunked.Value, ct);")]
    [InlineData("Result<Embedding<float>[]> batch = await weave.EmbedBatchAsync(chunks, ct); if (batch.Value.Any(item => item.Vector.Length != expected)) { }")]
    public void The_detector_does_not_flag_a_width_check_or_another_calls_count(string body)
    {
        Assert.Empty(AnswerCountComparisons(WrapInMethod(body)));
    }

    [Fact]
    public void The_service_is_the_one_place_that_checks_the_vector_count_of_a_provider_batch()
    {
        ProductionSource service = Assert.Single(
            ProductionSourceInventory.Sources(),
            static source => source.IsExactOwner(ServiceRelativePath));

        Assert.Contains("batchEmbeddings.Length != batch.Count", service.Text, StringComparison.Ordinal);
    }

    private static string WrapInMethod(string body) =>
        $"class Consumer {{ async Task RunAsync() {{ {body} }} }}";

    /// <summary>
    /// Every comparison in <paramref name="text"/> whose operand is the <c>Length</c> or <c>Count</c> of an
    /// <c>EmbedBatchAsync</c> answer, followed through the locals it is assigned to.
    /// </summary>
    private static List<string> AnswerCountComparisons(string text)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(text).GetRoot();

        List<string> comparisons = [];

        HashSet<SyntaxNode> bodies = [];

        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (IsEmbedBatchCall(call) && EnclosingBody(call) is SyntaxNode body)
            {
                bodies.Add(body);
            }
        }

        foreach (SyntaxNode body in bodies)
        {
            HashSet<string> tracked = TrackedAnswerLocals(body);

            foreach (BinaryExpressionSyntax comparison in body.DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (IsComparison(comparison)
                    && (IsAnswerCount(comparison.Left, tracked) || IsAnswerCount(comparison.Right, tracked)))
                {
                    comparisons.Add(comparison.ToString());
                }
            }
        }

        return comparisons;
    }

    private static SyntaxNode? EnclosingBody(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is BaseMethodDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AccessorDeclarationSyntax
                or AnonymousFunctionExpressionSyntax);

    /// <summary>
    /// The locals in <paramref name="body"/> that hold an answer, its <c>Value</c>, or a copy of either,
    /// grown to a fixed point so a chain of locals of any length is followed.
    /// </summary>
    private static HashSet<string> TrackedAnswerLocals(SyntaxNode body)
    {
        HashSet<string> tracked = new(StringComparer.Ordinal);

        bool grew = true;

        while (grew)
        {
            grew = false;

            foreach (VariableDeclaratorSyntax declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer is not null && IsAnswer(declarator.Initializer.Value, tracked))
                {
                    grew |= tracked.Add(declarator.Identifier.ValueText);
                }
            }

            foreach (AssignmentExpressionSyntax assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && assignment.Left is IdentifierNameSyntax target
                    && IsAnswer(assignment.Right, tracked))
                {
                    grew |= tracked.Add(target.Identifier.ValueText);
                }
            }
        }

        return tracked;
    }

    private static bool IsAnswerCount(ExpressionSyntax operand, HashSet<string> tracked) =>
        Unwrap(operand) is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" or "Count" } count
            && IsAnswer(count.Expression, tracked);

    /// <summary>
    /// Whether <paramref name="expression"/> is an answer once the wrappers that do not change which
    /// collection it is (await, parentheses, null-forgiving, casts, <c>ConfigureAwait</c>, <c>Value</c>,
    /// <c>ToArray</c>/<c>ToList</c>/<c>AsSpan</c>) are peeled off.
    /// </summary>
    private static bool IsAnswer(ExpressionSyntax expression, HashSet<string> tracked)
    {
        ExpressionSyntax current = Unwrap(expression);

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax call when IsEmbedBatchCall(call):
                    return true;

                case IdentifierNameSyntax identifier:
                    return tracked.Contains(identifier.Identifier.ValueText);

                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" or "ToArray" or "ToList" or "AsSpan" } wrapper,
                }:
                    current = Unwrap(wrapper.Expression);

                    break;

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value:
                    current = Unwrap(value.Expression);

                    break;

                default:
                    return false;
            }
        }
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        ExpressionSyntax current = expression;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;

                    break;

                case AwaitExpressionSyntax awaited:
                    current = awaited.Expression;

                    break;

                case PostfixUnaryExpressionSyntax forgiving when forgiving.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    current = forgiving.Operand;

                    break;

                case CastExpressionSyntax cast:
                    current = cast.Expression;

                    break;

                default:
                    return current;
            }
        }
    }

    private static bool IsEmbedBatchCall(InvocationExpressionSyntax call) =>
        call.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "EmbedBatchAsync",
            IdentifierNameSyntax name => name.Identifier.ValueText == "EmbedBatchAsync",
            _ => false,
        };

    private static bool IsComparison(BinaryExpressionSyntax expression) =>
        expression.Kind() is SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression
            or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.GreaterThanOrEqualExpression;
}
