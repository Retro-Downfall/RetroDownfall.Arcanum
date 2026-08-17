using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Inventorying and purging the protected state a staged archive carries (§10.19.10).
/// </summary>
/// <remarks>
/// Everything here runs against a real three-tier staged database that has never been published as
/// live, which is the only place the purge is allowed to happen: the point of removing the Covenant
/// family before replacement is that the live installation never holds it at all.
///
/// <para>The preservation assertions matter as much as the removal ones. A purge that also cleared the
/// destination's host-tools taint or its joined disclosure counts would turn an honest nonrevocable
/// record into a clean one, which is the one thing no erasure path may do (§10.15).</para>
/// </remarks>
public sealed class BackupRestoreProtectedStatePurgeTests : IAsyncLifetime
{

    private static readonly string[] CoreObjects =
    [
        "covenant_authority_state",
        "campaign_path_identities",
        "Campaigns",
        "artifact_sensitivity",
        "artifact_sensitivity_guard_delete",
        "artifact_sensitivity_guard_update",
        "managed_file_write_intents",
        "local_erasure_work_items",
        "restored_managed_file_authority_tombstones",
        "restored_managed_file_authority_tombstones_guard_insert",
        "restored_managed_file_authority_tombstones_guard_update",
        "restored_managed_file_authority_tombstones_guard_delete",
        "external_disclosure_state",
        "external_disclosure_state_guard_delete",
        "external_disclosure_receipts",
        "external_disclosure_receipts_guard_delete",
        "external_disclosure_receipts_guard_update",
        "Sessions",
        "session_sensitivity_state",
        "session_summary_artifacts",
        "session_summary_artifacts_guard_delete",
        "session_summary_artifacts_guard_update",
        "session_summary_state",
        "session_turn_claims",
        "session_turn_claims_validate_insert",
        "session_turn_claims_validate_update",
        "assistant_finalization_capacity_reservations",
    ];

    private const string DestinationIdentity = "11111111-2222-4333-8444-555555555555";

    private const string SessionId = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa";

    private const string SummaryArtifactId = "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb";

    private const string SummaryLabelId = "cccccccc-3333-4333-8333-cccccccccccc";

    private CovenantSchemaScratchDatabase _staged = null!;

    public async Task InitializeAsync()
    {

        _staged = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _staged.InstallCoreObjectsAsync(CoreObjects, CancellationToken.None);

        await _staged.InstallCanonicalAsync(CancellationToken.None);

        await _staged.InstallAcceleratorAsync(CancellationToken.None);

    }

    public async Task DisposeAsync()
    {

        if (_staged is not null)
        {

            await _staged.DisposeAsync();

        }

    }

    [Fact]
    public async Task An_empty_Covenant_tier_inventories_as_carrying_no_protected_state()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        BackupRestoreProtectedStateInventory inventory = await InspectAsync();

        // The canonical singleton is installed by the schema, not carried by the operator, so an
        // installation that merely has the tier must not read as holding protected state.
        Assert.Equal(0, inventory.CanonicalRows);

        Assert.Equal(0, inventory.AcceleratorRows);

        Assert.Equal(0, inventory.ProtectedArtifacts);

        Assert.False(inventory.SourceAuthorityTainted);

