using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, Token);

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
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, Token);

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

    [Fact]
    public async Task A_healthy_catalog_factory_erasure_reseeds_a_canonical_singleton_that_is_absent()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_search_documents;", Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_state;", Token);

        Result<Guid> applied = await CreateService(fixture)
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

        await fixture.ReopenAsync(Token);

        Assert.Equal(1, await fixture.CountAsync("covenant_state", Token));

        Assert.Equal(applied.Value, await fixture.ReadDatasetGenerationAsync(Token));

        // A reseeded singleton starts where a fresh installation starts.
        Assert.Equal(1, await fixture.ScalarLongAsync("SELECT AcceleratorEpoch FROM covenant_state;", Token));

        Assert.Equal(
            4,
            await fixture.ScalarLongAsync("SELECT AppliedCampaignDeletionSequence FROM covenant_state;", Token));

    }

    [Fact]
    public async Task A_reset_refuses_when_the_canonical_singleton_is_absent()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_search_documents;", Token);

        await fixture.ExecuteAsync("DELETE FROM covenant_state;", Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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
            .ApplyAsync(CovenantExclusiveOperation.HealthyCatalogFactoryErasure, Token);

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

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(operation, Token);

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
            fixture.Connections(),
            new UnprovenSecureDeleteInitializer(),
            fixture.Drain,
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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
            new RecordingConnectionFactory(fixture.Connections(), steps),
            CovenantSqliteConnectionInitializer.Instance,
            new RecordingConnectionDrain(fixture.Drain, steps),
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

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
            fixture.Connections(),
            CovenantSqliteConnectionInitializer.Instance,
            new FailingConnectionDrain(),
            TimeProvider.System);

        Result<Guid> applied = await service.ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        Assert.Equal(1, await fixture.CountAsync("covenant_entries", Token));

    }

    [Fact]
    public async Task A_failure_inside_the_transaction_leaves_the_whole_family_standing()
    {

        await using CovenantCanonicalErasureFixture fixture = await CovenantCanonicalErasureFixture.CreateAsync(Token);

        await fixture.SeedAsync(Token);

        // A saturated accelerator epoch is refused by the singleton's own update trigger, which fires
        // after every delete in the same transaction has already run. Nothing else in this suite can
        // prove the deletes and the state write are one transaction rather than nine.
        await fixture.ExecuteAsync(
            "UPDATE covenant_state SET AcceleratorEpoch = 9223372036854775807 WHERE StateKey = 1;",
            Token);

        Result<Guid> applied = await CreateService(fixture).ApplyAsync(CovenantExclusiveOperation.CovenantReset, Token);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, applied.Error.Code);

        await fixture.ReopenAsync(Token);

        foreach (string table in FamilyTables)
        {

            Assert.Equal(1, await fixture.CountAsync(table, Token));

        }

    }

    private static CovenantCanonicalErasureTransaction CreateService(CovenantCanonicalErasureFixture fixture) =>
        new(
            fixture.Connections(),
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
        ICovenantMaintenanceConnectionFactory inner,
        List<string> steps) : ICovenantMaintenanceConnectionFactory
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

}
