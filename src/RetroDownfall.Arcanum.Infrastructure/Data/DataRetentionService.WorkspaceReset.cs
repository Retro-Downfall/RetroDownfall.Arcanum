using System.Data.Common;

using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{

    private async Task<DataRetentionPlan> BuildWorkspaceResetPlanAsync(
        DataRetentionRequest request,
        DataRetentionWorkspaceBinding binding,
        CancellationToken cancellationToken)
    {

        if (!TryGetCanonicalWorkspaceRoot(binding, out string workspaceRoot))
        {

            return InvalidWorkspaceResetPlan(
                request,
                binding,
                "The workspace-reset binding must contain a Campaign ID and canonical workspace root.");

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        if (!await WorkspaceBindingExistsAsync(
                connection,
                transaction: null,
                binding.CampaignId,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false))
        {

            return InvalidWorkspaceResetPlan(
                request,
                binding,
                "The Campaign ID and workspace root do not identify the same registered Campaign.");

        }

        WorkspaceResetSnapshot snapshot = await ReadWorkspaceResetSnapshotAsync(
            connection,
            transaction: null,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        DataRetentionConflict[] conflicts = snapshot.CrossScopeGenerationRows == 0
            ? []
            :
            [
                new DataRetentionConflict(
                    ErrorCodes.Data.Conflict,
                    workspaceRoot,
                    "A workspace Tapestry generation contains rows owned by another scope."),
            ];

        return FinalizePlan(
            request,
            WorkspaceResetPlanItems(snapshot),
            [],
            conflicts,
            WorkspaceResetCandidateIds(binding, workspaceRoot),
            requiresConfirmation: true,
            planAuthority: snapshot.ResourceFingerprint);

    }

    private async Task<DataRetentionApplyResult> ApplyWorkspaceResetAsync(
        Guid operationId,
        DataRetentionPlan plan,
        DataRetentionWorkspaceBinding binding,
        CancellationToken cancellationToken)
    {

        if (!TryGetCanonicalWorkspaceRoot(binding, out string workspaceRoot))
        {

            throw new RetentionConflictException(
                "The workspace-reset binding is no longer canonical.");

        }

        DbConnection connection = await OpenConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        long rowsDeleted = 0;

        long derivedDeleted = 0;

        try
        {

            if (!await WorkspaceBindingExistsAsync(
                    connection,
                    transaction,
                    binding.CampaignId,
                    workspaceRoot,
                    cancellationToken).ConfigureAwait(false))
            {

                throw new RetentionConflictException(
                    "The Campaign registration changed after preview; request a new dry-run before retrying.");

            }

            WorkspaceResetSnapshot current = await ReadWorkspaceResetSnapshotAsync(
                connection,
                transaction,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false);

            DataRetentionConflict[] currentConflicts =
                current.CrossScopeGenerationRows == 0
                    ? []
                    :
                    [
                        new DataRetentionConflict(
                            ErrorCodes.Data.Conflict,
                            workspaceRoot,
                            "A workspace Tapestry generation contains rows owned by another scope."),
                    ];

            DataRetentionPlan transactionalPlan = FinalizePlan(
                plan.Request,
                WorkspaceResetPlanItems(current),
                [],
                currentConflicts,
                WorkspaceResetCandidateIds(binding, workspaceRoot),
                requiresConfirmation: true,
                planAuthority: current.ResourceFingerprint);

            if (!string.Equals(
                    transactionalPlan.PlanId,
                    plan.PlanId,
                    StringComparison.Ordinal))
            {

                throw new RetentionConflictException(
                    "Workspace data changed after preview; request a new dry-run before retrying.");

            }

            if (await WorkspaceResetTableExistsAsync(
                    connection,
                    transaction,
                    "tapestry_node_embeddings_vec",
                    cancellationToken).ConfigureAwait(false))
            {

                derivedDeleted += await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM tapestry_node_embeddings_vec
                    WHERE NodeId IN (
                        SELECT NodeId
                        FROM tapestry_nodes
                        WHERE ScopeKind = 'Workspace' AND ScopeId = @root)
                    """,
                    cancellationToken,
                    ("@root", workspaceRoot)).ConfigureAwait(false);

            }

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM tapestry_node_embeddings
                WHERE NodeId IN (
                    SELECT NodeId
                    FROM tapestry_nodes
                    WHERE ScopeKind = 'Workspace' AND ScopeId = @root)
                """,
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM tapestry_nodes WHERE ScopeKind = 'Workspace' AND ScopeId = @root",
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM tapestry_generations WHERE ScopeKind = 'Workspace' AND ScopeId = @root",
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            if (await WorkspaceResetTableExistsAsync(
                    connection,
                    transaction,
                    "workspace_file_embeddings_vec",
                    cancellationToken).ConfigureAwait(false))
            {

                derivedDeleted += await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM workspace_file_embeddings_vec
                    WHERE ChunkId IN (
                        SELECT ChunkId
                        FROM workspace_file_chunks
                        WHERE WorkspacePath = @root)
                    """,
                    cancellationToken,
                    ("@root", workspaceRoot)).ConfigureAwait(false);

            }

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM workspace_file_embeddings
                WHERE ChunkId IN (
                    SELECT ChunkId
                    FROM workspace_file_chunks
                    WHERE WorkspacePath = @root)
                """,
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            derivedDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM workspace_file_chunks WHERE WorkspacePath = @root",
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            rowsDeleted += await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM \"WorkspaceContexts\" WHERE \"RootPath\" = @root",
                cancellationToken,
                ("@root", workspaceRoot)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        WorkspaceResetSnapshot remaining = await ReadWorkspaceResetSnapshotAsync(
            connection,
            transaction: null,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        bool reconciled = remaining.TotalOwnedRows == 0;

        return new DataRetentionApplyResult(
            operationId,
            plan.PlanId,
            rowsDeleted,
            0,
            0,
            derivedDeleted,
            reconciled,
            plan.Blockers,
            plan.Conflicts);

    }

    private DataRetentionPlan InvalidWorkspaceResetPlan(
        DataRetentionRequest request,
        DataRetentionWorkspaceBinding binding,
        string message) =>
        EmptyPlan(
            request,
            new DataRetentionBlocker(
                RetentionDataClass.WorkspaceChunks,
                binding.CampaignId.ToString("D"),
                ErrorCodes.Data.InvalidRequest,
                message));

    private static bool TryGetCanonicalWorkspaceRoot(
        DataRetentionWorkspaceBinding binding,
        out string canonicalRoot)
    {

        canonicalRoot = string.Empty;

        if (binding.CampaignId == Guid.Empty
            || string.IsNullOrWhiteSpace(binding.WorkspaceRoot))
        {

            return false;

        }

        try
        {

            canonicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(binding.WorkspaceRoot));

        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {

            return false;

        }

        return string.Equals(
            binding.WorkspaceRoot,
            canonicalRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    }

    private static async Task<bool> WorkspaceBindingExistsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid campaignId,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM "Campaigns"
                WHERE lower(replace("Id", '-', '')) = @campaignId
                  AND "Path" = @root)
            """;

        Add(command, "@campaignId", campaignId.ToString("N"));

        Add(command, "@root", workspaceRoot);

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;

    }

    private static DataRetentionPlanItem[] WorkspaceResetPlanItems(
        WorkspaceResetSnapshot snapshot)
    {

        List<DataRetentionPlanItem> items = [];

        if (snapshot.WorkspaceContextRows > 0 || snapshot.WorkspaceChunkRows > 0)
        {

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.WorkspaceChunks,
                    snapshot.WorkspaceContextRows,
                    0,
                    0,
                    snapshot.WorkspaceChunkRows));

        }

        long workspaceEmbeddingRows = snapshot.WorkspaceEmbeddingRows
            + snapshot.WorkspaceVectorRows;

        if (workspaceEmbeddingRows > 0)
        {

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.WorkspaceEmbeddings,
                    0,
                    0,
                    0,
                    workspaceEmbeddingRows));

        }

        long tapestryRows = snapshot.TapestryGenerationRows
            + snapshot.TapestryNodeRows
            + snapshot.TapestryEmbeddingRows
            + snapshot.TapestryVectorRows;

        if (tapestryRows > 0)
        {

            items.Add(
                new DataRetentionPlanItem(
                    RetentionDataClass.Tapestry,
                    0,
                    0,
                    0,
                    tapestryRows));

        }

        return [.. items.OrderBy(static item => item.DataClass)];

    }

    private static string[] WorkspaceResetCandidateIds(
        DataRetentionWorkspaceBinding binding,
        string workspaceRoot) =>
        [
            "campaign:" + binding.CampaignId.ToString("N"),
            "workspace:" + workspaceRoot,
        ];

    private static async Task<WorkspaceResetSnapshot> ReadWorkspaceResetSnapshotAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {

        long workspaceContexts = await WorkspaceResetCountAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM \"WorkspaceContexts\" WHERE \"RootPath\" = @root",
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        long workspaceChunks = await WorkspaceResetCountAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM workspace_file_chunks WHERE WorkspacePath = @root",
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        long workspaceEmbeddings = await WorkspaceResetCountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM workspace_file_embeddings embedding
            JOIN workspace_file_chunks chunk ON chunk.ChunkId = embedding.ChunkId
            WHERE chunk.WorkspacePath = @root
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        bool hasWorkspaceVectors = await WorkspaceResetTableExistsAsync(
            connection,
            transaction,
            "workspace_file_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        long workspaceVectors = hasWorkspaceVectors
            ? await WorkspaceResetCountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM workspace_file_embeddings_vec embedding
                JOIN workspace_file_chunks chunk ON chunk.ChunkId = embedding.ChunkId
                WHERE chunk.WorkspacePath = @root
                """,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false)
            : 0;

        long tapestryGenerations = await WorkspaceResetCountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM tapestry_generations
            WHERE ScopeKind = 'Workspace' AND ScopeId = @root
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        long tapestryNodes = await WorkspaceResetCountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM tapestry_nodes
            WHERE ScopeKind = 'Workspace' AND ScopeId = @root
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        long tapestryEmbeddings = await WorkspaceResetCountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM tapestry_node_embeddings embedding
            JOIN tapestry_nodes node ON node.NodeId = embedding.NodeId
            WHERE node.ScopeKind = 'Workspace' AND node.ScopeId = @root
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        bool hasTapestryVectors = await WorkspaceResetTableExistsAsync(
            connection,
            transaction,
            "tapestry_node_embeddings_vec",
            cancellationToken).ConfigureAwait(false);

        long tapestryVectors = hasTapestryVectors
            ? await WorkspaceResetCountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM tapestry_node_embeddings_vec embedding
                JOIN tapestry_nodes node ON node.NodeId = embedding.NodeId
                WHERE node.ScopeKind = 'Workspace' AND node.ScopeId = @root
                """,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false)
            : 0;

        long crossScopeGenerationRows = await WorkspaceResetCountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM tapestry_nodes node
            JOIN tapestry_generations generation
              ON generation.GenerationId = node.GenerationId
            WHERE generation.ScopeKind = 'Workspace'
              AND generation.ScopeId = @root
              AND (node.ScopeKind <> 'Workspace' OR node.ScopeId <> @root)
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        using IncrementalHash resourceFingerprint = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "workspace-context:",
            "SELECT \"Id\" FROM \"WorkspaceContexts\" WHERE \"RootPath\" = @root ORDER BY \"Id\"",
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "workspace-chunk:",
            "SELECT ChunkId FROM workspace_file_chunks WHERE WorkspacePath = @root ORDER BY ChunkId",
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "workspace-embedding:",
            """
            SELECT embedding.ChunkId
            FROM workspace_file_embeddings embedding
            JOIN workspace_file_chunks chunk ON chunk.ChunkId = embedding.ChunkId
            WHERE chunk.WorkspacePath = @root
            ORDER BY embedding.ChunkId
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        if (hasWorkspaceVectors)
        {

            await AppendWorkspaceResetFingerprintAsync(
                resourceFingerprint,
                connection,
                transaction,
                "workspace-vector:",
                """
                SELECT embedding.ChunkId
                FROM workspace_file_embeddings_vec embedding
                JOIN workspace_file_chunks chunk ON chunk.ChunkId = embedding.ChunkId
                WHERE chunk.WorkspacePath = @root
                ORDER BY embedding.ChunkId
                """,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false);

        }

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "tapestry-generation:",
            """
            SELECT GenerationId
            FROM tapestry_generations
            WHERE ScopeKind = 'Workspace' AND ScopeId = @root
            ORDER BY GenerationId
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "tapestry-node:",
            """
            SELECT NodeId
            FROM tapestry_nodes
            WHERE ScopeKind = 'Workspace' AND ScopeId = @root
            ORDER BY NodeId
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        await AppendWorkspaceResetFingerprintAsync(
            resourceFingerprint,
            connection,
            transaction,
            "tapestry-embedding:",
            """
            SELECT embedding.NodeId
            FROM tapestry_node_embeddings embedding
            JOIN tapestry_nodes node ON node.NodeId = embedding.NodeId
            WHERE node.ScopeKind = 'Workspace' AND node.ScopeId = @root
            ORDER BY embedding.NodeId
            """,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        if (hasTapestryVectors)
        {

            await AppendWorkspaceResetFingerprintAsync(
                resourceFingerprint,
                connection,
                transaction,
                "tapestry-vector:",
                """
                SELECT embedding.NodeId
                FROM tapestry_node_embeddings_vec embedding
                JOIN tapestry_nodes node ON node.NodeId = embedding.NodeId
                WHERE node.ScopeKind = 'Workspace' AND node.ScopeId = @root
                ORDER BY embedding.NodeId
                """,
                workspaceRoot,
                cancellationToken).ConfigureAwait(false);

        }

        return new WorkspaceResetSnapshot(
            workspaceContexts,
            workspaceChunks,
            workspaceEmbeddings,
            workspaceVectors,
            tapestryGenerations,
            tapestryNodes,
            tapestryEmbeddings,
            tapestryVectors,
            crossScopeGenerationRows,
            Convert.ToHexString(resourceFingerprint.GetHashAndReset()));

    }

    private static async Task<long> WorkspaceResetCountAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "@root", workspaceRoot);

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);

    }

    private static async Task<bool> WorkspaceResetTableExistsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name = @table AND type = 'table')";

        Add(command, "@table", table);

        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;

    }

    private static async Task AppendWorkspaceResetFingerprintAsync(
        IncrementalHash fingerprint,
        DbConnection connection,
        DbTransaction? transaction,
        string prefix,
        string sql,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "@root", workspaceRoot);

        await using DbDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            fingerprint.AppendData(
                Encoding.UTF8.GetBytes(
                    prefix + reader.GetString(0) + "\n"));

        }

    }

    private sealed record WorkspaceResetSnapshot(
        long WorkspaceContextRows,
        long WorkspaceChunkRows,
        long WorkspaceEmbeddingRows,
        long WorkspaceVectorRows,
        long TapestryGenerationRows,
        long TapestryNodeRows,
        long TapestryEmbeddingRows,
        long TapestryVectorRows,
        long CrossScopeGenerationRows,
        string ResourceFingerprint)
    {

        internal long TotalOwnedRows => WorkspaceContextRows
            + WorkspaceChunkRows
            + WorkspaceEmbeddingRows
            + WorkspaceVectorRows
            + TapestryGenerationRows
            + TapestryNodeRows
            + TapestryEmbeddingRows
            + TapestryVectorRows;

    }

}
