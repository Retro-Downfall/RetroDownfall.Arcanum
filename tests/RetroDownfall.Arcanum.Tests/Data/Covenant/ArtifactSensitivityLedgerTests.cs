using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The information-flow ledger every derived-output producer writes its label through.
/// </summary>
/// <remarks>
/// These assertions are about what the ledger refuses. A label that can be rewritten, downgraded, or
/// skipped is worth nothing to the readers that trust it, so the interesting cases are the second
/// write, the clean relabel, and the write whose transaction rolls back (§10.12).
/// </remarks>
public sealed class ArtifactSensitivityLedgerTests
{

    private static readonly Guid Session = Guid.Parse("0A1B2C3D-4E5F-4A6B-8C9D-0E1F2A3B4C5D");

    private static readonly Guid Campaign = Guid.Parse("1B2C3D4E-5F60-4B7C-8D9E-1F2A3B4C5D6E");

    private static readonly Guid GenerationOne = Guid.Parse("2C3D4E5F-6071-4C8D-9EAF-2A3B4C5D6E7F");

    private static readonly Guid GenerationTwo = Guid.Parse("3D4E5F60-7182-4D9E-8FA0-3B4C5D6E7F80");

    [Fact]
    public async Task An_untainted_write_persists_no_label_and_no_projection()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Result<LabeledArtifactWriteReceipt> receipt = await fixture.Ledger.LabelAsync(
            Clean(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(receipt.IsSuccess);

        Assert.Equal(ContentSensitivity.None, receipt.Value.Sensitivity);

        Assert.Null(receipt.Value.LabelId);

        Assert.Equal(0, await fixture.CountLabelsAsync());

        Assert.False((await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None))
            .Value.IsTainted);

    }

