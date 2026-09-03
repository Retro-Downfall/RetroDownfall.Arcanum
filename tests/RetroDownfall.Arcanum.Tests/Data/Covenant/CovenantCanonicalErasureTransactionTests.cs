using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one transaction that removes the Covenant family's rows, and the exact evidence it leaves.
/// </summary>
/// <remarks>
/// Every assertion here runs against a real encrypted file with the real canonical and accelerator
/// tiers installed, because most of what issue #125 promises is enforced by SQLite rather than by
/// C#: the delete guards, the singleton's monotonic counters, and the unconditional abort on
/// <c>external_disclosure_state</c> are all triggers, and a suite built on doubles would assert the
/// erasure's intentions rather than its effect.
/// </remarks>
public sealed class CovenantCanonicalErasureTransactionTests
{

    /// <summary>Every canonical table the erasure is responsible for emptying.</summary>
    private static readonly string[] FamilyTables =
    [
        "covenant_turn_receipts",
        "covenant_turn_receipt_aggregate",
        "covenant_mutation_receipts",
        "covenant_version_attachment_provenance",
        "covenant_heads",
        "covenant_versions",
        "covenant_entries",
        "covenant_key_epochs",
        "covenant_search_outbox",
        "covenant_search_documents",
    ];

    /// <summary>Counts index entries for a word the seeded head actually contains.</summary>
    private const string IndexedTokenQuery =
        "SELECT COUNT(*) FROM covenant_fts WHERE covenant_fts MATCH 'brief';";

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_reset_deletes_every_canonical_family_table()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        foreach (string table in FamilyTables)
        {

            Assert.Equal(1, await fixture.CountAsync(table, Token));

        }

        // Asserted before as well as after. An external-content index answers a bare COUNT(*) from its
        // content table, so "no rows afterwards" is true the moment the projection is empty whether or
        // not a single token was ever removed; only a MATCH reads the index's own pages, and only a
        // MATCH that found something first proves the later zero meant anything.
        Assert.Equal(1, await fixture.ScalarLongAsync(IndexedTokenQuery, Token));

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        foreach (string table in FamilyTables)
        {

            Assert.Equal(0, await fixture.CountAsync(table, Token));

        }

