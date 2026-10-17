using System.Data.Common;
using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Exact generation closure for a derived node and its authoritative content proof.</summary>
internal static class LongRestLifecycle
{
    // Queries are code-owned. Only the exact owner identity is supplied by a caller.
    private const string CampaignGenerations =
        "SELECT GenerationId FROM long_rest_discovery_generations WHERE ScopeKindCode = 2 AND CampaignId = $owner";

    private static readonly string SessionGenerations =
        "SELECT leaf.GenerationId FROM long_rest_discovery_leaves leaf JOIN saga_memories memory ON memory.Id = leaf.MemoryId WHERE "
        + CovenantIdentitySql.Keyed("memory.SessionId", "$owner");

    /// <summary>The logical generation inventory shared by Annals and native owner deletion.</summary>
    internal static IReadOnlyList<AnnalsErasureStep> GenerationSteps(string generationScope)
    {
        string nodeScope = $"SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId IN ({generationScope})";

        string workScope = $"SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId IN ({generationScope})";

        return
        [
            // The root leads deletion: its trigger closes the exact graph before a node or
            // source mutation can invalidate the leaves that select it. Count mode measures
            // every declared logical companion before that trigger or an FK cascade runs.
            new("long_rest_discovery_generations", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_clean_fts", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_protected_fts", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_discovery_embeddings", $"NodeId IN ({nodeScope})", RequiredFromCoreVersion: 18),
            new("long_rest_discovery_edges", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_accepted_summaries", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_adjudications", $"WorkItemId IN ({workScope})", RequiredFromCoreVersion: 18),
            new("long_rest_provider_effects", $"WorkItemId IN ({workScope})", RequiredFromCoreVersion: 18),
            new("long_rest_work_inputs", $"WorkItemId IN ({workScope})", RequiredFromCoreVersion: 18),
            new("long_rest_work_items", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_discovery_nodes", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_discovery_leaves", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
            new("long_rest_sweeps", $"GenerationId IN ({generationScope})", RequiredFromCoreVersion: 18),
        ];
    }

    internal static Task<long> ClearCampaignAsync(DbConnection connection, DbTransaction transaction,
        Guid campaignId, CancellationToken cancellationToken) =>
        MeasureOrClearAsync(connection, transaction, CampaignGenerations, campaignId.ToString("D").ToUpperInvariant(),
            clear: true, cancellationToken);

    internal static Task<long> ReadSessionClosureAsync(DbConnection connection, DbTransaction? transaction,
        Guid sessionId, CancellationToken cancellationToken) =>
        MeasureOrClearAsync(connection, transaction, SessionGenerations, CovenantIdentitySql.Key(sessionId),
            clear: false, cancellationToken);

    internal static Task<long> ClearSessionAsync(DbConnection connection, DbTransaction transaction,
        Guid sessionId, CancellationToken cancellationToken) =>
        MeasureOrClearAsync(connection, transaction, SessionGenerations, CovenantIdentitySql.Key(sessionId),
            clear: true, cancellationToken);

    private static async Task<long> MeasureOrClearAsync(DbConnection connection, DbTransaction? transaction,
        string generationQuery, string owner, bool clear, CancellationToken cancellationToken)
    {
        if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 18)
        {
            return 0;
        }

        IReadOnlyList<AnnalsErasureStep> steps = GenerationSteps(generationQuery);

        long rows = 0;

        // All logical rows are measured before the root's explicit closure or any FK cascade.
        foreach (AnnalsErasureStep step in steps)
        {
            rows += await ExecuteOwnerStepAsync(connection, transaction, step, owner, count: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (clear)
        {
            foreach (AnnalsErasureStep step in steps)
            {
                _ = await ExecuteOwnerStepAsync(connection, transaction, step, owner, count: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return rows;
    }

    private static async Task<long> ExecuteOwnerStepAsync(DbConnection connection, DbTransaction? transaction,
        AnnalsErasureStep step, string owner, bool count, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = count
            ? $"SELECT count(*) FROM {step.Table} WHERE {step.Predicate};"
            : $"DELETE FROM {step.Table} WHERE {step.Predicate};";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "$owner";

        parameter.Value = owner;

        _ = command.Parameters.Add(parameter);

        return count
            ? Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture)
            : await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<CovenantArtifactPurgeTarget> Projections { get; } =
    [
        Target("long_rest_clean_fts", "NodeId"),
        Target("long_rest_protected_fts", "NodeId"),
        Target("long_rest_discovery_embeddings", "NodeId"),
        Target("long_rest_discovery_edges", "ParentNodeId", "ParentNodeId || ':' || Ordinal"),
        Target("long_rest_accepted_summaries", "WorkItemId"),
        Target("long_rest_adjudications", "WorkItemId"),
        Target("long_rest_provider_effects", "WorkItemId"),
        Target("long_rest_work_inputs", "WorkItemId", "WorkItemId || ':' || Ordinal"),
        Target("long_rest_work_items", "WorkItemId"),
    ];

    internal static CovenantArtifactPurgeTarget Artifact { get; } = Target("long_rest_discovery_nodes", "NodeId");

    private static CovenantArtifactPurgeTarget Target(string table, string key, string? rowIdentity = null) =>
        new(table, key, RequiredFromCoreVersion: 18, RowIdentity: rowIdentity, LongRestNode: true);

    internal static string Predicate(string table, string parameter)
    {
        string generations = "SELECT GenerationId FROM long_rest_discovery_nodes WHERE " + CovenantIdentitySql.Keyed("NodeId", parameter);

        string nodes = $"SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId IN ({generations})";

        string items = $"SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId IN ({generations})";

        return table switch
        {
            "long_rest_discovery_embeddings" => $"NodeId IN ({nodes})",
            "long_rest_adjudications" or "long_rest_provider_effects" or "long_rest_work_inputs" => $"WorkItemId IN ({items})",
            "long_rest_clean_fts" or "long_rest_protected_fts" or "long_rest_discovery_edges"
                or "long_rest_accepted_summaries" or "long_rest_work_items" or "long_rest_discovery_nodes" => $"GenerationId IN ({generations})",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    internal static async Task<Result> ValidateArtifactAsync(SqliteConnection connection, SqliteTransaction transaction,
        ArtifactSensitivityLabel label, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT node.Summary, node.SummaryDigest, node.Revision, node.SensitivityCode, node.PartitionDigest,
                generation.CampaignId FROM long_rest_discovery_nodes node
            JOIN long_rest_discovery_generations generation ON generation.GenerationId = node.GenerationId
            WHERE node.NodeId = $node AND node.Layer > 0;
            """;

        _ = command.Parameters.AddWithValue("$node", label.ArtifactId.ToString("D").ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure();
        }

        string content = reader.GetString(0);

        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(content) is < 1 or > 8192)
            {
                return Failure();
            }
        }
        catch (EncoderFallbackException)
        {
            return Failure();
        }

        CovenantDigest digest = DerivedArtifactContentDigest.ForText(content);

        return label.ArtifactKind == SensitiveArtifactKind.LongRestDiscovery && label.SessionId is null
            && label.CampaignId == (reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)))
            && label.ArtifactRevision == checked((ulong)reader.GetInt64(2))
            && label.Sensitivity == ContentSensitivity.CovenantDerived && reader.GetInt64(3) == 1
            && !reader.IsDBNull(4) && label.SensitivityDigest == new CovenantDigest((byte[])reader[4])
            && digest == label.ArtifactContentDigest && digest == new CovenantDigest((byte[])reader[1])
                ? Result.Success() : Failure();
    }

    private static Result Failure() => Result.Failure(new Error(ErrorCodes.Covenant.IntegrityFailure,
        "The derived Long Rest node no longer matches its canonical owner, revision, partition and content label."));
}