    [Fact]
    public async Task A_tainted_write_persists_its_label_and_advances_the_session_projection()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Result<LabeledArtifactWriteReceipt> receipt = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, artifactId, GenerationOne),
            CancellationToken.None);

        Assert.True(receipt.IsSuccess);

        Assert.Equal(ContentSensitivity.CovenantDerived, receipt.Value.Sensitivity);

        Assert.NotNull(receipt.Value.LabelId);

        Result<ArtifactSensitivityLabel?> label = await fixture.Ledger.TryReadLabelAsync(
            SensitiveArtifactKind.AssistantEntry,
            artifactId,
            CancellationToken.None);

        Assert.NotNull(label.Value);

        Assert.Equal(receipt.Value.LabelDigest, label.Value!.LabelDigest);

        Assert.Contains(GenerationOne, label.Value.Provenance.ExactGenerationIds);

        SessionSensitivityProjection projection =
            (await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None)).Value;

        Assert.True(projection.IsTainted);

        Assert.Equal(1, projection.TaintedArtifactCount);

        Assert.Equal(1, projection.Revision);

    }

    [Fact]
    public async Task Repeating_the_exact_same_label_is_an_idempotent_replay()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        DerivedArtifactWrite write = Tainted(SensitiveArtifactKind.Summary, artifactId, GenerationOne);

        Result<LabeledArtifactWriteReceipt> first = await fixture.Ledger.LabelAsync(write, CancellationToken.None);

        Result<LabeledArtifactWriteReceipt> second = await fixture.Ledger.LabelAsync(write, CancellationToken.None);

        Assert.True(second.IsSuccess);

        Assert.Equal(first.Value.LabelId, second.Value.LabelId);

        Assert.Equal(1, await fixture.CountLabelsAsync());

        // The replay must not double-count the Session projection either.
        Assert.Equal(
            1,
            (await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None))
                .Value.TaintedArtifactCount);

    }

    [Fact]
    public async Task A_tainted_artifact_can_never_be_relabelled_clean()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        _ = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.ToolArtifact, artifactId, GenerationOne),
            CancellationToken.None);

        Result<LabeledArtifactWriteReceipt> downgrade = await fixture.Ledger.LabelAsync(
            Clean(SensitiveArtifactKind.ToolArtifact, artifactId),
            CancellationToken.None);

        Assert.True(downgrade.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, downgrade.Error.Code);

        Assert.Equal(
            ContentSensitivity.CovenantDerived,
            (await fixture.Ledger.TryReadLabelAsync(
                SensitiveArtifactKind.ToolArtifact,
                artifactId,
                CancellationToken.None)).Value!.Sensitivity);

    }

    [Fact]
    public async Task A_different_label_for_the_same_artifact_is_refused_rather_than_added_beside_it()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        _ = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Saga, artifactId, GenerationOne),
            CancellationToken.None);

        Result<LabeledArtifactWriteReceipt> conflicting = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Saga, artifactId, GenerationTwo),
            CancellationToken.None);

        Assert.True(conflicting.IsFailure);

        Assert.Equal(1, await fixture.CountLabelsAsync());

    }

    [Fact]
    public async Task The_same_artifact_identity_under_two_kinds_is_two_independent_labels()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        Assert.True((await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, artifactId, GenerationOne),
            CancellationToken.None)).IsSuccess);

        Assert.True((await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.SearchProjection, artifactId, GenerationOne),
            CancellationToken.None)).IsSuccess);

        Assert.Equal(2, await fixture.CountLabelsAsync());

    }

    [Fact]
    public async Task A_rolled_back_caller_transaction_leaves_no_label_behind()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        await using (SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false))
        {

            Result<LabeledArtifactWriteReceipt> written = await ArtifactSensitivityLedger.WriteWithinAsync(
                fixture.Connection,
                transaction,
                Tainted(SensitiveArtifactKind.SessionTitle, artifactId, GenerationOne),
                CancellationToken.None);

            Assert.True(written.IsSuccess);

            await transaction.RollbackAsync(CancellationToken.None);

        }

        Assert.Equal(0, await fixture.CountLabelsAsync());

        Assert.Null((await fixture.Ledger.TryReadLabelAsync(
            SensitiveArtifactKind.SessionTitle,
            artifactId,
            CancellationToken.None)).Value);

    }

    [Fact]
    public async Task Two_generations_in_one_session_merge_into_one_projection_fingerprint()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        _ = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationOne),
            CancellationToken.None);

        CovenantDigest afterFirst =
            (await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None))
            .Value.GenerationProvenanceDigest;

        _ = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Summary, Guid.NewGuid(), GenerationTwo),
            CancellationToken.None);

        SessionSensitivityProjection merged =
            (await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None)).Value;

        Assert.Equal(2, merged.TaintedArtifactCount);

        Assert.NotEqual(afterFirst, merged.GenerationProvenanceDigest);

        // The fingerprint is order-insensitive: the same two generations in the other order agree.
        await using LedgerFixture reversed = await LedgerFixture.CreateAsync();

        _ = await reversed.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Summary, Guid.NewGuid(), GenerationTwo),
            CancellationToken.None);

        _ = await reversed.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationOne),
            CancellationToken.None);

        Assert.Equal(
            merged.GenerationProvenanceDigest,
            (await reversed.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None))
                .Value.GenerationProvenanceDigest);

    }

    [Fact]
    public async Task A_malformed_label_row_fails_closed_rather_than_reading_as_absent()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        Guid artifactId = Guid.NewGuid();

        _ = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Lexicon, artifactId, GenerationOne),
            CancellationToken.None);

        // Corrupt only the stored timestamp, which the row's own CHECK constraints do not police.
        await using (SqliteCommand corrupt = fixture.Connection.CreateCommand())
        {

            corrupt.CommandText = "UPDATE artifact_sensitivity SET CreatedAtUtc = 'not-a-timestamp';";

            _ = await corrupt.ExecuteNonQueryAsync(CancellationToken.None);

        }

        Result<ArtifactSensitivityLabel?> read = await fixture.Ledger.TryReadLabelAsync(
            SensitiveArtifactKind.Lexicon,
            artifactId,
            CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, read.Error.Code);

    }

    [Fact]
    public void An_untainted_write_cannot_claim_producing_covenant_evidence()
    {

        _ = Assert.Throws<ArgumentException>(() => new DerivedArtifactWrite(
            SensitiveArtifactKind.AssistantEntry,
            Guid.NewGuid(),
            Session,
            Campaign,
            null,
            1,
            Digest(3),
            ContentSensitivity.None,
            GenerationProvenance.CreateExact([]),
            producingMaintenanceReceiptDigest: Digest(9)));

    }

    [Fact]
    public void A_producing_plan_without_its_admission_is_refused()
    {

        _ = Assert.Throws<ArgumentException>(() => new DerivedArtifactWrite(
            SensitiveArtifactKind.AssistantEntry,
            Guid.NewGuid(),
            Session,
            Campaign,
            null,
            1,
            Digest(3),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([GenerationOne]),
            producingPlanDigest: Digest(4)));

    }

    private static DerivedArtifactWrite Clean(SensitiveArtifactKind kind, Guid artifactId) =>
        new(
            kind,
            artifactId,
            Session,
            Campaign,
            null,
            1,
            Digest(3),
            ContentSensitivity.None,
            GenerationProvenance.CreateExact([]));

    private static DerivedArtifactWrite Tainted(
        SensitiveArtifactKind kind,
        Guid artifactId,
        Guid generation) =>
        new(
            kind,
            artifactId,
            Session,
            Campaign,
            null,
            1,
            Digest(3),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([generation]));

    private static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

    /// <summary>
    /// The Covenant dispatch gate reads this projection on every session-backed turn, including on
    /// an installation whose Covenant is disabled. Opening the canonical connection latches this
    /// process as having held Covenant material — a one-way latch that forbids the offline
    /// host-tools transition — so a projection read over an always-present core table must take the
    /// accessor that does not latch. The read itself is required: previously tainted Session history
    /// keeps its protections after disablement, and untaintedness cannot be known without reading.
    /// </summary>
    [Fact]
    public async Task Reading_the_session_projection_never_takes_the_residence_latching_connection()
    {

        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        RecordingConnectionSource connections = new(fixture.Connection);

        ArtifactSensitivityLedger ledger = new(connections);

        Result<SessionSensitivityProjection> projection =
            await ledger.ReadSessionProjectionAsync(Session, CancellationToken.None);

        Assert.True(projection.IsSuccess);

        Assert.Equal(0, connections.LatchingCalls);

        Assert.Equal(1, connections.CoreCalls);

    }

    /// <summary>
    /// Answers the same connection from either accessor and records which one the caller asked for.
    /// </summary>
    /// <remarks>
    /// The real latch is a process-wide one-way static, so asserting on it directly would depend on
    /// whatever else the test process had already run. Which accessor was taken is the same fact,
    /// measured where it cannot be contaminated.
    /// </remarks>
    private sealed class RecordingConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {

        internal int LatchingCalls { get; private set; }

        internal int CoreCalls { get; private set; }

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {

            LatchingCalls++;

            return ValueTask.FromResult(connection);

        }

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken)
        {

            CoreCalls++;

            return ValueTask.FromResult(connection);

        }

    }

    /// <summary>
    /// A scratch Grimoire with only the core objects the ledger touches.
    /// </summary>
    /// <remarks>
    /// The Session row is seeded because both projections carry a real foreign key to it: a fixture
    /// that skipped it would pass while the production write failed on the constraint.
    /// </remarks>
    private sealed class LedgerFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private LedgerFixture(CovenantSchemaScratchDatabase database)
        {

            _database = database;

            Ledger = new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(database.Connection));

        }

        internal ArtifactSensitivityLedger Ledger { get; }

        internal SqliteConnection Connection => _database.Connection;

        internal static async Task<LedgerFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
                .CreateAsync(CancellationToken.None);

            try
            {

                await database.InstallCoreObjectsAsync(
                    ["Campaigns", "Sessions", "artifact_sensitivity", "session_sensitivity_state"],
                    CancellationToken.None);

                await using SqliteCommand seed = database.Connection.CreateCommand();

                seed.CommandText = """
                    INSERT INTO "Sessions" ("Id", "Title", "CreatedAt", "UpdatedAt")
                    VALUES ($sessionId, 'ledger', $now, $now);
                    """;

                _ = seed.Parameters.AddWithValue("$sessionId", Session.ToString().ToUpperInvariant());

                _ = seed.Parameters.AddWithValue("$now", "2026-08-16T00:00:00.0000000+00:00");

                _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

                return new LedgerFixture(database);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal Task<long> CountLabelsAsync() =>
            _database.ScalarLongAsync("SELECT COUNT(*) FROM artifact_sensitivity;", CancellationToken.None);

        public ValueTask DisposeAsync() => _database.DisposeAsync();

    }

}
