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
    [InlineData("Covenant/CovenantMutationService.cs", "CommitAsync")]
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

        var ownership = Assert.Single(declaration.Body!.Statements.OfType<LocalDeclarationStatementSyntax>(),
            node => node.Declaration.Type.ToString() == "MemoryDelegationMutation?");

        Assert.False(ownership.AwaitKeyword.IsKind(SyntaxKind.None));

        Assert.False(ownership.UsingKeyword.IsKind(SyntaxKind.None));

        Assert.Contains("BeginOptionalAsync", ownership.ToString());

        foreach (var transaction in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(node =>
            node.Expression.ToString().Contains("BeginTransaction", StringComparison.Ordinal)
            || node.Expression.ToString().Contains("BeginWriteTransaction", StringComparison.Ordinal)
            || node.Expression.ToString().Contains("BeginMutationTransaction", StringComparison.Ordinal)))
        {
            Assert.True(ownership.SpanStart < transaction.SpanStart);
        }
    }
}
