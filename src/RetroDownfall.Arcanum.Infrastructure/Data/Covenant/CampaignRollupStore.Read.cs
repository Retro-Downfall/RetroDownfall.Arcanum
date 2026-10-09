using System.Collections.Immutable;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class CampaignRollupStore
{
    private async Task<CampaignContributionInput?> PrepareContributionWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sessionId, long? upper, ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        Guid? campaign = await ReadCanonicalCampaignAsync(connection, transaction, sessionId, ct).ConfigureAwait(false);

        if (campaign is not { } campaignId)
        {
            return null;
        }

        await EnsureStateAsync(connection, transaction, campaignId, null, ct).ConfigureAwait(false);

        await EnsureStateAsync(connection, transaction, campaignId, sessionId, ct).ConfigureAwait(false);

        State state = (await ReadStateAsync(connection, transaction, sessionId, true, ct).ConfigureAwait(false))!;

        if (upper is null && await HasActiveClaimAsync(connection, transaction, sessionId, ct).ConfigureAwait(false))
        {
            return null;
        }

        long inherited = await ReadInheritedFrontierAsync(connection, transaction, sessionId, ct).ConfigureAwait(false);

        CampaignRollupArtifact? previous = state.ArtifactId is { } previousId && !state.RefoldRequired
            ? await ReadArtifactAsync(connection, transaction, previousId, true, authority, ct).ConfigureAwait(false)
            : null;

        long after = previous is null ? inherited : Math.Max(inherited, state.ThroughSequence);

        List<EntryMetadata> page = await ReadEntryMetadataPageAsync(connection, transaction, sessionId, after, upper, ct).ConfigureAwait(false);

        if (page.Count == 0)
        {
            return null;
        }

        int available = CampaignRollupLimits.InputPageUtf8Bytes - Encoding.UTF8.GetByteCount(previous?.Content ?? string.Empty);

        ImmutableArray<CampaignContributionEntry>.Builder entries = ImmutableArray.CreateBuilder<CampaignContributionEntry>();

        long through = after;

        bool hasMore = page.Count > MetadataPageSize;

        foreach (EntryMetadata metadata in page.Take(MetadataPageSize))
        {
            if (metadata.ContentBytes > available)
            {
                if (entries.Count == 0)
                {
                    throw Refuse(new Error("campaign_rollup.input_too_large", "A native Entry exceeds the bounded Campaign contribution page."));
                }

                hasMore = true;

                break;
            }

            ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, transaction,
                SensitiveArtifactKind.AssistantEntry, metadata.EntryId, ct).ConfigureAwait(false);

            ContentSensitivity sensitivity = label?.Sensitivity ?? ContentSensitivity.None;

            GenerationProvenance provenance = label?.Provenance ?? CleanProvenance;

            CovenantDigest sensitivityDigest = label?.SensitivityDigest ?? CleanSensitivityDigest;

            if (label is { } exact && (exact.SessionId != sessionId
                || (exact.CampaignId is { } labelledCampaign && labelledCampaign != campaignId)))
            {
                throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "An Entry label names a different canonical owner."));
            }

            if (metadata.Role == 2 && (metadata.FinalSensitivity != sensitivity || metadata.FinalSensitivityDigest != sensitivityDigest))
            {
                throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "An assistant Entry's finalization and sensitivity label disagree."));
            }

            await RevalidateAsync(authority, campaignId, sensitivity, ct).ConfigureAwait(false);

            string content = await ReadTextAsync(connection, transaction, "Entries", "Id", metadata.EntryId, metadata.ContentBytes, ct).ConfigureAwait(false);

            CovenantDigest contentDigest = DerivedArtifactContentDigest.ForText(content);

            if (label is not null && label.ArtifactContentDigest != contentDigest)
            {
                throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "An Entry's protected bytes do not match its exact label."));
            }

            entries.Add(new CampaignContributionEntry(metadata.EntryId, metadata.Sequence, metadata.Role, content,
                metadata.CreatedAtUtc, contentDigest, sensitivityDigest, sensitivity, provenance));

            available -= metadata.ContentBytes;

            through = metadata.Sequence;
        }

        CampaignContributionInput input = new(sessionId, campaignId, state.Revision, state.SourceGeneration,
            inherited, through, previous, entries.ToImmutable(), default, hasMore);

        await RevalidateAsync(authority, campaignId, input.Sensitivity, ct).ConfigureAwait(false);

        return input with { SourceManifestDigest = ContributionManifest(input) };
    }

    private async Task<CampaignRollupInput?> PrepareRollupWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid campaignId, ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        await RequireCampaignAsync(connection, transaction, campaignId, ct).ConfigureAwait(false);

        await EnsureStateAsync(connection, transaction, campaignId, null, ct).ConfigureAwait(false);

        State state = (await ReadStateAsync(connection, transaction, campaignId, false, ct).ConfigureAwait(false))!;

        CampaignRollupArtifact? previous = state.ArtifactId is { } previousId && !state.RefoldRequired
            ? await ReadArtifactAsync(connection, transaction, previousId, false, authority, ct).ConfigureAwait(false)
            : null;

        List<Guid> pending = [];

        await using (SqliteCommand command = Command(connection, transaction,
            """
            SELECT s.CurrentArtifactId
            FROM campaign_contribution_state s
            WHERE s.CampaignId = $campaign AND s.CurrentArtifactId IS NOT NULL AND s.RefoldRequired = 0
                AND ($previous IS NULL OR NOT EXISTS (SELECT 1 FROM campaign_rollup_sources consumed
                    WHERE consumed.RollupArtifactId = $previous AND consumed.SessionId = s.SessionId))
            ORDER BY s.SessionId LIMIT $limit;
            """, ("$campaign", Format(campaignId)), ("$previous", previous is null ? null : Format(previous.ArtifactId)),
            ("$limit", MetadataPageSize + 1)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                pending.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (pending.Count == 0)
        {
            return null;
        }

        int available = CampaignRollupLimits.InputPageUtf8Bytes - Encoding.UTF8.GetByteCount(previous?.Content ?? string.Empty);

        ImmutableArray<CampaignRollupArtifact>.Builder contributions = ImmutableArray.CreateBuilder<CampaignRollupArtifact>();

        bool hasMore = pending.Count > MetadataPageSize;

        foreach (Guid artifactId in pending.Take(MetadataPageSize))
        {
            Metadata metadata = await ReadMetadataAsync(connection, transaction, artifactId, true, ct).ConfigureAwait(false)
                ?? throw Refuse(Stale("A Campaign contributor disappeared before its bounded fold read."));

            if (metadata.ContentBytes > available)
            {
                hasMore = true;

                break;
            }

            CampaignRollupArtifact artifact = await ReadArtifactAsync(connection, transaction, artifactId, true, authority, ct).ConfigureAwait(false);

            contributions.Add(artifact);

            available -= metadata.ContentBytes;
        }

        if (contributions.Count == 0)
        {
            throw Refuse(new Error("campaign_rollup.input_too_large", "A Campaign contribution exceeds its bounded fold page."));
        }

        CampaignRollupInput input = new(campaignId, state.Revision, state.SourceGeneration, previous,
            contributions.ToImmutable(), default, contributions[^1].SessionId, hasMore);

        await RevalidateAsync(authority, campaignId, input.Sensitivity, ct).ConfigureAwait(false);

        return input with { SourceManifestDigest = RollupInputManifest(input) };
    }

    private async Task<CampaignRollupArtifact> ReadArtifactAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid artifactId, bool contribution, ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        Metadata metadata = await ReadMetadataAsync(connection, transaction, artifactId, contribution, ct).ConfigureAwait(false)
            ?? throw Refuse(Stale("The bound Campaign summary revision no longer exists."));

        await RequireCampaignAsync(connection, transaction, metadata.CampaignId, ct).ConfigureAwait(false);

        await RevalidateAsync(authority, metadata.CampaignId, metadata.Sensitivity, ct).ConfigureAwait(false);

        ArtifactSensitivityLabel? label = await VerifyArtifactLabelAsync(connection, transaction, metadata, ct).ConfigureAwait(false);

        GenerationProvenance provenance = label?.Provenance ?? CleanProvenance;

        if (contribution)
        {
            Guid? canonical = await ReadCanonicalCampaignAsync(connection, transaction, metadata.SessionId!.Value, ct).ConfigureAwait(false);

            State? state = await ReadStateAsync(connection, transaction, metadata.SessionId.Value, true, ct).ConfigureAwait(false);

            if (canonical != metadata.CampaignId || state?.ArtifactId != artifactId || state.RefoldRequired
                || state.Revision != metadata.Revision || state.ThroughSequence != metadata.ThroughSequence)
            {
                throw Refuse(Stale("The Campaign contribution is no longer the Session's current exact source."));
            }
        }
        else
        {
            await VerifySourcesAsync(connection, transaction, metadata, provenance, authority, ct).ConfigureAwait(false);
        }

        string content = await ReadTextAsync(connection, transaction,
            contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts", "ArtifactId",
            artifactId, metadata.ContentBytes, ct).ConfigureAwait(false);

        if (DerivedArtifactContentDigest.ForText(content) != metadata.ContentDigest)
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "Campaign summary bytes do not match their immutable digest."));
        }

        await RevalidateAsync(authority, metadata.CampaignId, metadata.Sensitivity, ct).ConfigureAwait(false);

        return metadata.Artifact(content, provenance);
    }

    private static async Task<ArtifactSensitivityLabel?> VerifyArtifactLabelAsync(SqliteConnection connection,
        SqliteTransaction transaction, Metadata metadata, CancellationToken ct)
    {
        ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, transaction,
            metadata.SessionId.HasValue ? SensitiveArtifactKind.CampaignContribution : SensitiveArtifactKind.CampaignRollup,
            metadata.ArtifactId, ct).ConfigureAwait(false);

        if (metadata.Sensitivity == ContentSensitivity.None)
        {
            if (label is not null || metadata.SensitivityDigest != CleanSensitivityDigest)
            {
                throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "A clean Campaign artifact has contradictory sensitivity evidence."));
            }
        }
        else if (label is null || label.SessionId != metadata.SessionId || label.CampaignId != metadata.CampaignId
            || label.ArtifactRevision != (ulong)metadata.Revision || label.ArtifactContentDigest != metadata.ContentDigest
            || label.Sensitivity != metadata.Sensitivity || label.SensitivityDigest != metadata.SensitivityDigest)
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "A protected Campaign artifact lacks its exact revision-bound label."));
        }

        return label;
    }

    private static async Task<ArtifactSensitivityLabel?> ReadVerifiedLabelAsync(SqliteConnection connection,
        SqliteTransaction transaction, SensitiveArtifactKind kind, Guid artifactId, CancellationToken ct)
    {
        // ArtifactId is outside the governed identity family. A foreign spelling cannot
        // prove absence through the ledger's exact native-key lookup.
        await using (SqliteCommand presence = Command(connection, transaction,
            $"SELECT ArtifactId FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND {CovenantIdentitySql.Keyed("ArtifactId", "$key")} LIMIT 2;",
            ("$kind", (int)kind), ("$key", CovenantIdentitySql.Key(artifactId))))
        {
            await using SqliteDataReader labels = await presence.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await labels.ReadAsync(ct).ConfigureAwait(false)
                && (labels.GetString(0) != Format(artifactId) || await labels.ReadAsync(ct).ConfigureAwait(false)))
            {
                throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure,
                    "A Campaign source has ambiguous or noncanonical sensitivity label identity evidence."));
            }
        }

        Result<ArtifactSensitivityLabel?> read = await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            connection, transaction, kind, artifactId, ct).ConfigureAwait(false);

        if (read.IsFailure)
        {
            throw Refuse(read.Error);
        }

        if (read.Value is not { } label)
        {
            return null;
        }

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT SensitivityDigest, ArtifactLabelDigest FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;",
            ("$kind", (int)kind), ("$artifact", Format(artifactId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)
            || new CovenantDigest((byte[])reader.GetValue(0)) != label.SensitivityDigest
            || new CovenantDigest((byte[])reader.GetValue(1)) != label.LabelDigest)
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "A Campaign source sensitivity label cannot reproduce its stored digest."));
        }

        return label;
    }

    private static async Task RevalidateAsync(ICovenantSnapshotReadLease? authority, Guid campaignId,
        ContentSensitivity sensitivity, CancellationToken ct)
    {
        if (sensitivity == ContentSensitivity.None)
        {
            return;
        }

        if (authority is null)
        {
            throw Refuse(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Protected Campaign context requires an exact Campaign read capability."));
        }

        Result validated = await authority.RevalidateAsync(ct).ConfigureAwait(false);

        if (validated.IsFailure)
        {
            throw Refuse(validated.Error);
        }

        CovenantOperationLeaseSnapshot snapshot = authority.Snapshot;

        if (snapshot.Coverage != CovenantLeaseCoverage.Installation
            && (snapshot.Scope is not { } scope || scope.Kind != CovenantScope.Campaign || scope.CampaignId != campaignId))
        {
            throw Refuse(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "The read capability does not cover this Campaign."));
        }

        CovenantProcessResidence.MarkOpened();
    }

    private static async Task<Guid?> ReadCanonicalCampaignAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sessionId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            """
            SELECT b.BindingKindCode, b.CampaignId, c.Id
            FROM "Sessions" s LEFT JOIN session_campaign_bindings b ON b.SessionId = s.Id
            LEFT JOIN "Campaigns" c ON c.Id = b.CampaignId WHERE s.Id = $session;
            """, ("$session", Format(sessionId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw Refuse(Stale("The Session no longer exists."));
        }

        if (reader.IsDBNull(0) || reader.GetInt32(0) == 3)
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "The Session has no resolved canonical Campaign binding."));
        }

        if (reader.GetInt32(0) == 1)
        {
            return null;
        }

        if (reader.GetInt32(0) != 2 || reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "The canonical Campaign binding names an unavailable Campaign."));
        }

        return Guid.Parse(reader.GetString(1));
    }

    private static async Task RequireCampaignAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid campaignId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM \"Campaigns\" WHERE Id = $campaign);", ("$campaign", Format(campaignId)));

        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw Refuse(Stale("The Campaign no longer exists."));
        }
    }

    private static async Task EnsureStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid campaignId, Guid? sessionId, CancellationToken ct)
    {
        string sql = sessionId.HasValue
            ? "INSERT INTO campaign_contribution_state (SessionId, CampaignId, UpdatedAtUtc) VALUES ($owner, $campaign, $now) ON CONFLICT(SessionId) DO NOTHING;"
            : "INSERT INTO campaign_rollup_state (CampaignId, UpdatedAtUtc) VALUES ($campaign, $now) ON CONFLICT(CampaignId) DO NOTHING;";

        _ = await ExecuteAsync(connection, transaction, sql, ct, ("$owner", sessionId is { } owner ? Format(owner) : null),
            ("$campaign", Format(campaignId)), ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow))).ConfigureAwait(false);
    }

    private static async Task<State?> ReadStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid owner, bool contribution, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            contribution
                ? "SELECT CurrentArtifactId, Revision, SourceGeneration, RefoldRequired, SummarizedThroughSequence, UpdatedAtUtc FROM campaign_contribution_state WHERE SessionId = $owner;"
                : "SELECT CurrentArtifactId, Revision, SourceGeneration, RefoldRequired, 0, UpdatedAtUtc FROM campaign_rollup_state WHERE CampaignId = $owner;",
            ("$owner", Format(owner)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new State(reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt32(3) == 1, reader.GetInt64(4), UtcInstantText.Parse(reader.GetString(5)))
            : null;
    }

    private static async Task<Metadata?> ReadMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid artifactId, bool contribution, CancellationToken ct)
    {
        string table = contribution ? "campaign_contribution_artifacts" : "campaign_rollup_artifacts";

        string session = contribution ? "SessionId" : "NULL";

        string through = contribution ? "SummarizedThroughSequence" : "0";

        string count = contribution ? "0" : "SourceCount";

        await using SqliteCommand command = Command(connection, transaction,
            $"SELECT ArtifactId, CampaignId, {session}, Revision, ContentDigest, SensitivityCode, SensitivityDigest, SourceGeneration, SourceManifestDigest, {through}, {count}, length(CAST(Content AS BLOB)) FROM {table} WHERE ArtifactId = $artifact;",
            ("$artifact", Format(artifactId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        Metadata metadata = new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetInt64(3),
            new CovenantDigest((byte[])reader.GetValue(4)), (ContentSensitivity)reader.GetInt32(5),
            new CovenantDigest((byte[])reader.GetValue(6)), reader.GetInt64(7), new CovenantDigest((byte[])reader.GetValue(8)),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt32(11));

        if (metadata.ContentBytes is < 0 or > CampaignRollupLimits.SummaryUtf8Bytes
            || metadata.Sensitivity is not (ContentSensitivity.None or ContentSensitivity.CovenantDerived))
        {
            throw Refuse(new Error(ErrorCodes.Covenant.IntegrityFailure, "Campaign artifact metadata exceeds its closed allocation or sensitivity domain."));
        }

        return metadata;
    }

    private static async Task<List<EntryMetadata>> ReadEntryMetadataPageAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sessionId, long after, long? upper, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            """
            SELECT e.Id, e.Sequence, e.Role, e.CreatedAt, length(CAST(e.Content AS BLOB)),
                terminal.ContentSensitivityCode, terminal.ContentSensitivityDigest
            FROM "Entries" e LEFT JOIN assistant_entry_finalizations terminal ON terminal.AssistantEntryId = e.Id
            WHERE e.SessionId = $session AND e.Sequence > $after AND ($upper IS NULL OR e.Sequence <= $upper)
                AND (e.Role = 1 OR (e.Role = 2 AND terminal.OutcomeCode IN (1, 3, 4)))
            ORDER BY e.Sequence LIMIT $limit;
            """, ("$session", Format(sessionId)), ("$after", after), ("$upper", upper), ("$limit", MetadataPageSize + 1));

        List<EntryMetadata> page = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long length = reader.GetInt64(4);

            page.Add(new EntryMetadata(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetInt32(2),
                UtcInstantText.Parse(reader.GetString(3)), length > int.MaxValue ? int.MaxValue : checked((int)length),
                reader.IsDBNull(5) ? null : (ContentSensitivity)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : new CovenantDigest((byte[])reader.GetValue(6))));
        }

        return page;
    }

    private static async Task<long> ReadInheritedFrontierAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sessionId, CancellationToken ct)
    {
        bool hasTransferIntents = await HasTransferIntentTableAsync(connection, transaction, ct).ConfigureAwait(false);

        await using SqliteCommand command = Command(connection, transaction,
            $"""
            SELECT s.ForkedFromSessionId, f.InheritedThroughSequence, ({UnprovenImportedHistorySql(hasTransferIntents, "s.Id")})
            FROM "Sessions" s LEFT JOIN campaign_fork_frontiers f ON f.SessionId = s.Id WHERE s.Id = $session;
            """, ("$session", Format(sessionId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw Refuse(Stale("The contribution's Session disappeared."));
        }

        if (reader.IsDBNull(1) && (!reader.IsDBNull(0) || reader.GetBoolean(2)))
        {
            throw Refuse(Stale("This copied Session has no proven inherited-frontier evidence."));
        }

        return reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
    }

    private static string UnprovenImportedHistorySql(bool hasTransferIntents, string sessionColumn)
    {
        string copiedGuard = $"EXISTS(SELECT 1 FROM assistant_entry_finalizations copied WHERE copied.SessionId = {sessionColumn} AND copied.OutcomeCode IN (3, 4))";

        // Transfer journal identities are historical and outside the governed native-key family.
        return hasTransferIntents
            ? $"{copiedGuard} OR EXISTS(SELECT 1 FROM protected_session_transfer_intents transfer WHERE {CovenantIdentitySql.Keyed("transfer.DestinationSessionId", $"lower(replace({sessionColumn}, '-', ''))")} AND transfer.PhaseCode BETWEEN 3 AND 5)"
            : copiedGuard;
    }

    private static async Task<bool> HasTransferIntentTableAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'protected_session_transfer_intents');");

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasActiveClaimAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid sessionId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM session_turn_claims WHERE SessionId = $session AND StateCode IN (1, 2));",
            ("$session", Format(sessionId)));

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> ReadTextAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string key, Guid artifactId, int expectedBytes, CancellationToken ct)
    {
        // Every caller has already checked metadata, bounds and sensitivity authority in this snapshot.
        await using SqliteCommand command = Command(connection, transaction,
            $"SELECT Content FROM {table} WHERE {key} = $id AND length(CAST(Content AS BLOB)) = $bytes;",
            ("$id", Format(artifactId)), ("$bytes", expectedBytes));

        object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return value is string content ? content : throw Refuse(Stale("The bounded source changed before its exact payload read."));
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, parameters);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record State(Guid? ArtifactId, long Revision, long SourceGeneration, bool RefoldRequired,
        long ThroughSequence, DateTimeOffset UpdatedAtUtc);

    private sealed record EntryMetadata(Guid EntryId, long Sequence, int Role, DateTimeOffset CreatedAtUtc,
        int ContentBytes, ContentSensitivity? FinalSensitivity, CovenantDigest? FinalSensitivityDigest);

    private sealed record Metadata(Guid ArtifactId, Guid CampaignId, Guid? SessionId, long Revision,
        CovenantDigest ContentDigest, ContentSensitivity Sensitivity, CovenantDigest SensitivityDigest,
        long SourceGeneration, CovenantDigest SourceManifestDigest, long ThroughSequence, long SourceCount, int ContentBytes)
    {
        internal CampaignRollupArtifact Artifact(string content, GenerationProvenance provenance) => new(
            ArtifactId, CampaignId, SessionId, Revision, content, ContentDigest, Sensitivity, provenance,
            SensitivityDigest, SourceGeneration, SourceManifestDigest, ThroughSequence, SourceCount);
    }
}
