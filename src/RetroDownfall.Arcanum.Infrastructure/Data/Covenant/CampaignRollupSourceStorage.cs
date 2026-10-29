using System.Data.Common;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Schema-aware logical membership and physical provenance cleanup predicates.</summary>
internal static class CampaignRollupSourceStorage
{
    internal static async Task<bool> IsInstalledAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct, RetentionSchemaCensus? schemaCensus = null)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'
                AND name IN ('campaign_rollup_manifests', 'campaign_rollup_manifest_sources'))
                + (SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'campaign_rollup_source_membership')
                + (SELECT COUNT(*) FROM pragma_table_info('campaign_rollup_artifacts') WHERE name = 'ManifestId');
            """;

        return (schemaCensus is null
            ? await command.ExecuteScalarAsync(ct).ConfigureAwait(false)
            : await schemaCensus.ReadScalarAsync(command, ct).ConfigureAwait(false)) switch
        {
            0L => false,
            4L => true,
            _ => throw new InvalidDataException("The Campaign shared-provenance schema is incomplete."),
        };
    }

    internal static async Task ResolveMembershipAsync(DbCommand command, CancellationToken ct, RetentionSchemaCensus? schemaCensus = null)
    {
        if (command.CommandText.Contains("campaign_rollup_source_membership", StringComparison.Ordinal)
            && !await IsInstalledAsync(command.Connection!, command.Transaction, ct, schemaCensus).ConfigureAwait(false))
        {
            command.CommandText = command.CommandText.Replace("campaign_rollup_source_membership", "campaign_rollup_sources", StringComparison.Ordinal);
        }
    }

    internal static string PurgePredicate(string table, string parameter) =>
        $"ManifestId IN ({UnreferencedSegments($"SELECT ArtifactId FROM campaign_rollup_artifacts WHERE {CovenantIdentitySql.Keyed("ArtifactId", parameter)}")})";

    internal static string UnreferencedSegments(string selectedArtifacts) =>
        $"""
        SELECT segment.ManifestId FROM campaign_rollup_manifests segment
        WHERE EXISTS (SELECT 1 FROM campaign_rollup_artifacts selected
            JOIN campaign_rollup_manifests selectedRoot ON selectedRoot.ManifestId = selected.ManifestId
            WHERE selected.ArtifactId IN ({selectedArtifacts}) AND selectedRoot.LineageId = segment.LineageId)
            AND NOT EXISTS (SELECT 1 FROM campaign_rollup_artifacts retained
                JOIN campaign_rollup_manifests root ON root.ManifestId = retained.ManifestId
                WHERE root.LineageId = segment.LineageId AND root.Ordinal >= segment.Ordinal
                    AND retained.ArtifactId NOT IN ({selectedArtifacts}))
        """;
}
