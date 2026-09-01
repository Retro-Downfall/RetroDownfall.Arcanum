using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableFact]

    public async Task PlanAsync_ResetWorkspace_RejectsCampaignAndRootMismatch()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(
            DataRetentionOperation.ResetWorkspace,
            Workspace: new DataRetentionWorkspaceBinding(
                graph.TargetCampaignId,
                graph.SiblingRoot));

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Empty(plan.Items);

        Assert.Empty(plan.CandidateIds);

        Assert.Contains(
            plan.Blockers,
            blocker => blocker.ReasonCode == ErrorCodes.Data.InvalidRequest);

    }

    [SkippableFact]

    public async Task PlanAsync_ResetWorkspace_ReportsOnlyExactWorkspaceOwnedRows()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Empty(plan.Blockers);

        Assert.Empty(plan.Conflicts);

        Assert.Equal(
            [
                "campaign:" + graph.TargetCampaignId.ToString("N"),
                "workspace:" + graph.TargetRoot,
            ],
            plan.CandidateIds);

        Assert.Equal(1, plan.Rows);

        Assert.Contains(
            plan.Items,
            item => item.DataClass == RetentionDataClass.WorkspaceChunks
                && item.Rows == 1
                && item.DerivedRecords == 1);

        long expectedWorkspaceEmbeddings =
            await TableExistsInTestAsync("workspace_file_embeddings_vec") ? 2 : 1;

        Assert.Contains(
            plan.Items,
            item => item.DataClass == RetentionDataClass.WorkspaceEmbeddings
                && item.DerivedRecords == expectedWorkspaceEmbeddings);

        long expectedTapestryRecords =
            await TableExistsInTestAsync("tapestry_node_embeddings_vec") ? 4 : 3;

        Assert.Contains(
            plan.Items,
            item => item.DataClass == RetentionDataClass.Tapestry
                && item.DerivedRecords == expectedTapestryRecords);

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetWorkspace_RemovesExactWorkspaceAndPreservesOtherState()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        _ = await SeedSessionAsync(pinned: false);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(0, await CountAsync(
            "WorkspaceContexts",
            "RootPath",
            graph.TargetRoot));

        Assert.Equal(0, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            graph.TargetChunkId));

        Assert.Equal(0, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            graph.TargetChunkId));

        Assert.Equal(0, await CountAsync(
            "tapestry_generations",
            "GenerationId",
            graph.TargetGenerationId));

        Assert.Equal(0, await CountAsync(
            "tapestry_nodes",
            "NodeId",
            graph.TargetNodeId));

        Assert.Equal(0, await CountAsync(
            "tapestry_node_embeddings",
            "NodeId",
            graph.TargetNodeId));

        Assert.Equal(1, await CountAsync(
            "Campaigns",
            "Id",
            Canonical(graph.TargetCampaignId)));

        Assert.Equal(1, await CountAsync(
            "WorkspaceContexts",
            "RootPath",
            graph.NestedRoot));

        Assert.Equal(1, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            graph.NestedChunkId));

        Assert.Equal(1, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            graph.NestedChunkId));

        Assert.Equal(1, await CountAsync(
            "WorkspaceContexts",
            "RootPath",
            graph.SiblingRoot));

        Assert.Equal(1, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            graph.SiblingChunkId));

        Assert.Equal(1, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            graph.SiblingChunkId));

        Assert.Equal(1, await CountAsync(
            "tapestry_generations",
            "GenerationId",
            graph.NestedGenerationId));

        Assert.Equal(1, await CountAsync(
            "tapestry_nodes",
            "NodeId",
            graph.NestedNodeId));

        Assert.Equal(1, await CountAsync(
            "tapestry_node_embeddings",
            "NodeId",
            graph.NestedNodeId));

        Assert.Equal(1, await CountAsync(
            "tapestry_generations",
            "GenerationId",
            graph.SiblingGenerationId));

        Assert.Equal(1, await CountAsync(
            "tapestry_nodes",
            "NodeId",
            graph.SiblingNodeId));

        Assert.Equal(1, await CountAsync(
            "tapestry_node_embeddings",
            "NodeId",
            graph.SiblingNodeId));

        Assert.Equal(1, await CountAsync(
            "tapestry_generations",
            "GenerationId",
            graph.GlobalGenerationId));

        Assert.Equal(1, await CountAllAsync("Sessions"));

        if (await TableExistsInTestAsync("workspace_file_embeddings_vec"))
        {

            Assert.Equal(0, await CountAsync(
                "workspace_file_embeddings_vec",
                "ChunkId",
                graph.TargetChunkId));

            Assert.Equal(1, await CountAsync(
                "workspace_file_embeddings_vec",
                "ChunkId",
                graph.NestedChunkId));

            Assert.Equal(1, await CountAsync(
                "workspace_file_embeddings_vec",
                "ChunkId",
                graph.SiblingChunkId));

        }

        if (await TableExistsInTestAsync("tapestry_node_embeddings_vec"))
        {

            Assert.Equal(0, await CountAsync(
                "tapestry_node_embeddings_vec",
                "NodeId",
                graph.TargetNodeId));

            Assert.Equal(1, await CountAsync(
                "tapestry_node_embeddings_vec",
                "NodeId",
                graph.GlobalNodeId));

            Assert.Equal(1, await CountAsync(
                "tapestry_node_embeddings_vec",
                "NodeId",
                graph.NestedNodeId));

            Assert.Equal(1, await CountAsync(
                "tapestry_node_embeddings_vec",
                "NodeId",
                graph.SiblingNodeId));

        }

    }

    [SkippableFact]
    public async Task ApplyAsync_ResetWorkspace_PreservesTheAcceptedPlanSummaryAfterCheckpointing()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsSuccess, result.Error.Message);

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)).GetAsync(
                result.Value.OperationId,
                CancellationToken.None));

        Assert.Equal(
            $"Applying ResetWorkspace data-retention plan {plan.PlanId}.",
            operation.PublicSummary);

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetWorkspace_RejectsPlanChangedAfterPreview()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        await ExecuteAsync(
            """
            UPDATE "WorkspaceContexts"
            SET "Id" = @replacementId
            WHERE "RootPath" = @root
            """,
            ("@replacementId", Guid.NewGuid().ToString()),
            ("@root", graph.TargetRoot));

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.Equal(1, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            graph.TargetChunkId));

    }

    [SkippableFact]

    public async Task ApplyAsync_ResetWorkspace_RollsBackAllRowsWhenOneDeletionFails()
    {

        RequireSqlCipher();

        WorkspaceResetGraph graph = await SeedWorkspaceResetGraphAsync();

        await ExecuteAsync(
            """
            CREATE TRIGGER fail_workspace_reset_chunk_delete
            BEFORE DELETE ON workspace_file_chunks
            BEGIN
                SELECT RAISE(ABORT, 'workspace reset transaction test');
            END;
            """);

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = WorkspaceResetRequest(graph);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Result<DataRetentionApplyResult> result = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId));

        Assert.True(result.IsFailure);

        Assert.Equal(1, await CountAsync(
            "WorkspaceContexts",
            "RootPath",
            graph.TargetRoot));

        Assert.Equal(1, await CountAsync(
            "workspace_file_chunks",
            "ChunkId",
            graph.TargetChunkId));

        Assert.Equal(1, await CountAsync(
            "workspace_file_embeddings",
            "ChunkId",
            graph.TargetChunkId));

        Assert.Equal(1, await CountAsync(
            "tapestry_generations",
            "GenerationId",
            graph.TargetGenerationId));

        Assert.Equal(1, await CountAsync(
            "tapestry_nodes",
            "NodeId",
            graph.TargetNodeId));

        Assert.Equal(1, await CountAsync(
            "tapestry_node_embeddings",
            "NodeId",
            graph.TargetNodeId));

    }

    private static DataRetentionRequest WorkspaceResetRequest(
        WorkspaceResetGraph graph) =>
        new(
            DataRetentionOperation.ResetWorkspace,
            Workspace: new DataRetentionWorkspaceBinding(
                graph.TargetCampaignId,
                graph.TargetRoot));

    private async Task<WorkspaceResetGraph> SeedWorkspaceResetGraphAsync()
    {

        string workspaceParent = Path.GetFullPath(
            Path.Combine(
                Directory.GetParent(_attachmentsRoot)!.FullName,
                "registered-workspaces"));

        string targetRoot = Path.Combine(workspaceParent, "target");

        string nestedRoot = Path.Combine(targetRoot, "nested");

        string siblingRoot = Path.Combine(workspaceParent, "sibling");

        Guid targetCampaignId = Guid.NewGuid();

        Guid nestedCampaignId = Guid.NewGuid();

        Guid siblingCampaignId = Guid.NewGuid();

        await SeedCampaignAsync(targetCampaignId, targetRoot);

        await SeedCampaignAsync(nestedCampaignId, nestedRoot);

        await SeedCampaignAsync(siblingCampaignId, siblingRoot);

        await SeedWorkspaceContextAsync(targetRoot);

        await SeedWorkspaceContextAsync(nestedRoot);

        await SeedWorkspaceContextAsync(siblingRoot);

        string targetChunkId = "workspace-target-" + Guid.NewGuid().ToString("N");

        string nestedChunkId = "workspace-nested-" + Guid.NewGuid().ToString("N");

        string siblingChunkId = "workspace-sibling-" + Guid.NewGuid().ToString("N");

        await SeedWorkspaceChunkAsync(targetRoot, targetChunkId);

        await SeedWorkspaceChunkAsync(nestedRoot, nestedChunkId);

        await SeedWorkspaceChunkAsync(siblingRoot, siblingChunkId);

        (string targetGenerationId, string targetNodeId) =
            await SeedWorkspaceTapestryAsync("Workspace", targetRoot);

        (string nestedGenerationId, string nestedNodeId) =
            await SeedWorkspaceTapestryAsync("Workspace", nestedRoot);

        (string siblingGenerationId, string siblingNodeId) =
            await SeedWorkspaceTapestryAsync("Workspace", siblingRoot);

        (string globalGenerationId, string globalNodeId) =
            await SeedWorkspaceTapestryAsync(
                "Session",
                Guid.NewGuid().ToString("N"));

        return new WorkspaceResetGraph(
            targetCampaignId,
            targetRoot,
            nestedRoot,
            siblingRoot,
            targetChunkId,
            nestedChunkId,
            siblingChunkId,
            targetGenerationId,
            targetNodeId,
            nestedGenerationId,
            nestedNodeId,
            siblingGenerationId,
            siblingNodeId,
            globalGenerationId,
            globalNodeId);

    }

    private Task SeedCampaignAsync(Guid campaignId, string root) =>
        ExecuteAsync(
            """
            INSERT INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES
                (@id, @name, @name, @path, 0, '{}', @at, @at)
            """,
            // The object-relational writer is the only writer of "Campaigns"."Id", and the value binder
            // uppercases a Guid unconditionally, so this is the spelling every installation holds.
            ("@id", campaignId.ToString("D").ToUpperInvariant()),
            ("@name", campaignId.ToString("N")),
            ("@path", root),
            ("@at", OldTimestamp));

    private Task SeedWorkspaceContextAsync(string root) =>
        ExecuteAsync(
            """
            INSERT INTO "WorkspaceContexts"
                ("Id", "RootPath", "SerializedSnapshot", "CreatedAt")
            VALUES
                (@id, @root, '{}', @at)
            """,
            ("@id", Guid.NewGuid().ToString()),
            ("@root", root),
            ("@at", OldTimestamp));

    private async Task SeedWorkspaceChunkAsync(string root, string chunkId)
    {

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_chunks
                (ChunkId, WorkspacePath, RelativePath, ChunkIndex, Content, CharOffset,
                 CharLength, FileLastWriteTime, IndexedAt)
            VALUES
                (@id, @root, 'Program.cs', 0, 'content', 0, 7, @at, @at)
            """,
            ("@id", chunkId),
            ("@root", root),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO workspace_file_embeddings (ChunkId, Embedding, Dim)
            VALUES (@id, @embedding, 1)
            """,
            ("@id", chunkId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        if (await TableExistsInTestAsync("workspace_file_embeddings_vec"))
        {

            await ExecuteAsync(
                """
                INSERT INTO workspace_file_embeddings_vec (ChunkId, Embedding)
                VALUES (@id, @embedding)
                """,
                ("@id", chunkId),
                ("@embedding", new byte[768 * sizeof(float)]));

        }

    }

    private async Task<(string GenerationId, string NodeId)> SeedWorkspaceTapestryAsync(
        string scopeKind,
        string scopeId)
    {

        string generationId = "workspace-generation-" + Guid.NewGuid().ToString("N");

        string nodeId = "workspace-node-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            """
            INSERT INTO tapestry_generations
                (GenerationId, ScopeKind, ScopeId, Status, AlgorithmVersion,
                 SettingsFingerprint, SummaryModel, SummaryRecipeVersion, EmbeddingDimension,
                 CorpusFingerprint, LayerCount, NodeCount, RootNodeCount, StartedAt, CompletedAt)
            VALUES
                (@generationId, @scopeKind, @scopeId, 'Published', '1', 'SETTINGS',
                 'test-model', '1', 1, 'CORPUS', 1, 1, 1, @at, @at)
            """,
            ("@generationId", generationId),
            ("@scopeKind", scopeKind),
            ("@scopeId", scopeId),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO tapestry_nodes
                (NodeId, GenerationId, ScopeKind, ScopeId, Layer, ParentScopeKey, NodeKind,
                 SourceLabel, Content, ContentHash, EmbeddingDimension, CreatedAt)
            VALUES
                (@nodeId, @generationId, @scopeKind, @scopeId, 0, @scopeId, 'Summary',
                 'Workspace summary', 'summary', 'HASH', 1, @at)
            """,
            ("@nodeId", nodeId),
            ("@generationId", generationId),
            ("@scopeKind", scopeKind),
            ("@scopeId", scopeId),
            ("@at", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO tapestry_node_embeddings (NodeId, Embedding, Dim)
            VALUES (@nodeId, @embedding, 1)
            """,
            ("@nodeId", nodeId),
            ("@embedding", new byte[] { 0, 0, 128, 63 }));

        if (await TableExistsInTestAsync("tapestry_node_embeddings_vec"))
        {

            await ExecuteAsync(
                """
                INSERT INTO tapestry_node_embeddings_vec (NodeId, Embedding)
                VALUES (@nodeId, @embedding)
                """,
                ("@nodeId", nodeId),
                ("@embedding", new byte[768 * sizeof(float)]));

        }

        return (generationId, nodeId);

    }

    private sealed record WorkspaceResetGraph(
        Guid TargetCampaignId,
        string TargetRoot,
        string NestedRoot,
        string SiblingRoot,
        string TargetChunkId,
        string NestedChunkId,
        string SiblingChunkId,
        string TargetGenerationId,
        string TargetNodeId,
        string NestedGenerationId,
        string NestedNodeId,
        string SiblingGenerationId,
        string SiblingNodeId,
        string GlobalGenerationId,
        string GlobalNodeId);

}
