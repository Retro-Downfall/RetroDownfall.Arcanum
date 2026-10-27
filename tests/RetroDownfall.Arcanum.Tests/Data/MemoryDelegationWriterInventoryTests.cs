using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class MemoryDelegationWriterInventoryTests
{
    [Theory]
    [InlineData("Data/SagaMemoryStore.cs", "InsertCoreAsync")]
    [InlineData("Data/SagaMemoryStore.cs", "DeleteAsync")]
    [InlineData("Data/SagaMemoryStore.cs", "DeleteAllAsync")]
    [InlineData("Data/SagaMemoryStore.Curation.cs", "RetireAsync")]
    [InlineData("Data/SagaMemoryStore.Curation.cs", "ReinstateAsync")]
    [InlineData("Data/SagaMemoryStore.Curation.cs", "CorrectAsync")]
    [InlineData("Data/SagaMemoryStore.Curation.cs", "SetPinAsync")]
    [InlineData("Lexicon/LexiconService.cs", "UpsertCoreAsync")]
    [InlineData("Lexicon/LexiconService.cs", "DeleteByNameAsync")]
    [InlineData("Lexicon/LexiconService.Lifecycle.cs", "ChangeLifecycleAsync")]
    [InlineData("Lexicon/LexiconService.Correction.cs", "CorrectAsync")]
    [InlineData("Lexicon/LexiconService.MemoryReview.cs", "ApplyAsync")]
    [InlineData("Lexicon/LexiconService.Erasure.cs", "ApplyAsync")]
    [InlineData("Covenant/CovenantMutationService.cs", "CommitAttemptAsync")]
    [InlineData("Covenant/CovenantMemoryReviewService.cs", "ApplyCoreAsync")]
    [InlineData("Memory/SagaMemoryErasureService.cs", "ApplyAsync")]
    [InlineData("Memory/SagaMemoryReviewService.cs", "ApplyCoreAsync")]
    [InlineData("Covenant/CovenantEntryErasureService.cs", "ApplyAsync")]
    [InlineData("Data/LongRest/SagaLongRestService.cs", "ApplyAsync")]
    [InlineData("Data/LongRest/LongRestSweepStore.cs", "RunAsync")]
    [InlineData("Repositories/CampaignRepository.cs", "DeleteAsync")]
    [InlineData("Repositories/GrimoireRepository.TurnCommit.cs", "CommitTurnAsync")]
    [InlineData("Data/DataRetentionService.cs", "DeleteSessionAsync")]
    [InlineData("Data/DataRetentionService.cs", "ApplyMemoryResetAsync")]
    [InlineData("Data/DataRetentionService.Pruning.cs", "DeleteSagaCandidateAsync")]
    [InlineData("Data/DataRetentionService.Pruning.cs", "DeleteLexiconCandidateAsync")]
    [InlineData("Covenant/CovenantMaintenanceCoordinators.cs", "RunBatchAsync")]
    [InlineData("Data/CovenantSensitiveRetentionPurgeCoordinator.cs", "PurgeOwnedAsync")]
    [InlineData("Data/Covenant/CovenantProtectedArtifactErasureKernel.cs", "ErasePageOwnedAsync")]
    [InlineData("Weave/EmbeddingsResetService.cs", "ResetAsync")]
    public void Relevant_source_write_families_acquire_at_outer_transaction_boundary(string file, string method)
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure", file);

        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        var declaration = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(), node => node.Identifier.Text == method && node.Body is not null);

        string shape = file == "Covenant/CovenantEntryErasureService.cs" ? "Erasure"
            : file == "Covenant/CovenantMutationService.cs" ? "Operator" : "TopLevel";

        string? storage = shape == "Operator" ? File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(),
            "src", "RetroDownfall.Arcanum.Infrastructure", "Covenant", "CovenantMutationService.Storage.cs")) : null;

        Assert.True(GuardAccepts(declaration, shape, storage));
    }

    [Theory]
    [InlineData("Erasure")]
    [InlineData("Operator")]
    public void The_inventory_recognizes_only_the_two_reviewed_nested_ownership_shapes(string shape)
    {
        var method = ParseMethod(shape == "Erasure" ? ValidErasure : ValidOperator,
            shape == "Erasure" ? "ApplyAsync" : "CommitAttemptAsync");

        Assert.True(GuardAccepts(method, shape, ValidStorage));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("sibling")]
    [InlineData("late")]
    [InlineData("await")]
    [InlineData("using")]
    [InlineData("lambda")]
    [InlineData("local")]
    [InlineData("gate-outside")]
    [InlineData("apply-outside")]
    [InlineData("receiver")]
    [InlineData("factory")]
    [InlineData("probe")]
    [InlineData("dispose")]
    [InlineData("factory-not-awaited")]
    [InlineData("duplicate")]
    [InlineData("boundaries-lambda")]
    [InlineData("boundaries-local")]
    [InlineData("alias")]
    [InlineData("gate-unawaited")]
    [InlineData("initializer-lambda")]
    public void The_erasure_inventory_rejects_hostile_ownership_relocations(string change)
    {
        const string barrier = "await using MemoryDelegationMutation? mutation = await memoryDelegation.BeginOptionalAsync(ct);";

        const string acquire = "var closed = await gate.AcquireEntryErasureAsync(ct);";

        const string apply = "await ApplyUnderClosureAsync(ct);";

        string modified = change switch
        {
            "entry" => ValidErasure.Replace(barrier, "", StringComparison.Ordinal).Replace("bool present", barrier + " bool present", StringComparison.Ordinal),
            "sibling" => ValidErasure.Replace(barrier, "if (other) { " + barrier + " }", StringComparison.Ordinal),
            "late" => ValidErasure.Replace(barrier + " " + acquire, acquire + " " + barrier, StringComparison.Ordinal),
            "await" => ValidErasure.Replace("await using MemoryDelegationMutation?", "using MemoryDelegationMutation?", StringComparison.Ordinal),
            "using" => ValidErasure.Replace("await using MemoryDelegationMutation?", "MemoryDelegationMutation?", StringComparison.Ordinal),
            "lambda" => ValidErasure.Replace(barrier, "Func<Task> deferred = async () => { " + barrier + " };", StringComparison.Ordinal),
            "local" => ValidErasure.Replace(barrier, "async Task Deferred() { " + barrier + " }", StringComparison.Ordinal),
            "gate-outside" => ValidErasure.Replace(acquire, "", StringComparison.Ordinal).Replace("bool present", acquire + " bool present", StringComparison.Ordinal),
            "apply-outside" => ValidErasure.Replace(apply, "", StringComparison.Ordinal).Replace("} }", "} " + apply + " }", StringComparison.Ordinal),
            "receiver" => ValidErasure.Replace("memoryDelegation.BeginOptionalAsync", "other.BeginOptionalAsync", StringComparison.Ordinal),
            "factory" => ValidErasure.Replace("BeginOptionalAsync", "UnrelatedAsync", StringComparison.Ordinal),
            "probe" => ValidErasure.Replace("await using (probe.Value)", "", StringComparison.Ordinal),
            "dispose" => ValidErasure.Replace(acquire, "await mutation.DisposeAsync(); " + acquire, StringComparison.Ordinal),
            "factory-not-awaited" => ValidErasure.Replace("= await memoryDelegation", "= memoryDelegation", StringComparison.Ordinal),
            "duplicate" => ValidErasure.Replace(barrier, barrier + " " + barrier.Replace("mutation =", "second =", StringComparison.Ordinal), StringComparison.Ordinal),
            "boundaries-lambda" => ValidErasure.Replace(acquire + " " + apply, "Func<Task> deferred = async () => { " + acquire + " " + apply + " };", StringComparison.Ordinal),
            "boundaries-local" => ValidErasure.Replace(acquire + " " + apply, "async Task Deferred() { " + acquire + " " + apply + " }", StringComparison.Ordinal),
            "alias" => ValidErasure.Replace(acquire, "var alias = mutation; await alias.DisposeAsync(); " + acquire, StringComparison.Ordinal),
            "gate-unawaited" => ValidErasure.Replace("await gate.AcquireEntryErasureAsync", "gate.AcquireEntryErasureAsync", StringComparison.Ordinal),
            "initializer-lambda" => ValidErasure.Replace("await memoryDelegation.BeginOptionalAsync(ct)", "await OtherAsync(async () => await memoryDelegation.BeginOptionalAsync(ct))", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        Assert.NotEqual(ValidErasure, modified);

        Assert.False(GuardAccepts(ParseMethod(modified, "ApplyAsync"), "Erasure", ValidStorage));
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("await")]
    [InlineData("transaction")]
    [InlineData("lambda-forward")]
    [InlineData("transaction-forward")]
    [InlineData("caller-lambda")]
    [InlineData("conditional-helper")]
    public void The_operator_inventory_rejects_unforwarded_or_late_ownership(string change)
    {
        string code = ValidOperator;

        string storage = ValidStorage;

        if (change == "receiver") storage = storage.Replace("memoryDelegation.BeginOptionalAsync", "other.BeginOptionalAsync", StringComparison.Ordinal);

        if (change == "await") storage = storage.Replace("return await", "return", StringComparison.Ordinal);

        if (change == "transaction") code = code.Replace("await using MemoryDelegationMutation? mutation = await BeginOperatorDelegationAsync(ct); await using SqliteTransaction transaction = BeginOperatorTransaction(connection);",
            "await using SqliteTransaction transaction = BeginOperatorTransaction(connection); await using MemoryDelegationMutation? mutation = await BeginOperatorDelegationAsync(ct);", StringComparison.Ordinal);

        if (change == "lambda-forward") storage = storage.Replace("return await memoryDelegation.BeginOptionalAsync(ct);",
            "Func<Task<MemoryDelegationMutation?>> deferred = async () => await memoryDelegation.BeginOptionalAsync(ct); return null;", StringComparison.Ordinal);

        if (change == "transaction-forward") storage = storage.Replace("return connection.BeginTransaction(deferred: false);",
            "Func<SqliteTransaction> deferred = () => connection.BeginTransaction(deferred: false); return null;", StringComparison.Ordinal);

        if (change == "caller-lambda") code = code.Replace("await using SqliteTransaction transaction = BeginOperatorTransaction(connection); await kernel.ApplyBatchAsync(batch, transaction, ct);",
            "Func<Task> deferred = async () => { await using SqliteTransaction transaction = BeginOperatorTransaction(connection); await kernel.ApplyBatchAsync(batch, transaction, ct); };", StringComparison.Ordinal);

        if (change == "conditional-helper") storage = storage.Replace("return await memoryDelegation.BeginOptionalAsync(ct);",
            "if (skip) return null; return await memoryDelegation.BeginOptionalAsync(ct);", StringComparison.Ordinal);

        Assert.False(GuardAccepts(ParseMethod(code, "CommitAttemptAsync"), "Operator", storage));
    }

    private const string ValidErasure = "class C { async Task ApplyAsync() { bool present = await EntryPresentAsync(); await using (probe.Value) { await ProbeReceiptAsync(); } if (!present) { await AnswerAbsentAsync(); } else { await using MemoryDelegationMutation? mutation = await memoryDelegation.BeginOptionalAsync(ct); var closed = await gate.AcquireEntryErasureAsync(ct); await ApplyUnderClosureAsync(ct); } } }";

    private const string ValidOperator = "class C { async Task CommitAttemptAsync() { try { await using MemoryDelegationMutation? mutation = await BeginOperatorDelegationAsync(ct); await using SqliteTransaction transaction = BeginOperatorTransaction(connection); await kernel.ApplyBatchAsync(batch, transaction, ct); } catch { throw; } } }";

    private const string ValidStorage = "class C { async ValueTask<MemoryDelegationMutation?> BeginOperatorDelegationAsync() { try { return await memoryDelegation.BeginOptionalAsync(ct); } catch { throw; } } SqliteTransaction BeginOperatorTransaction() { try { return connection.BeginTransaction(deferred: false); } catch { throw; } } }";

    private static MethodDeclarationSyntax ParseMethod(string code, string name)
    {
        SyntaxNode syntax = CSharpSyntaxTree.ParseText(code).GetRoot();

        Assert.DoesNotContain(syntax.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        return Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.Text == name);
    }

    private static bool GuardAccepts(MethodDeclarationSyntax declaration, string shape, string? storage)
    {
        if (shape == "TopLevel")
        {
            LocalDeclarationStatementSyntax[] original = declaration.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
                .Where(node => node.Declaration.Type.ToString() == "MemoryDelegationMutation?").ToArray();

            return original.Length == 1
                && !original[0].AwaitKeyword.IsKind(SyntaxKind.None)
                && !original[0].UsingKeyword.IsKind(SyntaxKind.None)
                && original[0].ToString().Contains("BeginOptionalAsync", StringComparison.Ordinal)
                && declaration.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsOriginalTransaction)
                    .All(transaction => original[0].SpanStart < transaction.SpanStart);
        }

        LocalDeclarationStatementSyntax[] candidates = declaration.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(node => node.Declaration.Type.ToString() == "MemoryDelegationMutation?")
            .ToArray();

        if (candidates.Length != 1) return false;

        LocalDeclarationStatementSyntax ownership = candidates[0];

        if (ownership.AwaitKeyword.IsKind(SyntaxKind.None) || ownership.UsingKeyword.IsKind(SyntaxKind.None)
            || ownership.Parent is not BlockSyntax block || ownership.Declaration.Variables.Count != 1
            || ownership.Declaration.Variables[0].Initializer?.Value is not AwaitExpressionSyntax initializer)
            return false;

        string variable = ownership.Declaration.Variables[0].Identifier.Text;

        // The await-using owner is deliberately never exposed, aliased or manually disposed.
        if (block.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.Text == variable))
            return false;

        if (shape == "Erasure")
        {
            if (block.Parent is not ElseClauseSyntax alternative || alternative.Parent is not IfStatementSyntax branch
                || branch.Parent != declaration.Body || branch.Condition.ToString() != "!present"
                || !Invocations(branch.Statement, "AnswerAbsentAsync").Any()
                || !DirectFactory(initializer, "memoryDelegation.BeginOptionalAsync"))
                return false;

            bool releasedProbe = declaration.Body!.Statements.OfType<UsingStatementSyntax>().Any(probe =>
                !probe.AwaitKeyword.IsKind(SyntaxKind.None) && probe.Expression?.ToString() == "probe.Value"
                && probe.Span.End < ownership.SpanStart && Invocations(probe, "ProbeReceiptAsync").Any());

            InvocationExpressionSyntax[] boundaries = [.. Invocations(declaration, "AcquireEntryErasureAsync"), .. Invocations(declaration, "ApplyUnderClosureAsync")];

            return releasedProbe && boundaries.Length == 2
                && boundaries.All(call => DirectlyAwaited(call) && DominatedBy(block, ownership, call));
        }

        if (shape == "Operator")
        {
            if (block.Parent is not TryStatementSyntax guarded || guarded.Parent != declaration.Body
                || !DirectFactory(initializer, "BeginOperatorDelegationAsync")
                || storage is null)
                return false;

            SyntaxNode helperSyntax = CSharpSyntaxTree.ParseText(storage).GetRoot();

            MethodDeclarationSyntax? helper = helperSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(method => method.Identifier.Text == "BeginOperatorDelegationAsync");

            MethodDeclarationSyntax? transactionHelper = helperSyntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(method => method.Identifier.Text == "BeginOperatorTransaction");

            ReturnStatementSyntax? forwarding = helper is null ? null : DirectHelperReturn(helper);

            ReturnStatementSyntax? beginning = transactionHelper is null ? null : DirectHelperReturn(transactionHelper);

            bool forwarded = forwarding?.Expression is AwaitExpressionSyntax awaited
                && DirectFactory(awaited, "memoryDelegation.BeginOptionalAsync");

            bool immediate = beginning?.Expression is InvocationExpressionSyntax native
                && native.Expression.ToString() == "connection.BeginTransaction" && native.ArgumentList.Arguments.Any(argument =>
                    argument.NameColon?.Name.Identifier.Text == "deferred" && argument.Expression.IsKind(SyntaxKind.FalseLiteralExpression));

            InvocationExpressionSyntax[] boundaries = [.. Invocations(declaration, "BeginOperatorTransaction")];

            return forwarded && immediate && boundaries.Length == 1
                && DominatedBy(block, ownership, boundaries[0]);
        }

        return false;
    }

    private static IEnumerable<InvocationExpressionSyntax> Invocations(SyntaxNode node, string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == name,
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text == name,
            _ => false,
        });

    private static bool DominatedBy(BlockSyntax block, LocalDeclarationStatementSyntax ownership, InvocationExpressionSyntax boundary) =>
        boundary.Ancestors().Contains(block)
        && !boundary.Ancestors().TakeWhile(parent => parent.Span != block.Span)
            .Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
        && block.Statements.Any(statement => statement.Span.Contains(boundary.Span) && ownership.Span.End <= statement.SpanStart);

    private static ExpressionSyntax UnwrapConfigureAwait(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
            && member.Name.Identifier.Text == "ConfigureAwait" ? member.Expression : expression;

    private static bool DirectFactory(AwaitExpressionSyntax awaited, string factory) =>
        UnwrapConfigureAwait(awaited.Expression) is InvocationExpressionSyntax call && call.Expression.ToString() == factory;

    private static bool DirectlyAwaited(InvocationExpressionSyntax call) =>
        call.Ancestors().OfType<AwaitExpressionSyntax>().FirstOrDefault() is { } awaited
        && UnwrapConfigureAwait(awaited.Expression).Span == call.Span;

    private static ReturnStatementSyntax? DirectHelperReturn(MethodDeclarationSyntax helper)
    {
        if (helper.Body?.Statements.Count != 1 || helper.Body.Statements[0] is not TryStatementSyntax guarded)
            return null;

        ReturnStatementSyntax[] returns = helper.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(statement => !statement.Ancestors().TakeWhile(parent => parent.Span != helper.Span)
                .Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).ToArray();

        return returns.Length == 1 && returns[0].Parent == guarded.Block ? returns[0] : null;
    }

    private static bool IsOriginalTransaction(InvocationExpressionSyntax call) =>
        call.Expression.ToString().Contains("BeginTransaction", StringComparison.Ordinal)
        || call.Expression.ToString().Contains("BeginWriteTransaction", StringComparison.Ordinal)
        || call.Expression.ToString().Contains("BeginMutationTransaction", StringComparison.Ordinal);
}