        // The projection's tokens leave with it. An external-content FTS5 index keeps its own pages,
        // so a delete that skipped the after-delete trigger would leave every indexed word behind a
        // row that no longer exists.
        Assert.Equal(0, await fixture.ScalarLongAsync(IndexedTokenQuery, Token));

    }

    [Fact]
    public async Task A_reset_creates_exactly_one_new_dataset_and_restarts_the_search_state()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        Guid? before = await fixture.ReadDatasetGenerationAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        Assert.Equal(1, await fixture.CountAsync("covenant_state", Token));

        Guid? after = await fixture.ReadDatasetGenerationAsync(Token);

        Assert.NotNull(before);

        Assert.NotNull(after);

        Assert.NotEqual(before, after);

        // The reported generation is the one that committed, not a second identity minted beside it.
        Assert.Equal(after, applied.Value);

        Assert.Equal(
            0,
            await fixture.ScalarLongAsync("SELECT CanonicalSearchSequence FROM covenant_state;", Token));

        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT NextSearchRowId FROM covenant_state;", Token));

        Assert.Null(await fixture.ScalarStringAsync(
            "SELECT AppliedDatasetGeneration FROM covenant_state;",
            Token));

        Assert.Null(await fixture.ScalarStringAsync("SELECT AppliedSearchSequence FROM covenant_state;", Token));

        Assert.Null(await fixture.ScalarStringAsync("SELECT RebuildTargetSequence FROM covenant_state;", Token));

        Assert.Null(await fixture.ScalarStringAsync("SELECT RebuildCursor FROM covenant_state;", Token));

        // An accelerator holding a generation that no longer exists is behind by definition.
        Assert.Equal(
            (long)CovenantFtsRebuildState.FullRebuildRequired,
            await fixture.ScalarLongAsync("SELECT RebuildStateCode FROM covenant_state;", Token));

    }

    /// <summary>
    /// The stamped dataset is the one the launch preselected, not one this transaction chose.
    /// </summary>
    /// <remarks>
    /// A transaction that minted its own generation could not be asked afterwards whether it had
    /// committed: a replaced family looks identical whether this operation replaced it or something
    /// else did, so a recovery pass would have to guess — and the guess it would be forced into is
    /// "run the destructive statement again". Committing to the target first is what turns that into
    /// a comparison.
    /// </remarks>
    [Fact]
    public async Task A_reset_stamps_the_preselected_target_rather_than_a_generation_of_its_own()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync(
            """
            UPDATE covenant_state
            SET AcceleratorEpoch = 7, KeyReclamationEpoch = 11, EnvelopeKeyEpoch = 13
            WHERE StateKey = 1;
            """,
            Token);

        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            preselected,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        Assert.Equal(preselected.TargetDatasetGeneration, await fixture.ReadDatasetGenerationAsync(Token));

        // Read back rather than returned from memory: the value the caller gets has to be the one the
        // committed row carries, or it is a claim about the transaction rather than about the database.
        Assert.Equal(preselected.TargetDatasetGeneration, applied.Value);

        Assert.Equal(8, await fixture.ScalarLongAsync("SELECT AcceleratorEpoch FROM covenant_state;", Token));

        Assert.Equal(12, await fixture.ScalarLongAsync("SELECT KeyReclamationEpoch FROM covenant_state;", Token));

        Assert.Equal(14, await fixture.ScalarLongAsync("SELECT EnvelopeKeyEpoch FROM covenant_state;", Token));

    }

    /// <summary>
    /// A database that is not the one the plan was made against is refused, and nothing is deleted.
    /// </summary>
    /// <remarks>
    /// The refusal is a zero-row update rather than a read-then-compare, because a comparison made
    /// before the statement leaves a window the statement itself does not close. Deleting the family
    /// and then discovering the singleton had moved would already have destroyed the evidence that
    /// said so.
    /// </remarks>
    [Theory]
    [InlineData("generation")]
    [InlineData("accelerator")]
    [InlineData("keyReclamation")]
    [InlineData("envelopeKey")]
    public async Task A_canonical_erasure_refuses_a_source_this_database_does_not_carry(string moved)
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        CovenantCanonicalDatasetTransition stale = moved switch
        {

            "generation" => preselected with { SourceDatasetGeneration = Guid.NewGuid() },

            "accelerator" => preselected with
            {
                SourceEpochs = preselected.SourceEpochs with
                {
                    AcceleratorEpoch = preselected.SourceEpochs.AcceleratorEpoch + 5,
                },
                TargetEpochs = preselected.TargetEpochs with
                {
                    AcceleratorEpoch = preselected.SourceEpochs.AcceleratorEpoch + 6,
                },
            },

            "keyReclamation" => preselected with
            {
                SourceEpochs = preselected.SourceEpochs with
                {
                    KeyReclamationEpoch = preselected.SourceEpochs.KeyReclamationEpoch + 5,
                },
                TargetEpochs = preselected.TargetEpochs with
                {
                    KeyReclamationEpoch = preselected.SourceEpochs.KeyReclamationEpoch + 6,
                },
            },

            _ => preselected with
            {
                SourceEpochs = preselected.SourceEpochs with
                {
                    EnvelopeKeyEpoch = preselected.SourceEpochs.EnvelopeKeyEpoch + 5,
                },
                TargetEpochs = preselected.TargetEpochs with
                {
                    EnvelopeKeyEpoch = preselected.SourceEpochs.EnvelopeKeyEpoch + 6,
                },
            },

        };

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            stale,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsFailure, moved);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        // The whole statement is one transaction, so a refused stamp takes every deletion with it.
        Assert.Equal(
            preselected.SourceDatasetGeneration,
            await fixture.ReadDatasetGenerationAsync(Token));

    }

    /// <summary>
    /// A pair no launch could have committed to is refused before anything is opened or drained.
    /// </summary>
    /// <remarks>
    /// Transposed target epochs are the case worth naming. Compared as a set they look correct — every
    /// target is one more than some source — and a family replaced under them would then be verified
    /// against a counter it does not belong to.
    /// </remarks>
    [Fact]
    public async Task A_canonical_erasure_refuses_a_transition_whose_targets_are_transposed()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync(
            """
            UPDATE covenant_state
            SET AcceleratorEpoch = 7, KeyReclamationEpoch = 11, EnvelopeKeyEpoch = 13
            WHERE StateKey = 1;
            """,
            Token);

        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        CovenantCanonicalDatasetTransition transposed = preselected with
        {
            TargetEpochs = new CovenantOfflineTransitionEpochsV1(
                preselected.TargetEpochs.EnvelopeKeyEpoch,
                preselected.TargetEpochs.KeyReclamationEpoch,
                preselected.TargetEpochs.AcceleratorEpoch),
        };

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            transposed,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        // The refusal lands before the drain, so the fixture's own handle is still open and the
        // singleton it can still read is the untouched source.
        Assert.Equal(
            preselected.SourceDatasetGeneration,
            await fixture.ReadDatasetGenerationAsync(Token));

    }

    [Fact]
    public async Task A_reset_advances_the_accelerator_and_envelope_epochs_rather_than_restarting_them()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        // Deliberately not the seeded value of one. An erasure that wrote a literal would still pass
        // against a singleton that happened to start there.
        await fixture.ExecuteAsync(
            """
            UPDATE covenant_state
            SET AcceleratorEpoch = 7, KeyReclamationEpoch = 11, EnvelopeKeyEpoch = 13
            WHERE StateKey = 1;
            """,
            Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        // A turn that captured an epoch before the reset must not find that value still valid against
        // the dataset that replaced it, which is why these three restart nowhere.
        Assert.Equal(8, await fixture.ScalarLongAsync("SELECT AcceleratorEpoch FROM covenant_state;", Token));

        Assert.Equal(12, await fixture.ScalarLongAsync("SELECT KeyReclamationEpoch FROM covenant_state;", Token));

        Assert.Equal(14, await fixture.ScalarLongAsync("SELECT EnvelopeKeyEpoch FROM covenant_state;", Token));

        // The master key itself is not this operation's to move.
        Assert.Equal(
            1,
            await fixture.ScalarLongAsync("SELECT EnvelopeMasterKeyVersion FROM covenant_state;", Token));

    }

    [Fact]
    public async Task A_reset_moves_both_cleanup_cursors_to_the_core_owner_deletion_sequences()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        // Four seeded to the journal: Campaign events at 1, 3 and 4, and one Session event at 2. The
        // two kinds have different maxima on purpose, so a cursor pair filled from one query, or
        // transposed, cannot pass.
        Assert.Equal(
            4,
            await fixture.ScalarLongAsync(
                "SELECT AppliedCampaignSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;",
                Token));

        Assert.Equal(
            2,
            await fixture.ScalarLongAsync(
                "SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;",
                Token));

        // A dataset with no rows owes no sweep.
        Assert.Equal(
            0,
            await fixture.ScalarLongAsync(
                "SELECT FullSweepRequired FROM capability_cleanup_state WHERE CapabilityFamilyCode = 1;",
                Token));

        Assert.Equal(
            4,
            await fixture.ScalarLongAsync("SELECT AppliedCampaignDeletionSequence FROM covenant_state;", Token));

        Assert.Equal(
            2,
            await fixture.ScalarLongAsync("SELECT AppliedSessionDeletionSequence FROM covenant_state;", Token));

    }

    [Fact]
    public async Task A_reset_preserves_core_nonrevocable_disclosure_receipts_and_joined_disclosure_state()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        // A disclosure that already happened is not an artifact: erasing the local copy of a byte a
        // provider received does not un-receive it.
        Assert.Equal(1, await fixture.CountAsync("external_disclosure_receipts", Token));

        Assert.Equal(1, await fixture.CountAsync("disclosure_subject_state", Token));

        Assert.Equal(1, await fixture.CountAsync("disclosure_subject_aggregates", Token));

        Assert.Equal(1, await fixture.CountAsync("external_disclosure_state", Token));

        Assert.Equal(
            2,
            await fixture.ScalarLongAsync("SELECT RevocabilityCode FROM external_disclosure_receipts;", Token));

        Assert.Equal(3, await fixture.ScalarLongAsync("SELECT JoinedCount FROM external_disclosure_state;", Token));

        // The subject's own chain digest is evidence of an ordered sequence; a reset that rewrote it
        // would be rewriting the proof rather than the data.
        Assert.Equal(
            Convert.ToHexString(CovenantRetainedEvidence.Digest(0xA0).Bytes),
            await fixture.ScalarStringAsync(
                "SELECT hex(DisclosureChainDigest) FROM disclosure_subject_state;",
                Token));

    }

    [Fact]
    public async Task A_reset_retains_every_campaign_path_marker_and_both_host_tools_markers()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantRetainedEvidenceSnapshot before = await fixture.CaptureRetainedAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        await fixture.AssertRetainedAsync(before, Token);

    }

    [Fact]
    public async Task A_healthy_catalog_factory_erasure_retains_the_same_markers()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantRetainedEvidenceSnapshot before = await fixture.CaptureRetainedAsync(Token);

        Result<Guid> applied = await CreateService(fixture)
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        await fixture.AssertRetainedAsync(before, Token);

    }

    /// <summary>
    /// The one assertion the other three paths read too, so the retention set has a single owner.
    /// </summary>
    [Fact]
    public void Ordinary_reset_retains_the_marker_set_no_production_path_may_delete() =>
        CovenantRetainedEvidence.AssertNoProductionPathDeletesRetainedEvidence();

    [Fact]
    public async Task A_healthy_catalog_factory_erasure_preserves_schema_objects_and_their_metadata()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        Result<Guid> applied = await CreateService(fixture)
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        Assert.True(await fixture.ObjectExistsAsync("covenant_entries", "table", Token));

        Assert.True(await fixture.ObjectExistsAsync("covenant_heads", "table", Token));

        Assert.True(await fixture.ObjectExistsAsync("covenant_entries_guard_delete", "trigger", Token));

        Assert.True(await fixture.ObjectExistsAsync("covenant_search_documents", "table", Token));

        Assert.Equal(1, await fixture.CountAsync("grimoire_feature_schemas", Token));

        // Core owns two tables whose names begin covenant_. An erasure that selected its work by name
        // prefix rather than by the canonical manifest would take the installation's authority row and
        // its repair journal with the family.
        Assert.Equal(1, await fixture.CountAsync("covenant_authority_state", Token));

        Assert.True(await fixture.ObjectExistsAsync("covenant_schema_repair_intents", "table", Token));

    }

    /// <summary>
    /// An absent canonical singleton is refused rather than reseeded, on both arms.
    /// </summary>
    /// <remarks>
    /// The factory arm used to mint a singleton when it found none, which was right while the erasure
    /// discovered its own state: a catalog whose optional tier had been reinstalled underneath it
    /// still had to be resettable. An offline transition cannot be in that position. Its launch
    /// records a source epoch above zero for all three counters, so a launch structurally cannot
    /// describe a database with no singleton — and an arm that reseeded anyway would stamp epochs no
    /// launch committed to, against a catalog whose damage the operator would never be told about.
    /// </remarks>
    [Fact]
    public async Task A_healthy_catalog_factory_erasure_refuses_a_canonical_singleton_that_is_absent()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_search_documents;", Token);

        // Preselected while the singleton is still there, so the pair handed to the transaction is a
        // coherent one and the refusal below is attributable to the missing row rather than to it.
        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_state;", Token);

        Result<Guid> applied = await CreateService(fixture)
            .ApplyAsync(
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
                preselected,
                CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
                Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        // Nothing was reseeded, and nothing was erased on the way to deciding not to.
        Assert.Equal(0, await fixture.CountAsync("covenant_state", Token));

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

    }

    /// <summary>
    /// The exclusive acquisition has no <see cref="SqliteBusyRetry"/> wrap, so any handle the
    /// drain could not close turns the erasure into a hard refusal after the first attempt's own
    /// busy budget elapses. <see cref="CovenantDisclosureJournal"/> wraps its identical
    /// <c>BEGIN IMMEDIATE</c> in the retry helper; this transaction did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rollback alone does not free the lock this test needs to race. <see cref="ApplyAsync"/>'s own
    /// remarks say why: "an exclusive maintenance handle cannot take its lock while any other handle
    /// holds the same database open." <c>ExclusiveMaintenance</c> connections run
    /// <c>PRAGMA locking_mode=EXCLUSIVE</c>, so the erasure's acquisition is blocked by any other
    /// connection that is merely open, independent of whether that connection has an active
    /// transaction. The holder below therefore closes - the "racing handle finishing its own close"
    /// <see cref="ApplyOnConnectionAsync"/>'s remarks describe - rather than only rolling back; the
    /// connection string turns pooling off first, or <c>Close()</c> would return the still-open
    /// sqlite3 handle to the pool instead of releasing it, which is exactly why
    /// <see cref="CovenantConnectionDrain"/> carries its own <c>ClearExactPoolAfterClose</c>.
    /// </para>
    /// <para>
    /// The bound one attempt can spend blocked inside <c>BeginTransaction(deferred: false)</c> is not
    /// the 5000 ms <c>CovenantSqliteConnectionInitializer.BusyTimeoutMs</c> PRAGMA - that only governs
    /// SQLite's own native busy-handler. <c>Microsoft.Data.Sqlite</c> layers a second, longer wait on
    /// top, bounded by <see cref="SqliteConnection.DefaultTimeout"/> (30 s by default), so a real
    /// racing holder can take up to 30 real seconds to surface as SQLITE_BUSY at all. Waiting that out
    /// on every test run would make this suite unusable, so <see cref="ThrottledV3ConnectionFactory"/>
    /// lowers both bounds - the same two settings <c>CampaignRepository.AddAsync</c> already lowers
    /// for its own <c>BEGIN IMMEDIATE</c> - on the erasure's connection only, and only after the real
    /// factory's <c>InitializeAsync</c> has already verified the production 5000 ms policy.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ApplyAsync_retries_a_racing_exclusive_acquisition_rather_than_failing_on_the_first_busy()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        string holderConnectionString = new SqliteConnectionStringBuilder(fixture.Connection.ConnectionString)
        {
            Pooling = false,
        }.ToString();

        // Disposed explicitly at the release point below, not just rolled back - see the remarks
        // above for why an open connection blocks the erasure regardless of its transaction state.
        // Kept as "await using" too: disposing an already-disposed connection/transaction is a safe
        // no-op, and this way the holder still closes if an assertion above the release point throws.
        await using SqliteConnection holder = new(holderConnectionString);

        await holder.OpenAsync(Token);

        await using SqliteTransaction holderTransaction = holder.BeginTransaction(deferred: false);

        CovenantCanonicalErasureTransaction service = new(
            new ThrottledV3ConnectionFactory(fixture.V3Connections()),
            CovenantSqliteConnectionInitializer.Instance,
            fixture.Drain,
            TimeProvider.System);

        // IsSuccess alone cannot tell a genuine retry from a holder that happened to
        // release before the erasure's first attempt ever ran - this suite would pass vacuously
        // either way. RetryingForTesting is invoked once per busy exception SqliteBusyRetry actually
        // caught, so counting its calls (Interlocked, since it runs on the Task.Run thread below) and
        // inspecting what it was called with is the only way to assert a retry happened at all.
        int retryCount = 0;

        service.RetryingForTesting = (attempt, exception, retryingToken) =>
        {

            _ = Interlocked.Increment(ref retryCount);

            Assert.True(attempt >= 1, $"Expected a positive attempt number; observed {attempt}.");

            SqliteException busy = Assert.IsType<SqliteException>(exception);

            Assert.True(
                busy.SqliteErrorCode is 5 or 6,
                $"Expected the retried exception to be SQLITE_BUSY/LOCKED; observed code {busy.SqliteErrorCode}.");

            return ValueTask.CompletedTask;

        };

        // Task.Run, not a bare call: ApplyAsync's prefix (drain, connection open, the secure-delete
        // read-back) can complete synchronously all the way into the blocking BeginTransaction call,
        // which would otherwise run inline on this thread and only return control here after the
        // whole throttled busy wait had already elapsed - starving the Task.Delay below of any chance
        // to race it at all.
        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        Task<Result<Guid>> applying = Task.Run(() => service.ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            preselected,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token));

        // The throttled per-attempt bound is ~1s (ThrottledV3ConnectionFactory); holding past it
        // before closing the holder proves the C# retry - not the first attempt's own wait - is what
        // makes a later attempt succeed.
        await Task.Delay(TimeSpan.FromMilliseconds(1_800), Token);

        await holderTransaction.RollbackAsync(Token);

        await holderTransaction.DisposeAsync();

        await holder.DisposeAsync();

        Result<Guid> applied = await applying;

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        Assert.True(retryCount >= 1, "Expected at least one retry; the holder may have released before the first attempt ever raced it.");

    }

    [Fact]
    public async Task A_reset_refuses_when_the_canonical_singleton_is_absent()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_search_documents;", Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_state;", Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        // A reset reseeds nothing. Minting a singleton for a catalog that lost one would answer schema
        // damage by inventing a dataset identity nothing else in the installation agrees with.
        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

    }

    [Fact]
    public async Task A_healthy_catalog_factory_erasure_reseeds_the_accelerator_secure_delete_configuration()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync(
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 0);",
            Token);

        Result<Guid> applied = await CreateService(fixture)
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        // The accelerator is the one Covenant object holding plaintext-derived tokens in its own
        // pages, so an index left without secure delete keeps retired words legible in freed pages.
        Assert.Equal(
            1,
            await fixture.ScalarLongAsync("SELECT v FROM covenant_fts_config WHERE k = 'secure-delete';", Token));

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CampaignPathMutation)]
    [InlineData(CovenantExclusiveOperation.BackupRestore)]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
    public async Task Only_a_reset_or_a_healthy_catalog_factory_erasure_may_enter_the_transaction(
        CovenantExclusiveOperation operation)
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(
            operation,
            await fixture.PreselectAsync(Token),
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, applied.Error.Code);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

    }

    [Fact]
    public async Task A_connection_that_cannot_prove_secure_delete_refuses_before_it_deletes_anything()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantCanonicalErasureTransaction service = new(
            new CovenantV3MaintenanceTestConnectionFactory(fixture.Connections(), new UnprovenSecureDeleteInitializer()),
            new UnprovenSecureDeleteInitializer(),
            fixture.Drain,
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        // A freed page that stays legible is the whole reason this proof exists, so the refusal has to
        // come before the first delete rather than after it.
        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

        Assert.Equal(1, await fixture.CountAsync("covenant_versions", Token));

    }

    [Fact]
    public async Task The_family_is_drained_before_the_exclusive_connection_is_opened()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        List<string> steps = [];

        CovenantCanonicalErasureTransaction service = new(
            new CovenantV3MaintenanceTestConnectionFactory(
                new RecordingConnectionFactory(fixture.Connections(), steps),
                CovenantSqliteConnectionInitializer.Instance),
            CovenantSqliteConnectionInitializer.Instance,
            new RecordingConnectionDrain(fixture.Drain, steps),
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        // Order rather than presence. An exclusive maintenance connection cannot take its lock while
        // another handle holds the same database open, so a drain performed afterwards would be a
        // drain of something the erasure already failed to work around.
        Assert.Equal(["drain", "open"], steps);

        Assert.Equal(ConnectionState.Closed, fixture.Connection.State);

    }

    [Fact]
    public async Task A_drain_that_cannot_close_a_handle_refuses_without_touching_the_family()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantCanonicalErasureTransaction service = new(
            fixture.V3Connections(),
            CovenantSqliteConnectionInitializer.Instance,
            new FailingConnectionDrain(),
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, await fixture.PreselectAsync(Token), CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure), Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

    }

    [Fact]
    public async Task A_failure_inside_the_transaction_leaves_the_whole_family_standing()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        CovenantCanonicalDatasetTransition preselected = await fixture.PreselectAsync(Token);

        // The source guard is a zero-row update, which is evaluated after every delete in the same
        // transaction has already run. Nothing else in this suite can prove the deletes and the state
        // write are one transaction rather than nine — and a source that moved between the plan and
        // the effect is the exact condition the guard exists for, so the two facts are proved together.
        await fixture.ExecuteAsync(
            "UPDATE covenant_state SET AcceleratorEpoch = AcceleratorEpoch + 3 WHERE StateKey = 1;",
            Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(
            CovenantExclusiveOperation.CovenantReset,
            preselected,
            CovenantV3MaintenanceTestAuthority.Mint(CovenantV3MaintenancePurpose.CanonicalErasure),
            Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        foreach (string table in FamilyTables)
        {

            Assert.Equal(1, await fixture.CountAsync(table, Token));

        }

    }

    private static CovenantCanonicalErasureTransaction CreateService(CovenantCanonicalErasureFixture fixture) =>
        new(
            fixture.V3Connections(),
            CovenantSqliteConnectionInitializer.Instance,
            fixture.Drain,
            TimeProvider.System);

    /// <summary>
    /// Applies every part of the real policy except the one this suite is standing over.
    /// </summary>
    /// <remarks>
    /// The refusal has to belong to the erasure rather than to the initializer. The real initializer
    /// throws when its own read-back fails, and a suite that only exercised that would prove the
    /// initializer works and say nothing about whether the transaction checks before it deletes.
    /// </remarks>
    private sealed class UnprovenSecureDeleteInitializer : ICovenantSqliteConnectionInitializer
    {

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "PRAGMA secure_delete=OFF;";

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new InvalidOperationException(
                "A transaction that could not prove secure delete must refuse before it authorizes anything.");

        public CovenantSqliteAuthorizationScope AuthorizeRestoreStagingManagedAuthoritySanitization(
            RestoreStagingManagedAuthoritySanitizationCapability authority,
            RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingConnectionFactory(
        IDesignTimeGrimoireConnectionFactory inner,
        List<string> steps) : IDesignTimeGrimoireConnectionFactory
    {

        public string DatabasePath => inner.DatabasePath;

        public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
        {

            steps.Add("open");

            return inner.OpenAsync(cancellationToken);

        }

        public Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "The canonical erasure transaction opens no read-only handle.");

        // The canonical transaction opens no side file and no read-only handle. Delegating rather
        // than throwing would let it grow one without this suite's ordering log noticing.
        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "The canonical erasure transaction opens no sidecar-free read-only handle.");

        public Task<SqliteConnection> OpenSideFileAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The canonical erasure transaction opens no side file.");

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The canonical erasure transaction attaches no side file.");

    }

    private sealed class RecordingConnectionDrain(ICovenantConnectionDrain inner, List<string> steps)
        : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) => inner.Register(connection);

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            inner.ClearExactPoolAfterClose(connection);

        public Task<Result> DrainAsync(CancellationToken cancellationToken)
        {

            steps.Add("drain");

            return inner.DrainAsync(cancellationToken);

        }

    }

    private sealed class FailingConnectionDrain : ICovenantConnectionDrain
    {

        public IDisposable Register(SqliteConnection connection) => throw new NotSupportedException();

        public Result ClearExactPoolAfterClose(SqliteConnection connection) =>
            throw new NotSupportedException();

        public Task<Result> DrainAsync(CancellationToken cancellationToken) =>
            Task.FromResult(
                Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.MaintenanceFailed,
                        "A Covenant connection handle did not close.")));

    }

    /// <summary>
    /// Lowers <see cref="SqliteConnection.DefaultTimeout"/> and <c>busy_timeout</c> on the canonical
    /// erasure's connection only, and only after the real factory's own <c>InitializeAsync</c> has
    /// already opened, policed, and verified it - so a racing holder resolves inside a test-sized
    /// window instead of the real 30 second per-attempt bound
    /// <see cref="ApplyAsync_retries_a_racing_exclusive_acquisition_rather_than_failing_on_the_first_busy"/>
    /// would otherwise have to wait out.
    /// </summary>
    private sealed class ThrottledV3ConnectionFactory(ICovenantV3MaintenanceConnectionFactory inner)
        : ICovenantV3MaintenanceConnectionFactory
    {

        public async Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CanonicalErasureAsync(
            CovenantV3MaintenanceCapability capability,
            CancellationToken cancellationToken)
        {

            Result<ICovenantV3MaintenanceConnectionLease> opened =
                await inner.OpenV3CanonicalErasureAsync(capability, cancellationToken).ConfigureAwait(false);

            if (opened.IsFailure)
            {

                return opened;

            }

            SqliteConnection connection = opened.Value.Connection;

            connection.DefaultTimeout = 1;

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "PRAGMA busy_timeout=1000;";

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return opened;

        }

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3WalTruncationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3WalTruncationAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3VacuumAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3VacuumAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportSourceAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3ExportSourceAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3ExportVerificationAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3PostReplaceJournalRestoreAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3PostReplaceJournalRestoreAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3AcceleratorInitializationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3AcceleratorInitializationAsync(capability, cancellationToken);

        public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CandidateReopenVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
            inner.OpenV3CandidateReopenVerificationAsync(capability, cancellationToken);

        public Task<Result> AttachV3ExportStagingAsync(ICovenantV3MaintenanceConnectionLease exportLease, CancellationToken cancellationToken) =>
            inner.AttachV3ExportStagingAsync(exportLease, cancellationToken);

    }

}
