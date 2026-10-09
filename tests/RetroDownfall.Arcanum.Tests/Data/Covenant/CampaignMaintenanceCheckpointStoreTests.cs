using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CampaignMaintenanceCheckpointStoreTests
{
    private static readonly Guid CampaignId = Guid.NewGuid();

    private static readonly Guid SessionId = Guid.NewGuid();

    private static readonly Guid InstallationId = Guid.NewGuid();

    [Fact]
    public async Task Claim_renewal_and_checkpoint_writes_persist_fixed_width_UTC_instants()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceCheckpointStore store = Store(fixture);

        CampaignMaintenanceIdentity identity = await IdentityAsync(fixture, claim, 1);

        Assert.True((await store.RenewClaimAsync(claim, identity.Campaign, CancellationToken.None)).IsSuccess);

        await AssertCanonicalInstantsAsync(fixture, 2);

        Result<CampaignMaintenanceAttempt> prepared = await store.PrepareAttemptAsync(identity, Digest(7), CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        await AssertCanonicalInstantsAsync(fixture, 3);

        Result<CampaignMaintenanceAttempt> recorded = await store.RecordDisclosureAsync(prepared.Value, Digest(9), CancellationToken.None);

        Assert.True(recorded.IsSuccess, recorded.IsFailure ? recorded.Error.Message : null);

        await AssertCanonicalInstantsAsync(fixture, 3);
    }

    [Fact]
    public async Task An_uncertain_retry_and_a_second_page_receive_distinct_durable_physical_ordinals()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceCheckpointStore store = Store(fixture);

        CampaignMaintenanceIdentity firstPage = await IdentityAsync(fixture, claim, 1);

        CampaignMaintenanceAttempt first = (await store.PrepareAttemptAsync(firstPage, Digest(7), CancellationToken.None)).Value;

        CampaignMaintenanceAttempt retry = (await store.PrepareAttemptAsync(firstPage, Digest(7), CancellationToken.None)).Value;

        CampaignMaintenanceAttempt secondPage = (await store.PrepareAttemptAsync(await IdentityAsync(fixture, claim, 2), Digest(8), CancellationToken.None)).Value;

        Assert.Equal(1UL, first.PhysicalProviderAttemptOrdinal);

        Assert.Equal(2UL, retry.PhysicalProviderAttemptOrdinal);

        Assert.Equal(3UL, secondPage.PhysicalProviderAttemptOrdinal);

        Assert.True((await store.ValidateAttemptAsync(first, CancellationToken.None)).IsFailure);

        Assert.True((await store.ValidateAttemptAsync(retry, CancellationToken.None)).IsSuccess);

        Assert.Equal(2L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_maintenance_checkpoints;"));
    }

    [Fact]
    public async Task An_expired_claim_cannot_renew_or_prepare_another_attempt()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceIdentity identity = await IdentityAsync(fixture, claim, 1);

        await ExecuteAsync(fixture, "UPDATE session_turn_claims SET LeaseDeadlineUtc = '2000-01-01T00:00:00.0000000+00:00';");

        Assert.True((await Store(fixture).RenewClaimAsync(claim, identity.Campaign, CancellationToken.None)).IsFailure);

        Assert.True((await Store(fixture).PrepareAttemptAsync(identity, Digest(7), CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_maintenance_checkpoints;"));

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT InputSensitivityRevision FROM session_turn_claims;"));
    }

    [Fact]
    public async Task Only_a_proven_prior_boot_adoption_can_replace_a_preparing_executor()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimLease originalClaim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceCheckpointStore store = Store(fixture);

        CampaignMaintenanceAttempt original = (await store.PrepareAttemptAsync(await IdentityAsync(fixture, originalClaim, 1), Digest(7), CancellationToken.None)).Value;

        SessionTurnClaimLease adoptedClaim = await AcquireAsync(fixture, Guid.NewGuid());

        Assert.Equal(SessionTurnClaimDisposition.Adopted, adoptedClaim.Disposition);

        Assert.True((await store.ValidateAttemptAsync(original, CancellationToken.None)).IsFailure);

        CampaignMaintenanceAttempt adopted = (await store.PrepareAttemptAsync(await IdentityAsync(fixture, adoptedClaim, 1), Digest(7), CancellationToken.None)).Value;

        Assert.Equal(2UL, adopted.PhysicalProviderAttemptOrdinal);

        Assert.True((await store.ValidateAttemptAsync(adopted, CancellationToken.None)).IsSuccess);

        CampaignMaintenanceIdentity forged = adopted.Identity with
        {
            ClaimLease = adoptedClaim with { ExecutorId = Guid.NewGuid() },
        };

        Assert.True((await store.PrepareAttemptAsync(forged, Digest(7), CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Disclosure_recording_advances_the_prepared_checkpoint_and_preserves_the_claims_input_revision()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceCheckpointStore store = Store(fixture);

        CampaignMaintenanceAttempt attempt = (await store.PrepareAttemptAsync(await IdentityAsync(fixture, claim, 1), Digest(7), CancellationToken.None)).Value;

        Result<CampaignMaintenanceAttempt> recorded = await store.RecordDisclosureAsync(attempt, Digest(9), CancellationToken.None);

        Assert.True(recorded.IsSuccess, recorded.IsFailure ? recorded.Error.Message : null);

        Assert.Equal(attempt.CheckpointRevision + 1, recorded.Value.CheckpointRevision);

        Assert.True((await store.ValidateAttemptAsync(attempt, CancellationToken.None)).IsFailure);

        Assert.True((await store.ValidateAttemptAsync(recorded.Value, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT InputSensitivityRevision FROM session_turn_claims;"));

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT CompletedStepMask FROM session_turn_claims;"));
    }

    [Fact]
    public async Task An_acknowledged_rollup_commits_its_output_lineage_and_replays_without_allocating_another_attempt()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        CampaignRollupStore rollups = new(new FixedCovenantConnectionSource(fixture.Connection), CovenantSqliteConnectionInitializer.Instance);

        CampaignRollupInput input = await PrepareRollupAsync(fixture, rollups);

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceIdentity identity = await RollupIdentityAsync(fixture, claim, input);

        CampaignMaintenanceCheckpointStore checkpoints = Store(fixture);

        CampaignMaintenanceAttempt attempt = (await checkpoints.PrepareAttemptAsync(identity, Digest(7), CancellationToken.None)).Value;

        CampaignMaintenanceAttempt acknowledged = (await checkpoints.RecordDisclosureAsync(attempt, Digest(9), CancellationToken.None)).Value;

        Result<CampaignRollupArtifact> published = await rollups.PublishRollupAsync(input, "Project convention: use SQLite.",
            new CampaignMaintenancePublication(acknowledged, Digest(9)), null, CancellationToken.None);

        Assert.True(published.IsSuccess, published.IsFailure ? published.Error.Message : null);

        CampaignRollupArtifact output = published.Value;

        Assert.NotEqual(input.SourceManifestDigest, output.SourceManifestDigest);

        Assert.Equal(output, (await rollups.ReadCurrentAsync(CampaignId, null, CancellationToken.None)).Value);

        Assert.True((await rollups.ValidateAsync(output, null, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_rollup_sources;"));

        CampaignMaintenanceAttempt replay = (await checkpoints.PrepareAttemptAsync(identity, Digest(8), CancellationToken.None)).Value;

        Assert.Equal(CovenantMaintenanceCheckpoint.Committed, replay.State);

        Assert.Equal(output.ArtifactId, replay.OutputArtifactId);

        Assert.Equal(output.Revision, replay.OutputRevision);

        Assert.Equal(acknowledged.PhysicalProviderAttemptOrdinal, replay.PhysicalProviderAttemptOrdinal);

        Assert.Equal(acknowledged.ProviderCallDigest, replay.ProviderCallDigest);

        Assert.Equal(acknowledged.CheckpointRevision + 1, replay.CheckpointRevision);

        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_maintenance_checkpoints;"));

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT CompletedStepMask FROM session_turn_claims;"));

        Assert.Null((await rollups.PrepareRollupAsync(CampaignId, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task A_rollup_attempt_for_a_different_input_manifest_publishes_no_output_or_checkpoint()
    {
        await using CovenantCanonicalFixture fixture = await CreateAsync();

        CampaignRollupStore rollups = new(new FixedCovenantConnectionSource(fixture.Connection), CovenantSqliteConnectionInitializer.Instance);

        CampaignRollupInput input = await PrepareRollupAsync(fixture, rollups);

        SessionTurnClaimLease claim = await AcquireAsync(fixture, Guid.NewGuid());

        CampaignMaintenanceIdentity identity = (await RollupIdentityAsync(fixture, claim, input)) with { SourceManifestDigest = Digest(10) };

        CampaignMaintenanceCheckpointStore checkpoints = Store(fixture);

        CampaignMaintenanceAttempt attempt = (await checkpoints.PrepareAttemptAsync(identity, Digest(7), CancellationToken.None)).Value;

        CampaignMaintenanceAttempt acknowledged = (await checkpoints.RecordDisclosureAsync(attempt, Digest(9), CancellationToken.None)).Value;

        Result<CampaignRollupArtifact> refused = await rollups.PublishRollupAsync(input, "Project convention: use SQLite.",
            new CampaignMaintenancePublication(acknowledged, Digest(9)), null, CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_rollup_artifacts;"));

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM campaign_rollup_sources;"));

        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT Revision FROM campaign_rollup_state;"));

        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT CheckpointStateCode FROM campaign_maintenance_checkpoints;"));

        Assert.True((await checkpoints.ValidateAttemptAsync(acknowledged, CancellationToken.None)).IsSuccess);
    }

    private static async Task<CampaignRollupInput> PrepareRollupAsync(CovenantCanonicalFixture fixture, CampaignRollupStore rollups)
    {
        Guid sourceSession = Guid.NewGuid();

        await CovenantCapacityFixture.AddSessionAsync(fixture, sourceSession, CancellationToken.None);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO session_campaign_bindings(SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES($session, 2, $campaign, $now);
            INSERT INTO Entries(Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence)
            VALUES($entry, $session, 1, 'Choose SQLite', '', $now, 1);
            """;

        _ = command.Parameters.AddWithValue("$session", sourceSession);

        _ = command.Parameters.AddWithValue("$campaign", CampaignId);

        _ = command.Parameters.AddWithValue("$entry", Guid.NewGuid());

        _ = command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using (CovenantSqliteAuthorizationScope bindingWrite = CovenantSqliteConnectionInitializer.Instance.Authorize(
            fixture.Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        Result<CampaignContributionInput?> prepared = await rollups.PrepareContributionAsync(sourceSession, null, null, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        Assert.NotNull(prepared.Value);

        Result<CampaignRollupArtifact> contribution = await rollups.PublishContributionAsync(prepared.Value, "Use SQLite for this project.", null, null, CancellationToken.None);

        Assert.True(contribution.IsSuccess, contribution.IsFailure ? contribution.Error.Message : null);

        Result<CampaignRollupInput?> fold = await rollups.PrepareRollupAsync(CampaignId, null, CancellationToken.None);

        Assert.True(fold.IsSuccess, fold.IsFailure ? fold.Error.Message : null);

        Assert.NotNull(fold.Value);

        return fold.Value;
    }

    private static async Task<CampaignMaintenanceIdentity> RollupIdentityAsync(CovenantCanonicalFixture fixture, SessionTurnClaimLease lease, CampaignRollupInput input) =>
        new(lease,
            CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(CampaignId),
                await ScalarAsync(fixture, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"), 1, null, null),
            CovenantMaintenanceStep.CampaignRollup, null, input.SourceManifestDigest, input.SourceGeneration, input.ExpectedRevision);

    private static async Task<CovenantCanonicalFixture> CreateAsync()
    {
        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: [.. GrimoireSchemaCatalog.CoreObjects.Select(static definition => definition.Name)]);

        await fixture.AddCampaignAsync(CampaignId, "maintenance", CancellationToken.None);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, CancellationToken.None);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO session_campaign_bindings(SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES($session, 2, $campaign, $now);
            INSERT OR IGNORE INTO installation_turn_quota_state(StateKey, ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount)
            VALUES(1, 0, 0, 0);
            """;

        _ = command.Parameters.AddWithValue("$session", SessionId);

        _ = command.Parameters.AddWithValue("$campaign", CampaignId);

        _ = command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using CovenantSqliteAuthorizationScope bindingWrite = CovenantSqliteConnectionInitializer.Instance.Authorize(
            fixture.Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        _ = await command.ExecuteNonQueryAsync();

        return fixture;
    }

    private static async Task<SessionTurnClaimLease> AcquireAsync(CovenantCanonicalFixture fixture, Guid bootId)
    {
        SessionTurnRequestIdentity request = new(
            InstallationId, 0, InstallationId, SessionId, SessionTurnSurface.Intelligence,
            Digest(1), Digest(2), null, 0, 0);

        Result<SessionTurnClaimLease> acquired = await new SessionTurnClaimStore(
            new FixedCovenantConnectionSource(fixture.Connection), new CovenantQuotaGuard(), bootId)
            .AcquireAsync(request, CancellationToken.None);

        Assert.True(acquired.IsSuccess, acquired.IsFailure ? acquired.Error.Message : null);

        return acquired.Value;
    }

    private static async Task<CampaignMaintenanceIdentity> IdentityAsync(CovenantCanonicalFixture fixture, SessionTurnClaimLease lease, byte manifest) =>
        new(lease,
            CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(CampaignId),
                await ScalarAsync(fixture, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"), 1, null, null),
            CovenantMaintenanceStep.CampaignContribution, SessionId, Digest(manifest), 0, 0);

    private static CampaignMaintenanceCheckpointStore Store(CovenantCanonicalFixture fixture) =>
        new(new FixedCovenantConnectionSource(fixture.Connection));

    private static CovenantDigest Digest(byte seed) => CovenantOperationGateFixture.Digest(seed);

    private static async Task AssertCanonicalInstantsAsync(CovenantCanonicalFixture fixture, int expectedCount)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            SELECT HeartbeatAtUtc FROM session_turn_claims
            UNION ALL SELECT LeaseDeadlineUtc FROM session_turn_claims
            UNION ALL SELECT UpdatedAtUtc FROM campaign_maintenance_checkpoints;
            """;

        int count = 0;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", reader.GetString(0));

            count++;
        }

        Assert.Equal(expectedCount, count);
    }

    private static async Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(CovenantCanonicalFixture fixture, string sql)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }
}
