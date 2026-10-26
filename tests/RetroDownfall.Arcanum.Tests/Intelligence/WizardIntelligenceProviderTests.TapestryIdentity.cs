using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("old-model", false, false)]
    [InlineData("replacement-model", false, true)]
    [InlineData("replacement-model", true, true)]
    [InlineData("old-model", true, false)]
    public async Task WizardTapestry_RequiresActualQueryIdentityBeforeSearchOrHydration(string? actualModel, bool legacy, bool expected)
    {
        using GrimoireFixture fixture = new();

        string path = fixture.CopyDatabase();

        await using var db = fixture.CreateContext(path);

        await db.Database.OpenConnectionAsync();

        await using (DbCommand command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('source','/graph','a.cs',0,'body',0,4,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

            _ = await command.ExecuteNonQueryAsync();
        }

        TapestryStore store = new(db, new WeaveIndexAvailability());

        TapestryScope scope = new(TapestryScopeKind.Workspace, "/graph");

        TapestryBuildSnapshot snapshot = await store.ReadBuildSnapshotAsync(scope, CancellationToken.None);

        string identity = TapestryHash.OfEmbeddingModel("local", "replacement-model");

        string settingsHash = TapestryHash.OfSettings(5, 8, 24, 256, 512, 8, identity);

        TapestryGenerationRecipe recipe = new(legacy ? SphericalKMeans.AlgorithmVersion : RaptorAlgorithmManifest.Version,
            settingsHash, null, TapestryHash.SummaryRecipeVersion, 8, identity);

        string id = legacy
            ? await store.BeginGenerationAsync(scope, recipe.AlgorithmVersion, settingsHash, null, recipe.SummaryRecipeVersion, 8, snapshot.Corpus.Fingerprint, DateTimeOffset.UtcNow, CancellationToken.None)
            : await store.BeginGraphGenerationAsync(snapshot, recipe, DateTimeOffset.UtcNow, CancellationToken.None);

        await store.AppendNodesAsync([new(new("node", id, scope.Kind, scope.Id, 0, TapestryNodeKind.Leaf, null,
            TapestryLeafSourceKind.WorkspaceFileChunk, "source", "a.cs", null, TapestryHash.OfContent("body"), null, 1, 0,
            TapestryPartitionReason.None, 8, DateTimeOffset.UtcNow), [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f])], CancellationToken.None);

        await store.PublishGenerationAsync(id, 1, 1, 1, TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow, CancellationToken.None);

        ArcanumSettings settings = DefaultSettings();

        settings.Features.Embeddings = true;

        settings.Features.Tapestry = true;

        settings.Integrations.Embeddings.Provider = "local";

        // Hot-reloaded settings name the new model; an old in-flight query must not inherit it.
        settings.Integrations.Embeddings.Model = "replacement-model";

        Embedding<float> query = new(new float[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f });

        if (actualModel is not null)
        {
            query.AdditionalProperties = new() { [EmbeddingProvenance.MetadataKey] = new EmbeddingProvenance("local", actualModel) };
        }

        FakeRagDivinationService divination = new() { Results = [new DivinationResult("node", 0.9f, EmptyDivinationMetadata)] };

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), settings, divinationService: divination,
            workspaceIndexingService: new FakeRagWorkspaceIndexingService { IndexedPath = "/graph" }, tapestryStore: store, tapestryAuthority: new Task10Authority(),
            campaignResolver: RetroDownfall.Arcanum.Tests.Support.ProductionCampaignResolver.Over(new(), new()));

        TapestryRetrievedNode[]? result = await wizard.RetrieveTapestryContextAsync(BaseRequest() with { WorkingDirectory = "/graph" }, RetroDownfall.Arcanum.Tests.Support.InvocationContexts.StatelessOperator(), query, DefaultProvider(), ModelName, CancellationToken.None);

        Assert.Equal(expected, result is { Length: 1 });

        Assert.Null(divination.LastWorkspaceScope);
    }
}
