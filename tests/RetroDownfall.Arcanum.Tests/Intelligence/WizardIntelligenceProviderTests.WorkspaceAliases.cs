using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_alias_retrieval_uses_the_indexed_path_for_ranking_and_every_join(bool indexedWithSeparator)
    {
        string indexedPath = indexedWithSeparator ? _workspace.Root + Path.DirectorySeparatorChar : _workspace.Root;

        string requestPath = indexedWithSeparator ? _workspace.Root : _workspace.Root + Path.DirectorySeparatorChar;

        string dbPath = Path.Combine(_workspace.Root, $"rag-alias-{Guid.NewGuid():N}.db");

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(dbPath);

        await SeedWorkspaceFileChunkAsync(db, indexedPath, "src/Target.cs", "alias-target", "public class AliasTarget {}");

        await SeedWorkspaceFileChunkAsync(db, indexedPath + "other", "src/Other.cs", "alias-other", "private class OtherWorkspace {}");

        FakeRagWorkspaceIndexingService indexing = new() { IndexedPath = indexedPath };

        FakeRagDivinationService divination = new()
        {
            Results = [new DivinationResult("alias-target", 0.95f, EmptyDivinationMetadata), new DivinationResult("alias-other", 0.94f, EmptyDivinationMetadata)],
        };

        ArcanumSettings settings = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings,
            weaveService: new FakeRagWeaveService { Available = true },
            divinationService: divination, workspaceIndexingService: indexing, db: db);

        var result = await wizard.ExecutePromptAsync(
            BaseRequest() with { Prompt = "show target", SkipSpellRouting = true, DisableMcpTools = true, WorkingDirectory = requestPath },
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        string prompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        Assert.Contains("public class AliasTarget {}", prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("private class OtherWorkspace {}", prompt, StringComparison.Ordinal);

        Assert.Equal(indexedPath, divination.LastWorkspaceScope);
    }
}
