using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Exact, transaction-bound cleanup of Campaign continuity and its source dependencies.</summary>
/// <remarks>The caller owns the immediate transaction, label admission, and SQL authorization.</remarks>
internal static class CampaignSummaryLifecycle
{
    internal static async Task<Result> EnsureSessionUnlabeledAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        ICovenantLabeledArtifactTransactionGuard guard,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return Result.Success();
        }

        Guid[] contributions = await ReadIdsAsync(connection, transaction,
            "SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $ownerId;",
            sessionId, cancellationToken).ConfigureAwait(false);

        Result admitted = contributions.Length == 0
            ? Result.Success()
            : await guard.EnsureAllUnlabeledAsync(
                SensitiveArtifactKind.CampaignContribution, contributions, connection, transaction, cancellationToken)
                .ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted;
        }

        Guid[] rollups = await ReadIdsAsync(connection, transaction,
            "SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE SessionId = $ownerId;",
            sessionId, cancellationToken).ConfigureAwait(false);

        return rollups.Length == 0
            ? Result.Success()
            : await guard.EnsureAllUnlabeledAsync(
                SensitiveArtifactKind.CampaignRollup, rollups, connection, transaction, cancellationToken)
                .ConfigureAwait(false);
    }

    internal static async Task<Result> EnsureCampaignUnlabeledAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid? campaignId,
        ICovenantLabeledArtifactTransactionGuard guard,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return Result.Success();
        }

        foreach ((SensitiveArtifactKind kind, string table) in new[]
        {
            (SensitiveArtifactKind.CampaignContribution, "campaign_contribution_artifacts"),
            (SensitiveArtifactKind.CampaignRollup, "campaign_rollup_artifacts"),
        })
        {
            Guid[] artifacts = await ReadIdsAsync(connection, transaction,
                campaignId is null
                    ? $"SELECT ArtifactId FROM {table};"
                    : $"SELECT ArtifactId FROM {table} WHERE CampaignId = $ownerId;",
                campaignId, cancellationToken).ConfigureAwait(false);

            if (artifacts.Length == 0)
            {
                continue;
            }

            Result admitted = await guard.EnsureAllUnlabeledAsync(
                kind, artifacts, connection, transaction, cancellationToken).ConfigureAwait(false);

            if (admitted.IsFailure)
            {
                return admitted;
            }
        }

        return Result.Success();
    }

    internal static async Task<Result> EnsureArtifactUnlabeledAsync(
        DbConnection connection,
        DbTransaction transaction,
        SensitiveArtifactKind kind,
        Guid artifactId,
        ICovenantLabeledArtifactTransactionGuard guard,
        CancellationToken cancellationToken)
    {
        Result selected = await guard.EnsureUnlabeledAsync(
            kind, artifactId, connection, transaction, cancellationToken).ConfigureAwait(false);

        if (selected.IsFailure || kind != SensitiveArtifactKind.CampaignContribution)
        {
            return selected;
        }

        Guid[] dependents = await ReadIdsAsync(connection, transaction,
            "SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE ContributionArtifactId = $ownerId;",
            artifactId, cancellationToken).ConfigureAwait(false);

        return dependents.Length == 0
            ? Result.Success()
            : await guard.EnsureAllUnlabeledAsync(
                SensitiveArtifactKind.CampaignRollup, dependents, connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<CovenantOperationScope[]> ReadDependencyOwnersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SensitiveArtifactKind kind,
        Guid artifactId,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        bool installed = await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        bool summariesInstalled = kind == SensitiveArtifactKind.AssistantEntry && sessionId is not null
            && await SessionSummaryLifecycle.IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        string summaryOwnerPredicate = summariesInstalled
            ? "(SessionId = $sessionId OR lower(replace(ArtifactId, '-', '')) IN "
                + "(SELECT lower(replace(ArtifactId, '-', '')) FROM session_summary_artifacts WHERE SessionId = $sessionId))"
            : "SessionId = $sessionId";

        string? predicate = kind switch
        {
            SensitiveArtifactKind.AssistantEntry when sessionId is not null =>
                $"(ArtifactKindCode = $summaryKind AND {summaryOwnerPredicate})"
                + (installed
                    ? " OR (ArtifactKindCode = 15 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(ArtifactId, '-', '')) FROM campaign_contribution_artifacts WHERE SessionId = $sessionId))"
                        + " OR (ArtifactKindCode = 14 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(RollupArtifactId, '-', '')) FROM campaign_rollup_source_membership WHERE SessionId = $sessionId))"
                    : ""),
            SensitiveArtifactKind.CampaignContribution when installed =>
                "ArtifactKindCode = 14 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(RollupArtifactId, '-', '')) FROM campaign_rollup_source_membership WHERE ContributionArtifactId = $artifactId)",
            _ => null,
        };

        if (predicate is null)
        {
            return [];
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT DISTINCT CampaignId FROM artifact_sensitivity WHERE {predicate};";

        Add(command, "$summaryKind", (long)SensitiveArtifactKind.Summary);

        Add(command, "$artifactId", Format(artifactId));

        Add(command, "$sessionId", sessionId is { } session ? Format(session) : DBNull.Value);

        List<CovenantOperationScope> owners = [];

        await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken).ConfigureAwait(false);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            owners.Add(reader.IsDBNull(0)
                ? CovenantOperationScope.Global
                : CovenantOperationScope.ForCampaign(Guid.Parse(reader.GetString(0))));
        }

        return [.. owners];
    }

    internal static async Task<CampaignSummaryClosureSnapshot> ReadSessionClosureAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken,
        bool includeOwnerRows = true,
        HashSet<string>? rowKeys = null,
        RetentionSchemaCensus? schemaCensus = null)
    {
        await using DbCommand installed = connection.CreateCommand();

        installed.Transaction = transaction;

        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'campaign_rollup_artifacts');";

        if (Convert.ToInt64(schemaCensus is null
                ? await installed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                : await schemaCensus.ReadScalarAsync(installed, cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return new CampaignSummaryClosureSnapshot(0, "");
        }

        const string contributions = "SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $ownerId";

        const string rollups = "SELECT RollupArtifactId FROM campaign_rollup_source_membership WHERE SessionId = $ownerId";

        (string Table, string Predicate, string Identity, string Key)[] targets =
        [
            ("campaign_contribution_artifacts", "SessionId = $ownerId", "ArtifactId || ':' || Revision || ':' || hex(SourceManifestDigest)", "ArtifactId"),
            ("campaign_rollup_artifacts", $"ArtifactId IN ({rollups})", "ArtifactId || ':' || Revision || ':' || hex(SourceManifestDigest)", "ArtifactId"),
            ("campaign_rollup_sources", $"RollupArtifactId IN ({rollups})", "RollupArtifactId || ':' || SessionId || ':' || ContributionArtifactId || ':' || ContributionRevision || ':' || hex(ContentDigest) || ':' || hex(SensitivityDigest)", "RollupArtifactId || ':' || SessionId"),
            ("campaign_maintenance_checkpoints", $"SessionId = $ownerId OR SourceSessionId = $ownerId OR (OutputArtifactKindCode = 15 AND OutputArtifactId IN ({contributions})) OR (OutputArtifactKindCode = 14 AND OutputArtifactId IN ({rollups}))", "ClaimId || ':' || StepCode || ':' || hex(InputManifestDigest) || ':' || CheckpointRevision", "ClaimId || ':' || StepCode || ':' || hex(InputManifestDigest)"),
            ("campaign_contribution_state", "SessionId = $ownerId", "SessionId || ':' || Revision || ':' || SourceGeneration || ':' || SummarizedThroughSequence", "SessionId"),
            ("campaign_fork_frontiers", "SessionId = $ownerId", "SessionId || ':' || SourceSessionId || ':' || InheritedThroughSequence || ':' || ProofKindCode", "SessionId"),
            ("artifact_sensitivity", "(ArtifactKindCode = 15 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(ArtifactId, '-', '')) FROM campaign_contribution_artifacts WHERE SessionId = $ownerId)) OR (ArtifactKindCode = 14 AND lower(replace(ArtifactId, '-', '')) IN (SELECT lower(replace(RollupArtifactId, '-', '')) FROM campaign_rollup_source_membership WHERE SessionId = $ownerId))", "LabelId || ':' || ArtifactRevision || ':' || hex(ArtifactContentDigest)", "LabelId"),
        ];

        bool sharedProvenance = await CampaignRollupSourceStorage.IsInstalledAsync(
            connection, transaction, cancellationToken, schemaCensus).ConfigureAwait(false);

        if (sharedProvenance)
        {
            string unreferenced = CampaignRollupSourceStorage.UnreferencedSegments(rollups);

            targets = [.. targets,
                ("campaign_rollup_manifest_sources", $"ManifestId IN ({unreferenced})",
                    "ManifestId || ':' || SessionId || ':' || ContributionArtifactId || ':' || ContributionRevision || ':' || hex(ContentDigest) || ':' || hex(SensitivityDigest) || ':' || SummarizedThroughSequence", "ManifestId || ':' || SessionId"),
                ("campaign_rollup_manifests", $"ManifestId IN ({unreferenced})",
                    "ManifestId || ':' || LineageId || ':' || CampaignId || ':' || HashFormatCode || ':' || Ordinal || ':' || hex(PriorManifestDigest) || ':' || hex(SourceManifestDigest) || ':' || SourceCount || ':' || AddedSourceCount", "ManifestId"),
            ];
        }

        long rows = 0;

        using IncrementalHash authority = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach ((string table, string predicate, string identity, string key) in targets)
        {
            if (!includeOwnerRows && table is "campaign_contribution_state" or "campaign_fork_frontiers")
            {
                continue;
            }

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT {identity}, {key} FROM {table} WHERE {predicate} ORDER BY 1;";

            Add(command, "$ownerId", Format(sessionId));

            if (transaction is not null)
            {
                // Only reads follow the classification in this caller-owned transaction: no guard
                // callbacks, writes or savepoints can invalidate it during this closure phase.
                // Autocommit callers below must still observe DDL between their separate reads.
                if (!sharedProvenance)
                {
                    command.CommandText = command.CommandText.Replace("campaign_rollup_source_membership", "campaign_rollup_sources", StringComparison.Ordinal);
                }
            }
            else
            {
                await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken, schemaCensus).ConfigureAwait(false);
            }

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string value = reader.GetString(0);

                // Preserve the legacy UTF16 length prefix and UTF8 authority bytes exactly.
                AppendAuthority(authority, table, cancellationToken);

                AppendAuthority(authority, ":", cancellationToken);

                AppendAuthority(authority, value.Length.ToString(System.Globalization.CultureInfo.CurrentCulture), cancellationToken);

                AppendAuthority(authority, ":", cancellationToken);

                AppendAuthority(authority, value, cancellationToken);

                AppendAuthority(authority, "\n", cancellationToken);

                if (rowKeys is not null)
                {
                    _ = rowKeys.Add(table + ":" + reader.GetString(1));
                }

                rows = checked(rows + 1);
            }
        }

        return new CampaignSummaryClosureSnapshot(rows, Convert.ToHexString(authority.GetHashAndReset()));
    }

    private static void AppendAuthority(
        IncrementalHash authority,
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[1024];

        while (!value.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int characters = Math.Min(value.Length, 256);

            // Keep surrogate pairs together while preserving Encoding.UTF8's legacy fallback.
            if (characters < value.Length
                && char.IsHighSurrogate(value[characters - 1])
                && char.IsLowSurrogate(value[characters]))
            {
                characters--;
            }

            int bytes = Encoding.UTF8.GetBytes(value[..characters], buffer);

            authority.AppendData(buffer[..bytes]);

            value = value[characters..];
        }
    }

    internal static async Task<string[]> ReadArtifactClosureRowKeysAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (kind is not (SensitiveArtifactKind.CampaignRollup or SensitiveArtifactKind.CampaignContribution))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        HashSet<string> rows = new(StringComparer.Ordinal);

        if (kind == SensitiveArtifactKind.CampaignContribution)
        {
            Guid[] dependents = await ReadIdsAsync(connection, transaction,
                "SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE ContributionArtifactId = $ownerId;",
                artifactId, cancellationToken).ConfigureAwait(false);

            foreach (Guid dependent in dependents)
            {
                rows.UnionWith(await ReadArtifactClosureRowKeysAsync(connection, transaction,
                    SensitiveArtifactKind.CampaignRollup, dependent, cancellationToken).ConfigureAwait(false));
            }
        }

        CovenantArtifactPurgePlan plan = CovenantArtifactPurgePlans.Resolve(kind);

        foreach (CovenantArtifactPurgeTarget target in plan.Projections.Append(plan.Artifact!))
        {
            if (target.CampaignManifestNode
                && !await CampaignRollupSourceStorage.IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = target.ReadIdentitiesBy("$artifactKey");

            Add(command, "$artifactKey", CovenantIdentitySql.Key(artifactId));

            await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken).ConfigureAwait(false);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(target.Table + ":" + reader.GetString(0));
            }
        }

        await using SqliteCommand labels = connection.CreateCommand();

        labels.Transaction = transaction;

        labels.CommandText = "SELECT LabelId FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND "
            + CovenantIdentitySql.Keyed("ArtifactId", "$artifactKey") + ";";

        Add(labels, "$kind", (long)kind);

        Add(labels, "$artifactKey", CovenantIdentitySql.Key(artifactId));

        await using SqliteDataReader labelled = await labels.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await labelled.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add("artifact_sensitivity:" + labelled.GetString(0));
        }

        if (kind == SensitiveArtifactKind.CampaignContribution)
        {
            await AddSharedReclamationRowKeysAsync(connection, transaction, rows, cancellationToken).ConfigureAwait(false);
        }

        return [.. rows.Order(StringComparer.Ordinal)];
    }

    // A shared segment can become unreferenced only after several independently selected roots
    // disappear. Resolve the complete selected root set, including roots selected by owner closure.
    internal static async Task AddSharedReclamationRowKeysAsync(
        DbConnection connection,
        DbTransaction? transaction,
        HashSet<string> rows,
        CancellationToken cancellationToken)
    {
        const string prefix = "campaign_rollup_artifacts:";

        string[] selected = [.. rows.Where(row => row.StartsWith(prefix, StringComparison.Ordinal))
            .Select(row => row[prefix.Length..])];

        if (selected.Length == 0
            || !await CampaignRollupSourceStorage.IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string segments = CampaignRollupSourceStorage.UnreferencedSegments("SELECT value FROM json_each($selectedArtifacts)");

        string selectedJson = JsonSerializer.Serialize(selected, GrimoireJsonContext.Default.StringArray);

        foreach ((string table, string key) in new[]
        {
            ("campaign_rollup_manifest_sources", "ManifestId || ':' || SessionId"),
            ("campaign_rollup_manifests", "ManifestId"),
        })
        {
            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT {key} FROM {table} WHERE ManifestId IN ({segments});";

            Add(command, "$selectedArtifacts", selectedJson);

            await using DbDataReader source = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(table + ":" + source.GetString(0));
            }
        }
    }

    internal static async Task<long> CountArtifactClosureAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        string[] rows = await ReadArtifactClosureRowKeysAsync(connection, transaction, kind, artifactId, cancellationToken).ConfigureAwait(false);

        return rows.LongLength;
    }

    internal static async Task<long> PurgeArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (kind is not (SensitiveArtifactKind.CampaignRollup or SensitiveArtifactKind.CampaignContribution))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        long deleted = 0;

        if (kind == SensitiveArtifactKind.CampaignContribution)
        {
            Guid[] dependents = await ReadIdsAsync(connection, transaction,
                "SELECT DISTINCT RollupArtifactId FROM campaign_rollup_source_membership WHERE ContributionArtifactId = $ownerId;",
                artifactId, cancellationToken).ConfigureAwait(false);

            foreach (Guid dependent in dependents)
            {
                deleted += await PurgeArtifactAsync(connection, transaction,
                    SensitiveArtifactKind.CampaignRollup, dependent, cancellationToken).ConfigureAwait(false);
            }
        }

        string state = kind == SensitiveArtifactKind.CampaignRollup
            ? "campaign_rollup_state"
            : "campaign_contribution_state";

        // Keep the monotonic revision and native sequence cursor as denial/allocator evidence. A
        // missing artifact forces refolding; its old cursor must never mean that its bytes still exist.
        _ = await ExecuteAsync(connection, transaction,
            $"UPDATE {state} SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1, UpdatedAtUtc = $now WHERE CurrentArtifactId = $ownerId;",
            artifactId, cancellationToken, kind: null).ConfigureAwait(false);

        CovenantArtifactPlanTally tally = await CovenantArtifactPlanRunner.RunAsync(
            connection, transaction, kind, CovenantIdentitySql.Key(artifactId), CovenantArtifactPlanMode.Delete,
            cancellationToken).ConfigureAwait(false);

        deleted += tally.Targets.Sum(static target => target.Rows);

        // Content and source/checkpoint projections have gone before their sensitivity evidence.
        deleted += await ExecuteAsync(connection, transaction,
            "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND "
            + CovenantIdentitySql.Keyed("ArtifactId", "$artifactKey") + ";",
            artifactId, cancellationToken, kind).ConfigureAwait(false);

        return deleted;
    }

    internal static async Task<long> ClearSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        long deleted = 0;

        Guid[] contributions = await ReadIdsAsync(connection, transaction,
            "SELECT ArtifactId FROM campaign_contribution_artifacts WHERE SessionId = $ownerId;",
            sessionId, cancellationToken).ConfigureAwait(false);

        foreach (Guid contribution in contributions)
        {
            deleted += await PurgeArtifactAsync((SqliteConnection)connection, (SqliteTransaction)transaction,
                SensitiveArtifactKind.CampaignContribution, contribution, cancellationToken).ConfigureAwait(false);
        }

        deleted += await ExecuteAsync(connection, transaction,
            "DELETE FROM campaign_maintenance_checkpoints WHERE SessionId = $ownerId OR SourceSessionId = $ownerId;",
            sessionId, cancellationToken).ConfigureAwait(false);

        _ = await ExecuteAsync(connection, transaction,
            "UPDATE campaign_contribution_state SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1, UpdatedAtUtc = $now WHERE SessionId = $ownerId;",
            sessionId, cancellationToken, kind: null).ConfigureAwait(false);

        return deleted;
    }

    internal static async Task<long> ClearCampaignAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid? campaignId,
        bool removeOwner,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        long deleted = 0;

        foreach ((SensitiveArtifactKind kind, string table) in new[]
        {
            (SensitiveArtifactKind.CampaignContribution, "campaign_contribution_artifacts"),
            (SensitiveArtifactKind.CampaignRollup, "campaign_rollup_artifacts"),
        })
        {
            Guid[] artifacts = await ReadIdsAsync(connection, transaction,
                campaignId is null
                    ? $"SELECT ArtifactId FROM {table};"
                    : $"SELECT ArtifactId FROM {table} WHERE CampaignId = $ownerId;",
                campaignId, cancellationToken).ConfigureAwait(false);

            foreach (Guid artifact in artifacts)
            {
                deleted += await PurgeArtifactAsync((SqliteConnection)connection, (SqliteTransaction)transaction,
                    kind, artifact, cancellationToken).ConfigureAwait(false);
            }
        }

        string selection = campaignId is null ? "" : " WHERE CampaignId = $ownerId";

        deleted += await ExecuteAsync(connection, transaction,
            $"DELETE FROM campaign_maintenance_checkpoints{selection};", campaignId, cancellationToken).ConfigureAwait(false);

        foreach (string state in new[] { "campaign_contribution_state", "campaign_rollup_state" })
        {
            if (removeOwner)
            {
                deleted += await ExecuteAsync(connection, transaction,
                    $"DELETE FROM {state}{selection};", campaignId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await ExecuteAsync(connection, transaction,
                    $"UPDATE {state} SET CurrentArtifactId = NULL, RefoldRequired = 1, SourceGeneration = SourceGeneration + 1, UpdatedAtUtc = $now{selection};",
                    campaignId, cancellationToken, kind: null).ConfigureAwait(false);
            }
        }

        return deleted;
    }

    internal static async Task<bool> IsInstalledAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'campaign_rollup_artifacts');";

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<Guid[]> ReadIdsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        Guid? ownerId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "$ownerId", ownerId is { } owner ? Format(owner) : DBNull.Value);

        List<Guid> artifacts = [];

        await CampaignRollupSourceStorage.ResolveMembershipAsync(command, cancellationToken).ConfigureAwait(false);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(Guid.Parse(reader.GetString(0)));
        }

        return [.. artifacts];
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Guid? ownerId,
        CancellationToken cancellationToken,
        SensitiveArtifactKind? kind = null)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        Add(command, "$ownerId", ownerId is { } owner ? Format(owner) : DBNull.Value);

        Add(command, "$artifactKey", ownerId is { } artifact ? CovenantIdentitySql.Key(artifact) : DBNull.Value);

        Add(command, "$kind", kind is { } value ? (long)value : DBNull.Value);

        _ = UtcInstantSql.AddParameter(command, "$now", DateTimeOffset.UtcNow);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();
}

internal sealed record CampaignSummaryClosureSnapshot(long Rows, string Authority);