        Assert.False(inventory.CarriesProtectedState);

    }

    [Fact]
    public async Task Canonical_rows_labels_and_source_taint_are_counted_independently()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.HostToolsTainted);

        await SeedCanonicalFamilyAsync();

        await SeedProtectedSummaryAsync();

        BackupRestoreProtectedStateInventory inventory = await InspectAsync();

        Assert.Equal(3, inventory.CanonicalRows);

        Assert.Equal(1, inventory.AcceleratorRows);

        Assert.Equal(1, inventory.ProtectedArtifacts);

        Assert.True(inventory.SourceAuthorityTainted);

        Assert.True(inventory.CarriesProtectedState);

    }

    [Fact]
    public async Task A_pending_taint_counts_as_a_source_that_cannot_prove_it_is_clean()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.PendingHostToolsTaint);

        Assert.True((await InspectAsync()).SourceAuthorityTainted);

    }

    [Fact]
    public async Task An_absent_Covenant_tier_inventories_as_none()
    {

        // A snapshot from a build that predates the Covenant tables carries nothing and must not be
        // read as a refusal: absence is a real answer.
        await using CovenantSchemaScratchDatabase bare =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await bare.InstallCoreObjectsAsync(["Sessions"], CancellationToken.None);

        BackupRestoreProtectedStateInventory inventory = await BackupRestoreProtectedStateInspector
            .InspectAsync(bare.Connection, CancellationToken.None);

        Assert.Equal(BackupRestoreProtectedStateInventory.None, inventory);

    }

    [Fact]
    public async Task A_purge_removes_the_whole_Covenant_family_before_replacement()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedCanonicalFamilyAsync();

        Result<BackupCovenantRestoreReconciliationReceipt> receipt =
            await ReconcileAsync(purgeProtectedState: true);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        foreach (string table in new[]
                 {
                     "covenant_entries",
                     "covenant_heads",
                     "covenant_versions",
                     "covenant_version_attachment_provenance",
                     "covenant_mutation_receipts",
                     "covenant_turn_receipts",
                     "covenant_turn_receipt_aggregate",
                     "covenant_key_epochs",
                     "covenant_search_outbox",
                     "covenant_search_documents",
                 })
        {

            Assert.Equal(0, await CountAsync(table));

        }

        // The full-text index goes with the projection it mirrors. Leaving it would keep the authored and
        // compiled text of every entry readable in the restored installation, which is the one thing a
        // purge exists to prevent — "unusable" is not "removed".
        Assert.Equal(0, await CountAsync("covenant_fts"));

        // The canonical singleton survives: it is schema, not content, and the reissue below stamps a
        // fresh generation onto it.
        Assert.Equal(1, await CountAsync("covenant_state"));

        BackupRestoreProtectedStatePurgeReceipt purge =
            Assert.IsType<BackupRestoreProtectedStatePurgeReceipt>(receipt.Value.ProtectedStatePurge);

        Assert.Equal(3UL, purge.CanonicalRows);

        Assert.Equal(1UL, purge.AcceleratorRows);

    }

    [Fact]
    public async Task A_purge_empties_a_referenced_entry_graph_child_first()
    {

        // Foreign keys are on, and immediate constraints are checked at the end of every statement, so
        // clearing the family in the wrong order fails outright rather than leaving a mixture: a version
        // references its entry and a provenance row references its version.
        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedEntryGraphAsync();

        Assert.Equal(1, await CountAsync("covenant_entries"));

        Assert.Equal(1, await CountAsync("covenant_versions"));

        Assert.Equal(1, await CountAsync("covenant_version_attachment_provenance"));

        Result<BackupCovenantRestoreReconciliationReceipt> receipt =
            await ReconcileAsync(purgeProtectedState: true);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0, await CountAsync("covenant_version_attachment_provenance"));

        Assert.Equal(0, await CountAsync("covenant_versions"));

        Assert.Equal(0, await CountAsync("covenant_entries"));

        Assert.Equal(
            3UL,
            Assert.IsType<BackupRestoreProtectedStatePurgeReceipt>(receipt.Value.ProtectedStatePurge)
                .CanonicalRows);

    }

    [Fact]
    public async Task A_purge_removes_every_protected_artifact_and_its_label()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedProtectedSummaryAsync();

        Assert.Equal(1, await CountAsync("session_summary_artifacts"));

        Assert.Equal(1, await CountAsync("session_summary_state"));

        Result<BackupCovenantRestoreReconciliationReceipt> receipt =
            await ReconcileAsync(purgeProtectedState: true);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(0, await CountAsync("artifact_sensitivity"));

        Assert.Equal(0, await CountAsync("session_summary_artifacts"));

        Assert.Equal(0, await CountAsync("session_summary_state"));

        // The mutable legacy column the artifact labelled is redacted in the same transaction, or the
        // content would survive with nothing left admitting it is Covenant derived.
        Assert.Equal(
            0,
            await _staged.ScalarLongAsync(
                "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Summary\" IS NOT NULL;",
                CancellationToken.None));

        BackupRestoreProtectedStatePurgeReceipt purge =
            Assert.IsType<BackupRestoreProtectedStatePurgeReceipt>(receipt.Value.ProtectedStatePurge);

        Assert.Equal(1UL, purge.RemovedLabels);

        Assert.Equal(0UL, receipt.Value.RetainedLabels);

    }

    [Fact]
    public async Task A_purged_Session_still_bars_a_cached_replay()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedProtectedSummaryAsync();

        Assert.True((await ReconcileAsync(purgeProtectedState: true)).IsSuccess);

        // Conservative in exactly one direction: the count drops to zero because no tainted artifact
        // remains, and the maximum stays because taint that has been purged still bars a replay.
        Assert.Equal(
            0,
            await _staged.ScalarLongAsync(
                "SELECT TaintedArtifactCount FROM session_sensitivity_state WHERE SessionId = '"
                + SessionId + "';",
                CancellationToken.None));

        Assert.Equal(
            1,
            await _staged.ScalarLongAsync(
                "SELECT MaximumSensitivityCode FROM session_sensitivity_state WHERE SessionId = '"
                + SessionId + "';",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_purge_preserves_the_destinations_taint_and_its_joined_disclosure_evidence()
    {

        // The archive is clean and carries protected state; the destination is tainted. Joining must
        // keep the destination's taint, and purging must not launder it away either.
        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedCanonicalFamilyAsync();

        await SeedProtectedSummaryAsync();

        await SeedDisclosureReceiptAsync();

        Result<BackupCovenantRestoreReconciliationReceipt> receipt = await ReconcileAsync(
            purgeProtectedState: true,
            destinationAuthority: Tainted(DestinationIdentity, epoch: 7),
            disclosure: [Bucket(count: 12, CovenantDisclosureCountKind.LowerBound)]);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, receipt.Value.HostToolsState);

        Assert.Equal(
            (int)CovenantHostToolsState.HostToolsTainted,
            await _staged.ScalarLongAsync(
                "SELECT HostToolsStateCode FROM covenant_authority_state WHERE StateKey = 1;",
                CancellationToken.None));

        Assert.Equal(
            DestinationIdentity,
            await _staged.ScalarStringAsync(
                "SELECT InstallationIdentity FROM covenant_authority_state WHERE StateKey = 1;",
                CancellationToken.None));

        // The joined bucket and the receipts behind it are outside every purge policy: a disclosure
        // that already happened is not an artifact.
        Assert.Equal(1, await CountAsync("external_disclosure_state"));

        Assert.Equal(
            12,
            await _staged.ScalarLongAsync(
                "SELECT JoinedCount FROM external_disclosure_state;",
                CancellationToken.None));

        Assert.Equal(1, await CountAsync("external_disclosure_receipts"));

    }

    [Fact]
    public async Task A_purge_over_an_empty_family_reports_zero_rather_than_taking_another_path()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        Result<BackupCovenantRestoreReconciliationReceipt> receipt =
            await ReconcileAsync(purgeProtectedState: true);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        BackupRestoreProtectedStatePurgeReceipt purge =
            Assert.IsType<BackupRestoreProtectedStatePurgeReceipt>(receipt.Value.ProtectedStatePurge);

        Assert.Equal(0UL, purge.CanonicalRows);

        Assert.Equal(0UL, purge.AcceleratorRows);

        Assert.Equal(0UL, purge.RemovedLabels);

        Assert.Equal(0UL, purge.RemovedArtifacts);

    }

    [Fact]
    public async Task A_preserving_reconciliation_leaves_the_family_and_the_labels_where_they_are()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        await SeedCanonicalFamilyAsync();

        await SeedProtectedSummaryAsync();

        Result<BackupCovenantRestoreReconciliationReceipt> receipt =
            await ReconcileAsync(purgeProtectedState: false);

        Assert.True(receipt.IsSuccess, Describe(receipt));

        Assert.Null(receipt.Value.ProtectedStatePurge);

        Assert.Equal(1, await CountAsync("artifact_sensitivity"));

        Assert.Equal(1, await CountAsync("session_summary_artifacts"));

        Assert.Equal(1UL, receipt.Value.RetainedLabels);

        Assert.Equal(1, await CountAsync("covenant_key_epochs"));

        // The reissue still drains the outbox for a dataset that is about to stop existing, which is
        // pre-existing behaviour and not a purge.
        Assert.Equal(0, await CountAsync("covenant_search_outbox"));

    }

    [Fact]
    public void Every_persisted_artifact_kind_has_a_purge_policy_the_staged_purge_can_resolve()
    {

        // The purge fails the whole restore on a label whose kind it cannot classify, because removing
        // the label alone would leave Covenant-derived content with nothing admitting it is protected.
        // That branch is unreachable today rather than untested: the column CHECK admits exactly codes
        // one through thirteen and the policy table covers all thirteen, so this is the assertion that
        // keeps it unreachable when a fourteenth kind is added.
        Assert.All(
            Enum.GetValues<SensitiveArtifactKind>(),
            static kind => Assert.True(
                CovenantSensitiveArtifactPurgePolicy.IsCovered(kind),
                $"{kind} has no protected-artifact purge policy."));

        Assert.Equal(
            Enum.GetValues<SensitiveArtifactKind>().Length,
            CovenantSensitiveArtifactPurgePolicy.All.Count);

        // And the codes the staged purge keys on are the enum values the column stores.
        Assert.Equal(
            [.. Enum.GetValues<SensitiveArtifactKind>().Select(static kind => (byte)kind).Order()],
            [.. CovenantSensitiveArtifactPurgePolicy.All.Select(static rule => rule.Code).Order()]);

    }

    [Fact]
    public async Task A_purge_makes_no_filesystem_call_for_a_managed_file_label()
    {

        await SeedAuthorityAsync(CovenantHostToolsState.Clean);

        string missing = Path.Combine(
            Path.GetTempPath(),
            "arcanum-purge-must-not-touch-" + Guid.NewGuid().ToString("N"));

        await File.WriteAllTextAsync(missing, "a file on the machine the archive came from");

        try
        {

            await SeedLabelAsync(
                "dddddddd-4444-4444-8444-dddddddddddd",
                "eeeeeeee-5555-4555-8555-eeeeeeeeeeee",
                SensitiveArtifactKind.ManagedWorkspaceFile,
                sessionId: null);

            Result<BackupCovenantRestoreReconciliationReceipt> receipt =
                await ReconcileAsync(purgeProtectedState: true);

            Assert.True(receipt.IsSuccess, Describe(receipt));

            Assert.Equal(0, await CountAsync("artifact_sensitivity"));

            // The row described a file on a different machine. Removing the label is the whole effect;
            // touching the path would be acting on authority this installation does not have.
            Assert.True(File.Exists(missing));

        }
        finally
        {

            File.Delete(missing);

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

    private static CovenantDisclosureState Bucket(long count, CovenantDisclosureCountKind kind) =>
        new(
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            kind,
            everOccurred: true,
            checked((ulong)count),
            50,
            Bloom(0x33));

    private static byte[] Bloom(byte marker)
    {

        byte[] bloom = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        bloom[0] = marker;

        return bloom;

    }

    private static string Iso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

    private Task<BackupRestoreProtectedStateInventory> InspectAsync() =>
        BackupRestoreProtectedStateInspector.InspectAsync(_staged.Connection, CancellationToken.None);

    private Task<long> CountAsync(string table) =>
        _staged.ScalarLongAsync($"SELECT COUNT(*) FROM {table};", CancellationToken.None);

    private async Task<Result<BackupCovenantRestoreReconciliationReceipt>> ReconcileAsync(
        bool purgeProtectedState,
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
                purgeProtectedState,
                CancellationToken.None);

        if (receipt.IsFailure)
        {

            await transaction.RollbackAsync(CancellationToken.None);

            return receipt;

        }

        await transaction.CommitAsync(CancellationToken.None);

        return receipt;

    }

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

    /// <summary>
    /// Seeds three canonical rows and one accelerator projection: an outbox delta, a key epoch, and a
    /// folded turn aggregate.
    /// </summary>
    /// <remarks>
    /// Deliberately not only the outbox. The reissue already drains that table for every restore, so an
    /// outbox-only fixture could not tell a purge apart from the reconciliation that always runs.
    /// </remarks>
    private async Task SeedCanonicalFamilyAsync()
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
            INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc)
            VALUES ('project/goal', 3, '2026-01-01T00:00:00.0000000Z');
            """,
            CancellationToken.None);

        await _staged.ExecuteAsync(
            $"""
            INSERT INTO covenant_turn_receipt_aggregate (
                SessionId, CoveredCount, EarliestCoveredAtUtc, LatestCoveredAtUtc,
                ConfirmedTokenTotal, ProposedTokenTotal, CompletedOutcomeCount, FailedOutcomeCount,
                CancelledOutcomeCount, InterruptedOutcomeCount, MutationTotal, ChainDigest,
                UpdatedAtUtc)
            VALUES ('{SessionId}', 0, NULL, NULL, 0, 0, 0, 0, 0, 0, 0, zeroblob(32),
                    '2026-01-01T00:00:00.0000000Z');
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

    /// <summary>
    /// Seeds the one referenced graph inside the canonical tier: an entry, a retire version on it, and
    /// one attachment-provenance row on that version.
    /// </summary>
    /// <remarks>
    /// A retirement rather than an assertion, because a retire version carries no content and therefore
    /// needs none of the digest and fence bookkeeping an authored one does. What matters here is the two
    /// foreign-key edges, not what the version says.
    /// </remarks>
    private async Task SeedEntryGraphAsync()
    {

        await _staged.ExecuteAsync(
            """
            INSERT INTO covenant_entries (
                EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ('entry-1', 1, NULL, 'project/goal', 'project/goal',
                    '2026-01-01T00:00:00.0000000Z');
            """,
            CancellationToken.None);

        await _staged.ExecuteAsync(
            """
            INSERT INTO covenant_versions (
                VersionId, EntryId, LaneCode, LaneRevision, OperationCode, CompiledByteCost,
                RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion, OriginCode,
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest,
                AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
            VALUES ('version-1', 'entry-1', 1, 1, 2, 0, 0, 1, 1, 1, 'mutation-1', zeroblob(32),
                    zeroblob(32), zeroblob(32), 1, zeroblob(32), '2026-01-01T00:00:00.0000000Z');
            """,
            CancellationToken.None);

        await _staged.ExecuteAsync(
            """
            INSERT INTO covenant_version_attachment_provenance (
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                SourceRangeKindCode)
            VALUES ('version-1', 0, 'attachment-1', 'attachment-1:1', 'notes.md', zeroblob(32), 1);
            """,
            CancellationToken.None);

    }

    /// <summary>
    /// Seeds one labelled Session summary: the Session, the immutable artifact, its current pointer,
    /// the mutable legacy column it shadows, and the sensitivity label that makes it protected.
    /// </summary>
    private async Task SeedProtectedSummaryAsync()
    {

        await using (SqliteCommand session = _staged.Connection.CreateCommand())
        {

            session.CommandText = """
                INSERT INTO "Sessions" ("Id", "Status", "Summary", "CreatedAt", "UpdatedAt")
                VALUES ($id, 'active', 'a protected summary', $now, $now);
                """;

            _ = session.Parameters.AddWithValue("$id", SessionId);

            _ = session.Parameters.AddWithValue("$now", Iso());

            _ = await session.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand artifact = _staged.Connection.CreateCommand())
        {

            artifact.CommandText = """
                INSERT INTO session_summary_artifacts (
                    ArtifactId, SessionId, Revision, ContentDigest, SensitivityCode,
                    SensitivityDigest, SummarizedThroughUtc, CreatedAtUtc)
                VALUES ($artifact, $session, 1, zeroblob(32), 1, zeroblob(32), NULL, $now);
                """;

            _ = artifact.Parameters.AddWithValue("$artifact", SummaryArtifactId);

            _ = artifact.Parameters.AddWithValue("$session", SessionId);

            _ = artifact.Parameters.AddWithValue("$now", Iso());

            _ = await artifact.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand pointer = _staged.Connection.CreateCommand())
        {

            pointer.CommandText = """
                INSERT INTO session_summary_state (
                    SessionId, CurrentArtifactId, Revision, UpdatedAtUtc)
                VALUES ($session, $artifact, 1, $now);
                """;

            _ = pointer.Parameters.AddWithValue("$session", SessionId);

            _ = pointer.Parameters.AddWithValue("$artifact", SummaryArtifactId);

            _ = pointer.Parameters.AddWithValue("$now", Iso());

            _ = await pointer.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using (SqliteCommand projection = _staged.Connection.CreateCommand())
        {

            projection.CommandText = """
                INSERT INTO session_sensitivity_state (
                    SessionId, TaintedArtifactCount, MaximumSensitivityCode,
                    GenerationProvenanceDigest, Revision, UpdatedAtUtc)
                VALUES ($session, 1, 1, zeroblob(32), 1, $now);
                """;

            _ = projection.Parameters.AddWithValue("$session", SessionId);

            _ = projection.Parameters.AddWithValue("$now", Iso());

            _ = await projection.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await SeedLabelAsync(SummaryLabelId, SummaryArtifactId, SensitiveArtifactKind.Summary, SessionId);

    }

    private async Task SeedLabelAsync(
        string labelId,
        string artifactId,
        SensitiveArtifactKind kind,
        string? sessionId)
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, $session, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        _ = command.Parameters.AddWithValue("$label", labelId);

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactId);

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$session", sessionId ?? (object)DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task SeedDisclosureReceiptAsync()
    {

        await using SqliteCommand command = _staged.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO external_disclosure_receipts (
                OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode,
                RevocabilityCode, DestinationDigest, SensitivityCode,
                GenerationProvenanceModeCode, ExactGenerationIds, GenerationBloom, DisclosedAtUtc)
            VALUES ($origin, 2, $subject, 1, 4, 1, zeroblob(32), 8, 2, zeroblob(32), 1,
                    1, $generations, NULL, $now);
            """;

        _ = command.Parameters.AddWithValue("$origin", "ffffffff-6666-4666-8666-ffffffffffff");

        _ = command.Parameters.AddWithValue("$subject", "ffffffff-7777-4777-8777-ffffffffffff");

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", Iso());

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

}
