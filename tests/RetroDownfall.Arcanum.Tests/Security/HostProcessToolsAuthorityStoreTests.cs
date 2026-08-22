using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The durable half of the transition, against the real encrypted core table and its CHECK
/// constraints.
/// </summary>
/// <remarks>
/// The service suite fakes this store so it can drive orderings a real database makes hard to
/// reproduce. This suite exists so that the shapes those fakes accept are the shapes the schema
/// actually permits: the row's clean-or-complete constraint, the compare-and-swap, and the epoch
/// advance are all asserted here against SQLCipher rather than against an in-memory record.
/// </remarks>
public sealed class HostProcessToolsAuthorityStoreTests
{

    private const string Installation = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    [Fact]
    public async Task A_seeded_clean_row_reads_back_with_no_taint_evidence()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        Result<HostProcessToolsAuthorityRow> row = await store.ReadAsync(CancellationToken.None);

        Assert.True(row.IsSuccess);

        Assert.Equal(Installation, row.Value.InstallationIdentity);

        Assert.Equal(CovenantHostToolsState.Clean, row.Value.State);

        Assert.Null(row.Value.TransitionId);

        Assert.Null(row.Value.TaintMasterKeyVersion);

        Assert.Null(row.Value.TaintFingerprint);

    }

    [Fact]
    public async Task The_pending_commit_pins_the_taint_to_the_key_in_force()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        HostProcessToolsAuthorityRow before = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.True((await store.CommitPendingAsync(before, Transition, CancellationToken.None)).IsSuccess);

        HostProcessToolsAuthorityRow pending = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.Equal(CovenantHostToolsState.PendingHostToolsTaint, pending.State);

        Assert.Equal(Transition, pending.TransitionId);

        Assert.Equal(before.CurrentMasterKeyVersion, pending.TaintMasterKeyVersion);

        Assert.Equal(before.CurrentMasterKeyFingerprint, pending.TaintFingerprint);

        // The epoch does not move until the transition is proven complete.
        Assert.Equal(before.AuthorityEpoch, pending.AuthorityEpoch);

    }

    [Fact]
    public async Task The_pending_commit_stores_and_reads_a_canonical_unsigned_taint_version()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        HostProcessToolsAuthorityRow clean = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.True((await store.CommitPendingAsync(clean, Transition, CancellationToken.None)).IsSuccess);

        await using SqliteCommand inspect = database.Connection.CreateCommand();

        inspect.CommandText = "SELECT TaintTimeMasterVersion FROM covenant_authority_state WHERE StateKey = 1;";

        byte[] stored = Assert.IsType<byte[]>(await inspect.ExecuteScalarAsync(CancellationToken.None));

        Assert.Equal(8, stored.Length);

        Assert.Equal((ulong)clean.CurrentMasterKeyVersion, BinaryPrimitives.ReadUInt64BigEndian(stored));

        Result<HostProcessToolsAuthorityRow> read = await store.ReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess);

        Assert.Equal((ulong)clean.CurrentMasterKeyVersion, read.Value.TaintMasterKeyVersion);

    }

    [Fact]
    public async Task A_legacy_positive_integer_taint_version_reads_across_the_full_signed_range()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await using (SqliteCommand compatibility = database.Connection.CreateCommand())
        {

            compatibility.CommandText = "PRAGMA ignore_check_constraints = ON;";

            _ = await compatibility.ExecuteNonQueryAsync(CancellationToken.None);

        }

        await using SqliteCommand legacy = database.Connection.CreateCommand();

        legacy.CommandText = """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 3,
                TransitionId = $transition,
                TaintTimeMasterVersion = $taintVersion,
                TaintFingerprint = $fingerprint
            WHERE StateKey = 1;
            """;

        _ = legacy.Parameters.AddWithValue("$transition", Transition.ToString("D").ToUpperInvariant());

        _ = legacy.Parameters.AddWithValue("$taintVersion", long.MaxValue);

        _ = legacy.Parameters.AddWithValue("$fingerprint", Fingerprint().Bytes);

        _ = await legacy.ExecuteNonQueryAsync(CancellationToken.None);

        await using (SqliteCommand compatibility = database.Connection.CreateCommand())
        {

            compatibility.CommandText = "PRAGMA ignore_check_constraints = OFF;";

            _ = await compatibility.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Result<HostProcessToolsAuthorityRow> read = await new HostProcessToolsAuthorityStore(database.Connection)
            .ReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess);

        Assert.Equal((ulong)long.MaxValue, read.Value.TaintMasterKeyVersion);

    }

    [Fact]
    public async Task Fresh_schema_accepts_only_positive_eight_byte_taint_versions()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        await using SqliteCommand valid = database.Connection.CreateCommand();

        valid.CommandText = """
            UPDATE covenant_authority_state
            SET HostToolsStateCode = 3,
                TransitionId = $transition,
                TaintTimeMasterVersion = X'FFFFFFFFFFFFFFFF',
                TaintFingerprint = $fingerprint
            WHERE StateKey = 1;
            """;

        _ = valid.Parameters.AddWithValue("$transition", Transition.ToString("D").ToUpperInvariant());

        _ = valid.Parameters.AddWithValue("$fingerprint", Fingerprint().Bytes);

        Assert.Equal(1, await valid.ExecuteNonQueryAsync(CancellationToken.None));

        Result<HostProcessToolsAuthorityRow> read = await new HostProcessToolsAuthorityStore(database.Connection)
            .ReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess);

        Assert.Equal(ulong.MaxValue, read.Value.TaintMasterKeyVersion);

        foreach (string malformed in new[]
        {
            "X'0000000000000000'",
            "X'01'",
            "'0000000000000001'",
            "1",
        })
        {

            await using SqliteCommand damage = database.Connection.CreateCommand();

            damage.CommandText = $"""
                UPDATE covenant_authority_state
                SET TaintTimeMasterVersion = {malformed}
                WHERE StateKey = 1;
                """;

            _ = await Assert.ThrowsAsync<SqliteException>(
                () => damage.ExecuteNonQueryAsync(CancellationToken.None));

        }

    }

    [Fact]
    public async Task The_taint_commit_advances_both_epochs_exactly_once()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        HostProcessToolsAuthorityRow clean = (await store.ReadAsync(CancellationToken.None)).Value;

        _ = await store.CommitPendingAsync(clean, Transition, CancellationToken.None);

        HostProcessToolsAuthorityRow pending = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.True((await store.CommitTaintedAsync(pending, Transition, CancellationToken.None)).IsSuccess);

        HostProcessToolsAuthorityRow tainted = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, tainted.State);

        Assert.Equal(clean.AuthorityEpoch + 1, tainted.AuthorityEpoch);

        Assert.Equal(clean.RecoveryEnvelopeEpoch + 1, tainted.RecoveryEnvelopeEpoch);

        // Replaying the same commit against the stale row it already consumed changes nothing.
        Result replay = await store.CommitTaintedAsync(pending, Transition, CancellationToken.None);

        Assert.True(replay.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, replay.Error.Code);

        Assert.Equal(tainted.AuthorityEpoch, (await store.ReadAsync(CancellationToken.None)).Value.AuthorityEpoch);

    }

    [Fact]
    public async Task A_stale_epoch_loses_its_compare_and_swap()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        HostProcessToolsAuthorityRow stale = (await store.ReadAsync(CancellationToken.None)).Value
            with
        { AuthorityEpoch = 99 };

        Result committed = await store.CommitPendingAsync(stale, Transition, CancellationToken.None);

        Assert.True(committed.IsFailure);

        Assert.Equal(
            CovenantHostToolsState.Clean,
            (await store.ReadAsync(CancellationToken.None)).Value.State);

    }

    [Fact]
    public async Task A_foreign_transition_identity_cannot_complete_or_compensate_a_pending_row()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        HostProcessToolsAuthorityRow clean = (await store.ReadAsync(CancellationToken.None)).Value;

        _ = await store.CommitPendingAsync(clean, Transition, CancellationToken.None);

        HostProcessToolsAuthorityRow pending = (await store.ReadAsync(CancellationToken.None)).Value;

        Guid foreign = Guid.Parse("11112222-3333-4444-5555-666677778888");

        Assert.True((await store.CommitTaintedAsync(pending, foreign, CancellationToken.None)).IsFailure);

        Assert.True((await store.CompensateToCleanAsync(pending, foreign, CancellationToken.None)).IsFailure);

        Assert.Equal(
            CovenantHostToolsState.PendingHostToolsTaint,
            (await store.ReadAsync(CancellationToken.None)).Value.State);

    }

    [Fact]
    public async Task The_inventory_counts_protected_labels_and_treats_an_absent_optional_table_as_zero()
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        Result<HostProcessToolsProtectedInventory> empty = await store
            .InventoryProtectedStateAsync(CancellationToken.None);

        Assert.True(empty.IsSuccess);

        Assert.True(empty.Value.IsEmpty);

        await database.InstallCoreObjectsAsync(["artifact_sensitivity"], CancellationToken.None);

        await InsertLabelAsync(database, CancellationToken.None);

        Result<HostProcessToolsProtectedInventory> occupied = await store
            .InventoryProtectedStateAsync(CancellationToken.None);

        Assert.False(occupied.Value.IsEmpty);

        Assert.Equal(1, occupied.Value.ProtectedArtifactCount);

    }

    /// <summary>
    /// The digest columns are declared BLOB, which in a non-STRICT table is SQLite's "no affinity" — a
    /// TEXT value is stored as TEXT and still satisfies <c>length(...) = 32</c>. Reading one back as
    /// <c>byte[]</c> then throws, so the startup gate has to see a malformed-row failure it can turn
    /// into a blocked disposition rather than an exception that escapes past it.
    /// </summary>
    [Theory]
    [InlineData("CurrentMasterKeyFingerprint")]
    [InlineData("TaintFingerprint")]
    public async Task A_digest_column_stored_as_text_reads_back_as_a_malformed_row(string column)
    {

        await using CovenantSchemaScratchDatabase database = await CreateAsync();

        HostProcessToolsAuthorityStore store = new(database.Connection);

        // TaintFingerprint is NULL while the row is clean, so take the transition pending first.
        HostProcessToolsAuthorityRow clean = (await store.ReadAsync(CancellationToken.None)).Value;

        Assert.True((await store.CommitPendingAsync(clean, Transition, CancellationToken.None)).IsSuccess);

        await using SqliteCommand damage = database.Connection.CreateCommand();

        damage.CommandText = $"UPDATE covenant_authority_state SET {column} = $text WHERE StateKey = 1;";

        _ = damage.Parameters.AddWithValue("$text", "0123456789abcdef0123456789abcdef");

        _ = await damage.ExecuteNonQueryAsync(CancellationToken.None);

        Result<HostProcessToolsAuthorityRow> row = await store.ReadAsync(CancellationToken.None);

        Assert.True(row.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, row.Error.Code);

    }

    private static async Task<CovenantSchemaScratchDatabase> CreateAsync()
    {

        CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
            .CreateAsync(CancellationToken.None);

        try
        {

            await database.InstallCoreObjectsAsync(["covenant_authority_state"], CancellationToken.None);

            await using SqliteCommand seed = database.Connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO covenant_authority_state (
                    StateKey,
                    InstallationIdentity,
                    AuthorityEpoch,
                    CurrentMasterKeyVersion,
                    CurrentMasterKeyFingerprint,
                    RecoveryEnvelopeEpoch,
                    HostToolsStateCode,
                    TaintTimeMasterVersion,
                    TaintFingerprint,
                    TransitionId,
                    UpdatedAtUtc)
                VALUES (1, $identity, 1, 4, $fingerprint, 1, 1, NULL, NULL, NULL, '2026-08-16T00:00:00.0000000+00:00');
                """;

            _ = seed.Parameters.AddWithValue("$identity", Installation);

            _ = seed.Parameters.AddWithValue("$fingerprint", Fingerprint().Bytes);

            _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

            return database;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    private static async Task InsertLabelAsync(
        CovenantSchemaScratchDatabase database,
        CancellationToken cancellationToken)
    {

        ArtifactSensitivityLabel label = new(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            Guid.NewGuid(),
            sessionId: null,
            campaignId: null,
            turnId: null,
            artifactRevision: 1,
            Fingerprint(),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId,
                ArtifactKindCode,
                ArtifactId,
                SensitivityCode,
                ProvenanceModeCode,
                ExactGenerationIds,
                GenerationBloom,
                SessionId,
                CampaignId,
                TurnId,
                ArtifactRevision,
                ArtifactContentDigest,
                SensitivityDigest,
                ProducingPlanDigest,
                ProducingAdmissionDigest,
                ProducingMaintenanceReceiptDigest,
                ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES (
                $labelId, 1, $artifactId, 1, 1, $generations, NULL, NULL, NULL, NULL,
                1, $contentDigest, $sensitivityDigest, NULL, NULL, NULL, $labelDigest, $createdAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$labelId", label.LabelId.ToString().ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$artifactId", label.ArtifactId.ToString().ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$generations", label.Provenance.ToCanonicalExactBytes());

        _ = command.Parameters.AddWithValue("$contentDigest", label.ArtifactContentDigest.Bytes);

        _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes);

        _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes);

        _ = command.Parameters.AddWithValue("$createdAtUtc", "2026-08-16T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static CovenantDigest Fingerprint()
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(index + 1);

        }

        return new CovenantDigest(bytes);

    }

}
