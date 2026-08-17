using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Reconciling protected state inside restore staging (§10.19.9).
/// </summary>
/// <remarks>
/// Every assertion here is about a staged snapshot that has not been published as live. The whole
/// point of the phase is that a generation which describes another installation is converted into one
/// this machine may adopt <em>before</em> anything is displaced — so a failure leaves the live tree
/// untouched and a success leaves nothing of the source's authority behind.
/// </remarks>
public sealed class BackupCovenantRestoreReconcilerTests : IAsyncLifetime
{

    private static readonly string[] CoreObjects =
    [
        "covenant_authority_state",
        "campaign_path_identities",
        "Campaigns",
        "artifact_sensitivity",
        "managed_file_write_intents",
        "local_erasure_work_items",
        "restored_managed_file_authority_tombstones",
        "restored_managed_file_authority_tombstones_guard_insert",
        "restored_managed_file_authority_tombstones_guard_update",
        "restored_managed_file_authority_tombstones_guard_delete",
        "external_disclosure_state",
        "external_disclosure_state_guard_delete",
        "Sessions",
        "assistant_finalization_capacity_reservations",
        "session_turn_claims",
        "session_turn_claims_validate_insert",
        "session_turn_claims_validate_update",
    ];

    private const string DestinationIdentity = "11111111-2222-4333-8444-555555555555";

    private CovenantSchemaScratchDatabase _staged = null!;

    public async Task InitializeAsync()
    {

        _staged = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _staged.InstallCoreObjectsAsync(CoreObjects, CancellationToken.None);

        await _staged.InstallCanonicalAsync(CancellationToken.None);

        await _staged.InstallAcceleratorAsync(CancellationToken.None);

    }

    [Fact]
    public async Task A_fresh_dataset_generation_and_advanced_epochs_replace_the_archived_ones()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        string? before = await _staged.ScalarStringAsync(
            "SELECT hex(DatasetGeneration) FROM covenant_state;",
            CancellationToken.None);

        long acceleratorBefore = await _staged.ScalarLongAsync(
            "SELECT AcceleratorEpoch FROM covenant_state;",
            CancellationToken.None);

        long envelopeBefore = await _staged.ScalarLongAsync(
            "SELECT EnvelopeKeyEpoch FROM covenant_state;",
            CancellationToken.None);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        string? after = await _staged.ScalarStringAsync(
            "SELECT hex(DatasetGeneration) FROM covenant_state;",
            CancellationToken.None);

        Assert.NotEqual(before, after);

        Assert.Equal(
            acceleratorBefore + 1,
            await _staged.ScalarLongAsync(
                "SELECT AcceleratorEpoch FROM covenant_state;",
                CancellationToken.None));

        Assert.Equal(
            envelopeBefore + 1,
            await _staged.ScalarLongAsync(
                "SELECT EnvelopeKeyEpoch FROM covenant_state;",
                CancellationToken.None));

        Assert.Equal(receipt.Value.AcceleratorEpoch, acceleratorBefore + 1);

        Assert.Equal(receipt.Value.EnvelopeKeyEpoch, envelopeBefore + 1);

