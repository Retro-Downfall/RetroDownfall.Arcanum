using System.Collections.Immutable;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Immutable bounded Campaign publications with exact source and transactional CAS evidence.</summary>
internal sealed partial class CampaignRollupStore(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer) : ICampaignRollupStore
{
    private const int MetadataPageSize = 128;

    private static readonly GenerationProvenance CleanProvenance = GenerationProvenance.CreateExact([]);

    private static readonly CovenantDigest CleanSensitivityDigest = CovenantDigests.Sensitivity(
        CleanProvenance.ToDigestInput(ContentSensitivity.None));

    public Task<Result<CampaignRollupArtifact?>> ReadCurrentAsync(Guid campaignId,
        ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
        WithinAsync(false, async (connection, transaction) =>
        {
            await RequireCampaignAsync(connection, transaction, campaignId, cancellationToken).ConfigureAwait(false);

            State? state = await ReadStateAsync(connection, transaction, campaignId, contribution: false, cancellationToken).ConfigureAwait(false);

            return state?.ArtifactId is { } artifactId && !state.RefoldRequired
                ? await ReadArtifactAsync(connection, transaction, artifactId, contribution: false, authority, cancellationToken).ConfigureAwait(false)
                : null;
        }, cancellationToken);

    public Task<Result<CampaignContributionInput?>> PrepareContributionAsync(Guid sessionId, long? throughSequence,
        ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
        WithinAsync(true, (connection, transaction) => PrepareContributionWithinAsync(
            connection, transaction, sessionId, throughSequence, authority, cancellationToken), cancellationToken);

    public Task<Result<CampaignRollupInput?>> PrepareRollupAsync(Guid campaignId,
        ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
        WithinAsync(true, (connection, transaction) => PrepareRollupWithinAsync(
            connection, transaction, campaignId, authority, cancellationToken), cancellationToken);

    public Task<Result<CampaignRollupArtifact>> PublishContributionAsync(CampaignContributionInput input, string content,
        CampaignMaintenancePublication? publication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
        WithinAsync(true, async (connection, transaction) =>
        {
            RequireOutput(content);

            RequireContributionShape(input);

            CampaignContributionInput current = await PrepareContributionWithinAsync(connection, transaction,
                input.SessionId, input.SummarizedThroughSequence, authority, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(Stale("This Session no longer has the prepared contribution page."));

            if (current.CampaignId != input.CampaignId || current.ExpectedRevision != input.ExpectedRevision
                || current.SourceGeneration != input.SourceGeneration || current.SourceManifestDigest != input.SourceManifestDigest
                || ContributionManifest(input) != current.SourceManifestDigest)
            {
                throw Refuse(Stale("The prepared Session contribution no longer matches its exact source snapshot."));
            }

            await ValidatePublicationAsync(connection, transaction, publication, authority, current.CampaignId,
                current.SessionId, current.ExpectedRevision, current.SourceGeneration, current.SourceManifestDigest,
                current.Sensitivity, cancellationToken).ConfigureAwait(false);

            CampaignRollupArtifact output = NewArtifact(current.CampaignId, current.SessionId, current.ExpectedRevision + 1,
                content, current.Sensitivity, current.Provenance, current.SourceGeneration, current.SourceManifestDigest,
                current.SummarizedThroughSequence, 0);

            await InsertArtifactAsync(connection, transaction, output, publication, cancellationToken).ConfigureAwait(false);

            await LabelAsync(connection, transaction, output, publication, cancellationToken).ConfigureAwait(false);

            int moved = await ExecuteAsync(connection, transaction,
                """
                UPDATE campaign_contribution_state
                SET CurrentArtifactId = $artifact, Revision = $next, SummarizedThroughSequence = $through,
                    RefoldRequired = 0, UpdatedAtUtc = $now
                WHERE SessionId = $owner AND CampaignId = $campaign AND Revision = $expected AND SourceGeneration = $generation;
                """, cancellationToken,
                ("$artifact", Format(output.ArtifactId)), ("$next", output.Revision), ("$through", output.SummarizedThroughSequence),
                ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow)), ("$owner", Format(output.SessionId!.Value)),
                ("$campaign", Format(output.CampaignId)), ("$expected", input.ExpectedRevision), ("$generation", input.SourceGeneration)).ConfigureAwait(false);

            if (moved != 1)
            {
                throw Refuse(Stale("The Session contribution pointer changed before publication."));
            }

            await CommitPublicationAsync(connection, transaction, publication, output, cancellationToken).ConfigureAwait(false);

            await RevalidateAsync(authority, output.CampaignId, output.Sensitivity, cancellationToken).ConfigureAwait(false);

            return output;
        }, cancellationToken);

    public Task<Result<CampaignRollupArtifact>> PublishRollupAsync(CampaignRollupInput input, string content,
        CampaignMaintenancePublication? publication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
        WithinAsync(true, async (connection, transaction) =>
        {
            RequireOutput(content);

            RequireRollupShape(input);

            CampaignRollupInput current = await PrepareRollupWithinAsync(connection, transaction,
                input.CampaignId, authority, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(Stale("This Campaign no longer has the prepared fold page."));

            if (current.ExpectedRevision != input.ExpectedRevision || current.SourceGeneration != input.SourceGeneration
                || current.SourceManifestDigest != input.SourceManifestDigest || RollupInputManifest(input) != current.SourceManifestDigest)
            {
                throw Refuse(Stale("The prepared Campaign fold no longer matches its exact source snapshot."));
            }

            await ValidatePublicationAsync(connection, transaction, publication, authority, current.CampaignId,
                sourceSessionId: null, current.ExpectedRevision, current.SourceGeneration, current.SourceManifestDigest,
                current.Sensitivity, cancellationToken).ConfigureAwait(false);

            (CovenantDigest manifest, long count) = await FullSourceManifestAsync(connection, transaction,
                current.CampaignId, current.Previous?.ArtifactId, current.Contributions, cancellationToken).ConfigureAwait(false);

            CampaignRollupArtifact output = NewArtifact(current.CampaignId, null, current.ExpectedRevision + 1,
                content, current.Sensitivity, current.Provenance, current.SourceGeneration, manifest, 0, count);

            await InsertArtifactAsync(connection, transaction, output, publication, cancellationToken).ConfigureAwait(false);

            if (current.Previous is { } previous)
            {
                _ = await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO campaign_rollup_sources (RollupArtifactId, SessionId, CampaignId, RollupRevision,
                        ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence)
                    SELECT $artifact, SessionId, CampaignId, $revision, ContributionArtifactId, ContributionRevision,
                        ContentDigest, SensitivityDigest, SummarizedThroughSequence
                    FROM campaign_rollup_sources WHERE RollupArtifactId = $previous;
                    """, cancellationToken, ("$artifact", Format(output.ArtifactId)), ("$revision", output.Revision),
                    ("$previous", Format(previous.ArtifactId))).ConfigureAwait(false);
            }

            foreach (CampaignRollupArtifact contribution in current.Contributions)
            {
                _ = await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO campaign_rollup_sources (RollupArtifactId, SessionId, CampaignId, RollupRevision,
                        ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence)
                    VALUES ($artifact, $session, $campaign, $revision, $contribution, $contributionRevision,
                        $contentDigest, $sensitivityDigest, $through);
                    """, cancellationToken, ("$artifact", Format(output.ArtifactId)), ("$session", Format(contribution.SessionId!.Value)),
                    ("$campaign", Format(output.CampaignId)), ("$revision", output.Revision), ("$contribution", Format(contribution.ArtifactId)),
                    ("$contributionRevision", contribution.Revision), ("$contentDigest", contribution.ContentDigest.Bytes),
                    ("$sensitivityDigest", contribution.SensitivityDigest.Bytes), ("$through", contribution.SummarizedThroughSequence)).ConfigureAwait(false);
            }

            await LabelAsync(connection, transaction, output, publication, cancellationToken).ConfigureAwait(false);

            int moved = await ExecuteAsync(connection, transaction,
                """
                UPDATE campaign_rollup_state
                SET CurrentArtifactId = $artifact, Revision = $next, RefoldRequired = 0,
                    LastFoldedSessionId = $last, UpdatedAtUtc = $now
                WHERE CampaignId = $campaign AND Revision = $expected AND SourceGeneration = $generation;
                """, cancellationToken, ("$artifact", Format(output.ArtifactId)), ("$next", output.Revision),
                ("$last", current.LastFoldedSessionId is { } last ? Format(last) : null), ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow)),
                ("$campaign", Format(output.CampaignId)), ("$expected", input.ExpectedRevision), ("$generation", input.SourceGeneration)).ConfigureAwait(false);

            if (moved != 1)
            {
                throw Refuse(Stale("The Campaign pointer changed before publication."));
            }

            await CommitPublicationAsync(connection, transaction, publication, output, cancellationToken).ConfigureAwait(false);

            await RevalidateAsync(authority, output.CampaignId, output.Sensitivity, cancellationToken).ConfigureAwait(false);

            return output;
        }, cancellationToken);

    public async Task<Result> ValidateAsync(CampaignRollupArtifact artifact,
        ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
    {
        Result<bool> result = await WithinAsync(false, async (connection, transaction) =>
        {
            RequireArtifactShape(artifact);

            CampaignRollupArtifact current = await ReadArtifactAsync(connection, transaction, artifact.ArtifactId,
                artifact.SessionId.HasValue, authority, cancellationToken).ConfigureAwait(false);

            if (ArtifactIdentityDigest(current) != ArtifactIdentityDigest(artifact))
            {
                throw Refuse(Stale("The exact bound Campaign artifact no longer matches its immutable evidence."));
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public Task<Result<CampaignRollupStatus>> ReadStatusAsync(Guid campaignId, CancellationToken cancellationToken) =>
        WithinAsync(false, async (connection, transaction) =>
        {
            await RequireCampaignAsync(connection, transaction, campaignId, cancellationToken).ConfigureAwait(false);

            State? state = await ReadStateAsync(connection, transaction, campaignId, false, cancellationToken).ConfigureAwait(false);

            Metadata? metadata = state?.ArtifactId is { } artifactId
                ? await ReadMetadataAsync(connection, transaction, artifactId, false, cancellationToken).ConfigureAwait(false)
                : null;

            return new CampaignRollupStatus(campaignId, state?.ArtifactId, state?.Revision ?? 0, state?.SourceGeneration ?? 0,
                metadata?.SourceCount ?? 0, state?.RefoldRequired ?? false, metadata?.Sensitivity ?? ContentSensitivity.None, state?.UpdatedAtUtc);
        }, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindPendingContributionsAsync(DateTimeOffset idleCutoffUtc,
        int pageSize, CancellationToken cancellationToken) =>
        FindPendingContributionsWithinAsync(null, idleCutoffUtc, pageSize, cancellationToken);

    public Task<IReadOnlyList<Guid>> FindPendingContributionsForCampaignAsync(Guid campaignId,
        DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken) =>
        FindPendingContributionsWithinAsync(campaignId, idleCutoffUtc, pageSize, cancellationToken);

    private async Task<IReadOnlyList<Guid>> FindPendingContributionsWithinAsync(Guid? campaignId,
        DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        bool hasTransferIntents = await HasTransferIntentTableAsync(connection, null, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT s.Id FROM "Sessions" s
            JOIN session_campaign_bindings b ON b.SessionId = s.Id AND b.BindingKindCode = 2
            JOIN "Campaigns" c ON c.Id = b.CampaignId
            LEFT JOIN campaign_contribution_state state ON state.SessionId = s.Id
            LEFT JOIN campaign_fork_frontiers f ON f.SessionId = s.Id
            WHERE s.UpdatedAt <= $cutoff
                AND ($campaign IS NULL OR b.CampaignId = $campaign)
                AND (s.ForkedFromSessionId IS NULL OR f.SessionId IS NOT NULL)
                AND (f.SessionId IS NOT NULL OR NOT ({UnprovenImportedHistorySql(hasTransferIntents, "s.Id")}))
                AND NOT EXISTS (SELECT 1 FROM session_turn_claims claim WHERE claim.SessionId = s.Id AND claim.StateCode IN (1, 2))
                AND EXISTS (SELECT 1 FROM "Entries" e WHERE e.SessionId = s.Id
                    AND e.Sequence > CASE WHEN state.CurrentArtifactId IS NULL OR state.RefoldRequired = 1
                        THEN COALESCE(f.InheritedThroughSequence, 0) ELSE state.SummarizedThroughSequence END
                    AND (e.Role = 1 OR (e.Role = 2 AND EXISTS (SELECT 1 FROM assistant_entry_finalizations terminal
                        WHERE terminal.AssistantEntryId = e.Id AND terminal.OutcomeCode IN (1, 3, 4)))))
            ORDER BY s.Id LIMIT $limit;
            """;

        _ = command.Parameters.AddWithValue("$campaign", campaignId is { } campaign ? Format(campaign) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$cutoff", UtcInstantText.Format(idleCutoffUtc));

        _ = command.Parameters.AddWithValue("$limit", Math.Clamp(pageSize, 1, MetadataPageSize));

        List<Guid> sessions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(Guid.Parse(reader.GetString(0)));
        }

        return sessions;
    }

    private async Task<Result<T>> WithinAsync<T>(bool write,
        Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializer);

        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: !write);

        try
        {
            T value = await action(connection, transaction).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<T>.Success(value);
        }
        catch (ReadRefusal refusal)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return refusal.Error;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return new Error(ErrorCodes.Covenant.IntegrityFailure, "Campaign source evidence cannot be reconstructed exactly.");
        }
        catch (SqliteException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return new Error(ErrorCodes.Grimoire.WriteFailed, $"Campaign summary storage could not complete ({exception.SqliteErrorCode}).");
        }
    }

    private static void RequireOutput(string content)
    {
        Result validated = CampaignSummaryPolicy.Validate(content);

        if (validated.IsFailure)
        {
            throw Refuse(validated.Error);
        }
    }

    private static CampaignRollupArtifact NewArtifact(Guid campaignId, Guid? sessionId, long revision, string content,
        ContentSensitivity sensitivity, GenerationProvenance provenance, long sourceGeneration, CovenantDigest manifest,
        long through, long count) => new(Guid.NewGuid(), campaignId, sessionId, checked(revision), content,
            DerivedArtifactContentDigest.ForText(content), sensitivity, provenance,
            CovenantDigests.Sensitivity(provenance.ToDigestInput(sensitivity)), sourceGeneration, manifest, through, count);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Error Stale(string message) => new(ErrorCodes.Covenant.StaleSnapshot, message);

    private static ReadRefusal Refuse(Error error) => new(error);

    private sealed class ReadRefusal(Error error) : Exception(error.Message)
    {
        internal Error Error { get; } = error;
    }
}
