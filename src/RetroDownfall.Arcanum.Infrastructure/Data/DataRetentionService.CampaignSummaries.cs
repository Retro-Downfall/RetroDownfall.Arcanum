using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class DataRetentionService
{
    private const string CampaignRollupCandidatePrefix = "campaign-rollup:";

    private const string CampaignContributionCandidatePrefix = "campaign-contribution:";

    private async Task AddCampaignSummaryCandidatesAsync(
        RetentionSettings retention,
        int limit,
        IReadOnlySet<Guid> selectedSessions,
        HashSet<string> coveredRows,
        List<DataRetentionPlanItem> items,
        List<string> candidates,
        CancellationToken cancellationToken)
    {
        RetentionRuleSettings rule = retention.CampaignSummaries;

        if (!rule.Enabled || candidates.Count >= limit
            || !await TableExistsAsync("campaign_rollup_artifacts", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset cutoff = PrunePlanningTimestamp.AddDays(-ArcanumSettingClamps.RetentionRuleDays(rule.Days));

        foreach ((SensitiveArtifactKind kind, string table, string prefix) in new[]
        {
            (SensitiveArtifactKind.CampaignRollup, "campaign_rollup_artifacts", CampaignRollupCandidatePrefix),
            (SensitiveArtifactKind.CampaignContribution, "campaign_contribution_artifacts", CampaignContributionCandidatePrefix),
        })
        {
            if (!await TableExistsAsync(table, cancellationToken).ConfigureAwait(false))
                continue;

            string[] ids = await ReadEligibleCampaignSummaryIdsAsync(connection, null, kind, cutoff,
                limit - candidates.Count, cancellationToken).ConfigureAwait(false);

            foreach (string id in ids)
            {
                Guid artifactId = Guid.Parse(id);

                Guid[] owners = await ReadCampaignSummaryDependenciesAsync(connection, kind, artifactId, sessions: true, cancellationToken)
                    .ConfigureAwait(false);

                if (owners.Any(selectedSessions.Contains))
                {
                    continue;
                }

                string[] closureKeys = await CampaignSummaryLifecycle.ReadArtifactClosureRowKeysAsync(
                    connection, null, kind, artifactId, cancellationToken).ConfigureAwait(false);

                long closure = closureKeys.LongCount(coveredRows.Add);

                candidates.Add(prefix + artifactId.ToString("D").ToUpperInvariant());

                items.Add(new(RetentionDataClass.CampaignSummaries, 1, 0, 0, Math.Max(0, closure - 1)));
            }
        }

        int priorCount = coveredRows.Count;

        await CampaignSummaryLifecycle.AddSharedReclamationRowKeysAsync(
            connection, null, coveredRows, cancellationToken).ConfigureAwait(false);

        if (coveredRows.Count > priorCount)
        {
            items.Add(new(RetentionDataClass.CampaignSummaries, 0, 0, 0, coveredRows.Count - priorCount));
        }
    }

    private static async Task<string[]> ReadEligibleCampaignSummaryIdsAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        SensitiveArtifactKind kind,
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken,
        Guid? exactArtifactId = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = kind switch
        {
            SensitiveArtifactKind.CampaignRollup => """
                SELECT artifact.ArtifactId FROM campaign_rollup_artifacts artifact
                WHERE artifact.SensitivityCode = 0 AND artifact.CreatedAtUtc <= @cutoff
                    AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label
                        WHERE label.ArtifactKindCode = @kind
                            AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(artifact.ArtifactId, '-', '')))
                """,
            SensitiveArtifactKind.CampaignContribution => """
                SELECT artifact.ArtifactId FROM campaign_contribution_artifacts artifact
                WHERE artifact.SensitivityCode = 0 AND artifact.CreatedAtUtc <= @cutoff
                    AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label
                        WHERE label.ArtifactKindCode = @kind
                            AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(artifact.ArtifactId, '-', '')))
                    AND NOT EXISTS (
                        SELECT 1 FROM campaign_rollup_source_membership source
                        LEFT JOIN campaign_rollup_artifacts rollup ON rollup.ArtifactId = source.RollupArtifactId
                        WHERE source.ContributionArtifactId = artifact.ArtifactId
                            AND (rollup.SensitivityCode <> 0 OR EXISTS (
                                SELECT 1 FROM artifact_sensitivity label
                                WHERE label.ArtifactKindCode = @rollupKind
                                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(source.RollupArtifactId, '-', '')))))
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (exactArtifactId is { } id)
        {
            command.CommandText += " AND artifact.ArtifactId = @id";
            Add(command, "@id", id.ToString("D").ToUpperInvariant());
        }
        command.CommandText += " ORDER BY artifact.CreatedAtUtc, artifact.ArtifactId LIMIT @limit;";
        Add(command, "@kind", (int)kind);
        Add(command, "@rollupKind", (int)SensitiveArtifactKind.CampaignRollup);
        Add(command, "@cutoff", FormatTimestamp(cutoff));
        Add(command, "@limit", limit);
        await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken).ConfigureAwait(false);
        List<string> ids = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return [.. ids];
    }

    private async Task<HashSet<string>> AddNativeOwnerClosureItemsAsync(
        IReadOnlySet<Guid> selectedSessions,
        IReadOnlySet<Guid> selectedEntryOwners,
        List<DataRetentionPlanItem> items,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        HashSet<string> campaignRows = new(StringComparer.Ordinal);

        items.RemoveAll(static item => item.DataClass == RetentionDataClass.CampaignSummaries);

        foreach (Guid owner in selectedSessions.Concat(selectedEntryOwners).Distinct().Order())
        {
            _ = await CampaignSummaryLifecycle.ReadSessionClosureAsync(
                connection, null, owner, cancellationToken, includeOwnerRows: selectedSessions.Contains(owner), rowKeys: campaignRows)
                .ConfigureAwait(false);

            if (!selectedSessions.Contains(owner))
            {
                SessionSummaryClosureSnapshot summaries = await SessionSummaryLifecycle.ReadClosureAsync(
                    connection, null, owner, cancellationToken).ConfigureAwait(false);

                if (summaries.Rows > 0)
                {
                    items.Add(new(RetentionDataClass.Entries, 0, 0, 0, summaries.Rows));
                }
            }
        }

        await CampaignSummaryLifecycle.AddSharedReclamationRowKeysAsync(
            connection, null, campaignRows, cancellationToken).ConfigureAwait(false);

        if (campaignRows.Count > 0)
        {
            items.Add(new(RetentionDataClass.CampaignSummaries, 0, 0, 0, campaignRows.Count));
        }

        return campaignRows;
    }

    private static async Task<Guid[]> ReadCampaignSummaryDependenciesAsync(
        SqliteConnection connection,
        SensitiveArtifactKind kind,
        Guid artifactId,
        bool sessions,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sessions
            ? kind == SensitiveArtifactKind.CampaignContribution
                ? "SELECT SessionId FROM campaign_contribution_artifacts WHERE ArtifactId = @id;"
                : "SELECT DISTINCT SessionId FROM campaign_rollup_source_membership WHERE RollupArtifactId = @id;"
            : "SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE ContributionArtifactId = @id;";

        Add(command, "@id", artifactId.ToString("D").ToUpperInvariant());

        await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken).ConfigureAwait(false);

        List<Guid> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return [.. ids];
    }

    private async Task<CandidateDeleteResult> DeleteCampaignSummaryCandidateAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginMutationTransactionAsync(connection, cancellationToken).ConfigureAwait(false);

        string table = kind == SensitiveArtifactKind.CampaignRollup
            ? "campaign_rollup_artifacts"
            : "campaign_contribution_artifacts";

        string id = artifactId.ToString("D").ToUpperInvariant();

        string[] eligible = await ReadEligibleCampaignSummaryIdsAsync(connection, transaction, kind, cutoff,
            1, cancellationToken, artifactId).ConfigureAwait(false);

        if (eligible.Length == 0)
        {
            return CandidateDeleteResult.Empty;
        }

        Result unlabeled = await CampaignSummaryLifecycle.EnsureArtifactUnlabeledAsync(
            connection, transaction, kind, artifactId, labeledArtifactGuard, cancellationToken).ConfigureAwait(false);

        if (unlabeled.IsFailure)
        {
            return CandidateDeleteResult.Empty;
        }

        using IDisposable cleanup = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection, CovenantSqliteAuthorizationKind.SessionRetention);

        long deleted = await CampaignSummaryLifecycle.PurgeArtifactAsync(
            connection, (SqliteTransaction)transaction, kind, artifactId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        bool reconciled = await CountTableAsync(table, "ArtifactId = @id", cancellationToken, ("@id", id)).ConfigureAwait(false) == 0;

        return new(1, 0, 0, Math.Max(0, deleted - 1), reconciled);
    }
}