        Assert.NotEqual(Guid.Empty, receipt.Value.StagedDatasetGeneration);

    }

    [Fact]
    public async Task The_archived_outbox_is_drained_and_full_text_search_is_left_dirty()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedProjectionAsync();

        Assert.Equal(1, await CountAsync("covenant_search_outbox"));

        Assert.Equal(1, await CountAsync("covenant_search_documents"));

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0, await CountAsync("covenant_search_outbox"));

        Assert.Equal(1UL, receipt.Value.ClearedOutboxRows);

        // The projection rows are left exactly where they are. Only the outbox worker and the
        // rebuilder write accelerator state, and neither will serve these: a null applied tuple over a
        // nonempty projection is precisely the state the worker refuses and the rebuilder clears.
        Assert.Equal(1, await CountAsync("covenant_search_documents"));

        // A null applied tuple plus FullRebuildRequired is what "dirty" means here: the accelerator
        // has published nothing for this dataset and must not be trusted to answer a query.
        Assert.Equal(
            1,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_state WHERE AppliedDatasetGeneration IS NULL "
                + "AND AppliedSearchSequence IS NULL AND RebuildStateCode = 2 "
                + "AND RebuildTargetSequence IS NULL AND RebuildCursor IS NULL;",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_clean_archive_can_never_launder_the_destination_taint()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync(
            destinationAuthority: Tainted(DestinationIdentity, epoch: 9));

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, receipt.Value.HostToolsState);

        Assert.Equal(
            3,
            await _staged.ScalarLongAsync(
                "SELECT HostToolsStateCode FROM covenant_authority_state;",
                CancellationToken.None));

        // The destination's own identity, and an epoch past both lineages: every read authority
        // issued under either one has to stop being valid.
        Assert.Equal(
            DestinationIdentity,
            await _staged.ScalarStringAsync(
                "SELECT InstallationIdentity FROM covenant_authority_state;",
                CancellationToken.None));

        Assert.Equal(
            10,
            await _staged.ScalarLongAsync(
                "SELECT AuthorityEpoch FROM covenant_authority_state;",
                CancellationToken.None));

    }

    [Fact]
    public async Task Archived_taint_is_carried_into_a_clean_destination()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.HostToolsTainted);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync(
            destinationAuthority: Clean(DestinationIdentity, epoch: 2));

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, receipt.Value.HostToolsState);

        Assert.Equal(
            1,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_authority_state "
                + "WHERE HostToolsStateCode = 3 AND TransitionId IS NOT NULL "
                + "AND TaintTimeMasterVersion IS NOT NULL;",
                CancellationToken.None));

    }

    [Fact]
    public async Task Destination_disclosure_evidence_is_joined_rather_than_replaced()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        // The archive says two effects left through this destination; the machine says five.
        await SeedDisclosureBucketAsync(count: 2);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync(
            disclosure:
            [
                new CovenantDisclosureState(
                    CovenantEgressDestination.Provider,
                    CovenantDisclosureRevocability.Nonrevocable,
                    CovenantDisclosureCountKind.Exact,
                    everOccurred: true,
                    count: 5,
                    maximumTimestamp: 100,
                    evidenceBloom: Bloom(0x0F)),
            ]);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(1, receipt.Value.JoinedDisclosureBuckets);

        // A join is allowed to overstate and never to understate, so the larger count survives.
        Assert.Equal(
            5,
            await _staged.ScalarLongAsync(
                "SELECT JoinedCount FROM external_disclosure_state;",
                CancellationToken.None));

        Assert.Equal(
            1,
            await _staged.ScalarLongAsync(
                "SELECT EverOccurred FROM external_disclosure_state;",
                CancellationToken.None));

    }

    [Fact]
    public async Task Pending_and_begun_turn_claims_are_terminalized_as_restore_interrupted()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedTurnClaimAsync("claim-pending", begun: false);

        await SeedTurnClaimAsync("claim-begun", begun: true);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(2UL, receipt.Value.TerminalizedTurnClaims);

        Assert.Equal(
            2,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM session_turn_claims WHERE StateCode = 6 "
                + "AND ExecutorId IS NULL AND LeaseDeadlineUtc IS NULL "
                + "AND TerminalAtUtc IS NOT NULL AND TerminalErrorCode IS NOT NULL;",
                CancellationToken.None));

    }

    [Fact]
    public async Task Every_restored_Campaign_path_is_left_unresolved()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedCampaignPathIdentityAsync();

        Assert.Equal(1, await CountAsync("campaign_path_identities"));

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        // A resolved path is a keyed identity of a directory on the machine the archive came from.
        // Keeping it would let this installation act on a root it has never opened.
        Assert.Equal(0, await CountAsync("campaign_path_identities"));

        Assert.Equal(1UL, receipt.Value.UnresolvedCampaignPaths);

    }

    [Fact]
    public async Task Surviving_source_authority_refuses_the_whole_reconciliation()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedManagedIntentAsync();

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, receipt.Error.Code);

    }

    [Fact]
    public async Task A_label_the_sanitizer_reported_removed_may_not_still_exist()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedLabelAsync("label-1");

        await SeedManagedIntentAsync(sensitivityLabelId: "label-1");

        await SanitizeAsync();

        Assert.Equal(0, await CountAsync("artifact_sensitivity"));

        // The archive is replayed over the stripped generation: a label the tombstone says was
        // removed is back. Validation is what makes the tombstone a claim about this database
        // rather than about a moment that has since passed.
        await SeedLabelAsync("label-1");

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, receipt.Error.Code);

    }

    [Fact]
    public async Task A_sanitized_generation_reconciles_and_keeps_its_untouched_labels()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedLabelAsync("label-adopted");

        await SeedLabelAsync("label-kept");

        await SeedManagedIntentAsync(sensitivityLabelId: "label-adopted");

        await SanitizeAsync();

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync();

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(1UL, receipt.Value.RetainedLabels);

        Assert.Equal(
            "label-kept",
            await _staged.ScalarStringAsync(
                "SELECT LabelId FROM artifact_sensitivity;",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_failed_reconciliation_leaves_the_staged_snapshot_exactly_as_it_was()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedProjectionAsync();

        await SeedManagedIntentAsync();

        string? generation = await _staged.ScalarStringAsync(
            "SELECT hex(DatasetGeneration) FROM covenant_state;",
            CancellationToken.None);

        Assert.True((await ReconcileAsync()).IsFailure);

        // The caller owns the transaction, so a refusal must have changed nothing it can observe.
        Assert.Equal(
            generation,
            await _staged.ScalarStringAsync(
                "SELECT hex(DatasetGeneration) FROM covenant_state;",
                CancellationToken.None));

        Assert.Equal(1, await CountAsync("covenant_search_outbox"));

    }

    public async Task DisposeAsync()
    {

        if (_staged is not null)
        {

            await _staged.DisposeAsync();

        }

    }

    private static string Describe<T>(Result<T> result) =>
        result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : string.Empty;

    private static CovenantAuthorityStateRow Clean(string identity, long epoch) =>
        new(identity, epoch, CovenantHostToolsState.Clean, null, null, null);

    private static CovenantAuthorityStateRow Tainted(string identity, long epoch) =>
        new(
            identity,
            epoch,
            CovenantHostToolsState.HostToolsTainted,
            4,
            [.. Enumerable.Repeat((byte)9, 32)],
            "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE");

    private async Task<Result<BackupCovenantRestoreReconciliationReceipt>> ReconcileAsync(
        CovenantAuthorityStateRow? destinationAuthority = null,
        IReadOnlyList<CovenantDisclosureState>? disclosure = null)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _staged.Connection.BeginTransactionAsync(CancellationToken.None);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await BackupCovenantRestoreReconciler
            .ReconcileStagedAsync(
                _staged.Connection,
                transaction,
                new BackupCovenantRestoreDestinationState(
                    destinationAuthority ?? Clean(DestinationIdentity, epoch: 1),
                    disclosure ?? []),
                CovenantSqliteConnectionInitializer.Instance,
                TimeProvider.System,
                CancellationToken.None);

        if (receipt.IsFailure)
        {

            await transaction.RollbackAsync(CancellationToken.None);

            return receipt;

        }

        await transaction.CommitAsync(CancellationToken.None);

        return receipt;

    }

    private Task<long> CountAsync(string table) =>
        _staged.ScalarLongAsync($"SELECT COUNT(*) FROM {table};", CancellationToken.None);

    private async Task SeedAuthorityAsync(CovenantHostToolsState state)
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_authority_state (
                StateKey, InstallationIdentity, AuthorityEpoch, CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint, RecoveryEnvelopeEpoch, HostToolsStateCode,
                TaintTimeMasterVersion, TaintFingerprint, TransitionId, UpdatedAtUtc)
            VALUES (1, $identity, 4, 1, $fingerprint, 1, $state, $taintVersion, $taintFingerprint,
                    $transition, $now);
            """;

        _ = command.Parameters.AddWithValue("$identity", "99999999-8888-4777-8666-555555555555");

        _ = command.Parameters.AddWithValue("$fingerprint", Enumerable.Repeat((byte)2, 32).ToArray());

        _ = command.Parameters.AddWithValue("$state", (int)state);

        bool tainted = state != CovenantHostToolsState.Clean;

        _ = command.Parameters.AddWithValue("$taintVersion", tainted ? 1L : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$taintFingerprint",
            tainted ? Enumerable.Repeat((byte)3, 32).ToArray() : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$transition",
            tainted ? "CCCCCCCC-DDDD-4EEE-8FFF-111111111111" : (object)DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedProjectionAsync()
    {

        await _staged.ExecuteAsync(
            """
            INSERT INTO covenant_search_outbox (
                SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            VALUES (1, 0, 1, 'entry-1', 1, 'version-1');
            """,
            CancellationToken.None);

        await _staged.ExecuteAsync(
            """
            INSERT INTO covenant_search_documents (
                SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration,
                CanonicalSearchSequence)
            SELECT 1, 'entry-1', 1, 'version-1', 1, NULL, 1, 'key', 'authored', 'compiled',
                   DatasetGeneration, 1
            FROM covenant_state;
            """,
            CancellationToken.None);

    }

    private async Task SeedDisclosureBucketAsync(long count)
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES ($destination, $revocability, 1, 1, $count, 50, $bloom, $now);
            """;

        _ = command.Parameters.AddWithValue(
            "$destination",
            (int)CovenantEgressDestination.Provider);

        _ = command.Parameters.AddWithValue(
            "$revocability",
            (int)CovenantDisclosureRevocability.Nonrevocable);

        _ = command.Parameters.AddWithValue("$count", count);

        _ = command.Parameters.AddWithValue("$bloom", Bloom(0x33));

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static byte[] Bloom(byte value) => [.. Enumerable.Repeat(value, 32)];

    private async Task SeedTurnClaimAsync(string claimId, bool begun)
    {

        string sessionId = $"session-{claimId}";

        string reservationId = $"reservation-{claimId}";

        string assistantEntryId = $"assistant-{claimId}";

        await using (SqliteCommand session = _staged.Connection.CreateCommand())
        {

            session.CommandText = """
                INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'active', $now, $now);
                """;

            _ = session.Parameters.AddWithValue("$id", sessionId);

            _ = session.Parameters.AddWithValue("$now", Iso());

            _ = await session.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand reservation = _staged.Connection.CreateCommand())
        {

            reservation.CommandText = """
                INSERT INTO assistant_finalization_capacity_reservations (
                    ReservationId, SessionId, AssistantEntryId, OriginCode, ClaimId, StateCode,
                    CreatedAtUtc, StateChangedAtUtc)
                VALUES ($reservation, $session, $assistant, 1, $claim, 1, $now, $now);
                """;

            _ = reservation.Parameters.AddWithValue("$reservation", reservationId);

            _ = reservation.Parameters.AddWithValue("$session", sessionId);

            _ = reservation.Parameters.AddWithValue("$assistant", assistantEntryId);

            _ = reservation.Parameters.AddWithValue("$claim", claimId);

            _ = reservation.Parameters.AddWithValue("$now", Iso());

            _ = await reservation.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand claim = _staged.Connection.CreateCommand())
        {

            claim.CommandText = """
                INSERT INTO session_turn_claims (
                    ClaimId, OriginInstallationId, OriginRestoreEpoch, ClientTurnId, SessionId,
                    SurfaceCode, RequestDigest, DependencyDigest, StateCode,
                    PreRequestHistoryWatermarkUtc, PreRequestHistoryRevision,
                    InputSensitivityRevision, ExpectedCurrentSensitivityRevision,
                    FinalizationReservationId, CheckpointRevision, CompletedStepMask, CreatedAtUtc)
                VALUES ($claim, $origin, 0, $client, $session, 1, $digest, $digest, 1,
                        NULL, 0, 0, 0, $reservation, 0, 0, $now);
                """;

            _ = claim.Parameters.AddWithValue("$claim", claimId);

            _ = claim.Parameters.AddWithValue("$origin", "77777777-6666-4555-8444-333333333333");

            _ = claim.Parameters.AddWithValue("$client", $"client-{claimId}");

            _ = claim.Parameters.AddWithValue("$session", sessionId);

            _ = claim.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)4, 32).ToArray());

            _ = claim.Parameters.AddWithValue("$reservation", reservationId);

            _ = claim.Parameters.AddWithValue("$now", Iso());

            _ = await claim.ExecuteNonQueryAsync(CancellationToken.None);

        }

        if (!begun)
        {

            return;

        }

        await using SqliteCommand advance = _staged.Connection.CreateCommand();

        advance.CommandText = """
            UPDATE session_turn_claims
            SET StateCode = 2, UserEntryId = $user, AssistantEntryId = $assistant,
                ExecutorId = 'executor', LeaseDeadlineUtc = $now
            WHERE ClaimId = $claim;
            """;

        _ = advance.Parameters.AddWithValue("$user", $"user-{claimId}");

        _ = advance.Parameters.AddWithValue("$assistant", assistantEntryId);

        _ = advance.Parameters.AddWithValue("$claim", claimId);

        _ = advance.Parameters.AddWithValue("$now", Iso());

        _ = await advance.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedCampaignPathIdentityAsync()
    {

        await using (SqliteCommand campaign = _staged.Connection.CreateCommand())
        {

            campaign.CommandText = """
                INSERT INTO "Campaigns" (
                    "Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'Archived', 'archived', '/archived/campaign', 1, '{}', $now, $now);
                """;

            _ = campaign.Parameters.AddWithValue("$id", "campaign-1");

            _ = campaign.Parameters.AddWithValue("$now", Iso());

            _ = await campaign.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using SqliteCommand identity = _staged.Connection.CreateCommand();

        identity.CommandText = """
            INSERT INTO campaign_path_identities (
                CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest,
                UpdatedAtUtc)
            VALUES ($id, 1, 1, '/archived/campaign', 2, $digest, $now);
            """;

        _ = identity.Parameters.AddWithValue("$id", "campaign-1");

        _ = identity.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)8, 32).ToArray());

        _ = identity.Parameters.AddWithValue("$now", Iso());

        _ = await identity.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedManagedIntentAsync(string? sensitivityLabelId = null)
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO managed_file_write_intents (
                WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                ExpectedContentHash, ExpectedContentLength, CreatedChildPhysicalIdentityDigest,
                FinalOwnershipEvidence, PhaseCode, Revision, RetryCount, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($write, $effect, $artifact, $label, $digest, $pendingLabel, $evidence,
                    $digest, 4, NULL, NULL, 1, 0, 0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$write", $"write-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue(
            "$effect",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue(
            "$label",
            sensitivityLabelId ?? $"label-{Guid.NewGuid():N}");

        _ = command.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)5, 32).ToArray());

        _ = command.Parameters.AddWithValue("$pendingLabel", new byte[64]);

        _ = command.Parameters.AddWithValue("$evidence", new byte[64]);

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedLabelAsync(string labelId)
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, 1, $artifact, 1, 2, NULL, $bloom, NULL, NULL, NULL, 1,
                    $digest, $digest, NULL, NULL, NULL, $digest, $now);
            """;

        _ = command.Parameters.AddWithValue("$label", labelId);

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$bloom", Enumerable.Repeat((byte)0x0F, 32).ToArray());

        _ = command.Parameters.AddWithValue("$digest", Enumerable.Repeat((byte)5, 32).ToArray());

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    /// <summary>
    /// Strips the seeded managed authority through the real sealed capability, which is the only
    /// thing in the build that can write a tombstone.
    /// </summary>
    private async Task SanitizeAsync()
    {

        Result<BackupRestoreManagedAuthoritySanitizationReceipt> receipt =
            await RestoreStagingManagedAuthoritySanitizationCapability.Mint(
                    CovenantSqliteConnectionInitializer.Instance,
                    _staged.Connection,
                    new CovenantExclusiveRecoveryOwner(
                        Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                        CovenantExclusiveOperation.BackupRestore,
                        new CovenantDigest([.. Enumerable.Repeat((byte)7, 32)])),
                    Guid.Parse("cccccccc-dddd-4eee-8fff-111111111111"),
                    TimeProvider.System,
                    static () => true)
                .Value
                .RunImmediateAsync(CancellationToken.None);

        Assert.True(receipt.IsSuccess, Describe(receipt));

    }

    private static string Iso() =>
        DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

}
