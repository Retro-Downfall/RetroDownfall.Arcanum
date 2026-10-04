using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The acknowledgement that commits before bytes leave Arcanum (§10.13).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CovenantDisclosureJournalTests
{

    private static readonly Guid Installation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid TurnId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static readonly Guid BootId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

    private static readonly string[] CoreObjects =
    [
        "external_disclosure_receipts",
        "disclosure_subject_state",
        "external_disclosure_receipts_guard_delete",
        "external_disclosure_receipts_guard_update",
        "disclosure_subject_state_guard_delete",
        "external_disclosure_state",
    ];

    [Fact]
    public async Task AcknowledgeAsync_AllocatesOrdinalsAndAdvancesTheSubjectChain()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureTransactionWriter journal = Journal();

        Result<CovenantDisclosureReceipt> first = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        Result<CovenantDisclosureReceipt> second = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(2),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(second.IsSuccess, second.Error.Message);
        Assert.Equal(1ul, first.Value.AllocatedSubjectOrdinal);
        Assert.Equal(2ul, second.Value.AllocatedSubjectOrdinal);
        Assert.Equal(2, await CountReceiptsAsync(fixture));
        Assert.Equal(2, await ScalarAsync(fixture, "ProviderAttemptCount"));
        Assert.Equal(2, await ScalarAsync(fixture, "ExternalEffectCount"));
        Assert.Equal(2, await ScalarAsync(fixture, "LastAllocatedOrdinal"));
    }

    [Fact]
    public async Task AcknowledgeAsync_ReplaysOneEffectIdentityWithoutASecondPhysicalDisclosure()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureTransactionWriter journal = Journal();

        Result<CovenantDisclosureReceipt> first = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        Result<CovenantDisclosureReceipt> replay = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(first.Value.AllocatedSubjectOrdinal, replay.Value.AllocatedSubjectOrdinal);
        Assert.Equal(first.Value.Digest, replay.Value.Digest);
        Assert.Equal(1, await CountReceiptsAsync(fixture));
        Assert.Equal(1, await ScalarAsync(fixture, "ExternalEffectCount"));
    }

    [Fact]
    public async Task AcknowledgeAsync_CountsAToolUseSeparatelyFromAProviderDispatch()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureTransactionWriter journal = Journal();

        _ = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        _ = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(2),
            CovenantDisclosureEffectCategory.McpToolUse,
            Sensitivity,
            CancellationToken.None);

        Assert.Equal(2, await ScalarAsync(fixture, "ExternalEffectCount"));
        Assert.Equal(1, await ScalarAsync(fixture, "ProviderAttemptCount"));
    }

    [Fact]
    public async Task AcknowledgeAsync_folds_each_receipt_into_its_bucket_in_the_same_transaction()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureTransactionWriter journal = Journal();

        Result<CovenantDisclosureReceipt> first = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        Result<CovenantDisclosureReceipt> second = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(2),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(second.IsSuccess, second.Error.Message);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, CancellationToken.None));

        Assert.Equal(
            (CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                CovenantDisclosureCountKind.Exact, true, 2ul, 638_355_968_000_000_000L),
            (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.EverOccurred, bucket.Count,
                bucket.MaximumTimestamp));

        // The fold rebuilds each receipt from its stored row, so the Bloom it folds is only this one if
        // the row carries everything the live receipt's digest committed to.
        Assert.Equal(Or(first.Value.EvidenceBloom, second.Value.EvidenceBloom), bucket.EvidenceBloom.ToArray());

        Assert.Equal(2, await ScalarAsync(fixture, "LastFoldedOrdinal"));

        Assert.Equal("2023-11-14T22:13:20.0000000Z", await UpdatedAtAsync(fixture, 1, 2));

    }

    [Fact]
    public async Task AcknowledgeAsync_replay_folds_nothing_twice()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureTransactionWriter journal = Journal();

        _ = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Result<CovenantDisclosureReceipt> replay = await journal.AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, CancellationToken.None));

        Assert.Equal((CovenantDisclosureCountKind.Exact, 1ul), (bucket.CountKind, bucket.Count));

        Assert.Equal(1, await ScalarAsync(fixture, "LastFoldedOrdinal"));

    }

    [Fact]
    public async Task AcknowledgeAsync_tool_use_folds_into_the_locally_revocable_process_bucket()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        Result<CovenantDisclosureReceipt> tool = await Journal().AcknowledgeAsync(
            fixture.Connection,
            Draft(1, CovenantEgressDestination.Process, CovenantDisclosureRevocability.LocallyRevocable),
            CovenantDisclosureEffectCategory.McpToolUse,
            Sensitivity,
            CancellationToken.None);

        Assert.True(tool.IsSuccess, tool.Error.Message);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, CancellationToken.None));

        Assert.Equal(
            (CovenantEgressDestination.Process, CovenantDisclosureRevocability.LocallyRevocable,
                CovenantDisclosureCountKind.Exact, 1ul),
            (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.Count));

        Assert.Null(await UpdatedAtAsync(fixture, 1, 2));

    }

    /// <summary>
    /// A live fold weakens its buckets exactly when the tail it folds is longer than the one receipt
    /// it just wrote. Tail sizes one, two and three pin that boundary: a tail of one is this receipt
    /// alone and stays exact, and any longer tail carries receipts a build without the fold wrote.
    /// </summary>
    [Theory]
    [InlineData(1, CovenantDisclosureCountKind.Exact)]
    [InlineData(2, CovenantDisclosureCountKind.LowerBound)]
    [InlineData(3, CovenantDisclosureCountKind.LowerBound)]
    public async Task AcknowledgeAsync_a_pre_fold_backlog_is_folded_with_the_next_receipt_as_a_lower_bound(
        int tail,
        CovenantDisclosureCountKind expectedKind)
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        for (byte ordinal = 1; ordinal < tail; ordinal++)
        {

            await PreFoldDisclosureHistory.InsertAsync(fixture.Connection, Draft(ordinal), ordinal, CancellationToken.None);

        }

        Result<CovenantDisclosureReceipt> next = await Journal().AcknowledgeAsync(
            fixture.Connection,
            Draft((byte)tail),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.Equal((ulong)tail, next.Value.AllocatedSubjectOrdinal);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, CancellationToken.None));

        // Receipts a build without the fold wrote are counted now, but only as a lower bound: nothing
        // proves they are the whole of that history.
        Assert.Equal(
            (CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                expectedKind, (ulong)tail),
            (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.Count));

        Assert.Equal(
            Or([.. Enumerable.Range(1, tail).Select(
                static ordinal => new CovenantDisclosureReceipt(Draft((byte)ordinal), (ulong)ordinal).EvidenceBloom)]),
            bucket.EvidenceBloom.ToArray());

        Assert.Equal(tail, await ScalarAsync(fixture, "LastFoldedOrdinal"));

    }

    [Fact]
    public async Task AcknowledgeAsync_a_failed_fold_rolls_the_receipt_back()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        await ExecuteAsync(
            fixture,
            """
            CREATE TEMP TRIGGER refuse_disclosure_fold
            BEFORE INSERT ON main.external_disclosure_state
            BEGIN
                SELECT RAISE(ABORT, 'x');
            END;
            """);

        _ = await Assert.ThrowsAsync<SqliteException>(async () => await Journal().AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None));

        Assert.Equal(0, await CountReceiptsAsync(fixture));

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM disclosure_subject_state;",
                CancellationToken.None));

    }

    [Fact]
    public async Task AcknowledgeAsync_a_watermark_moved_during_the_fold_rolls_the_receipt_back()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        // Stands in for a second fold that advanced the watermark after this one read it. The
        // buckets this fold wrote would then count the same receipt twice, so the swap must refuse.
        await ExecuteAsync(
            fixture,
            """
            CREATE TEMP TRIGGER move_disclosure_watermark
            AFTER INSERT ON main.external_disclosure_state
            BEGIN
                UPDATE disclosure_subject_state SET LastFoldedOrdinal = LastAllocatedOrdinal;
            END;
            """);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Journal().AcknowledgeAsync(
            fixture.Connection,
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None));

        Assert.Equal(0, await CountReceiptsAsync(fixture));

        Assert.Empty(await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, CancellationToken.None));

    }

    [Fact]
    public async Task Exposure_reader_reports_live_receipts_without_seeded_state()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        for (byte seed = 1; seed <= 3; seed++)
        {

            Result<CovenantDisclosureReceipt> acknowledged = await Journal().AcknowledgeAsync(
                fixture.Connection,
                Draft(seed),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                Sensitivity,
                CancellationToken.None);

            Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        }

        Assert.Equal(
            new CovenantDisclosureExposure(3, CovenantDisclosureCountKind.Exact),
            (await new CovenantDisclosureExposureReader().ReadWithinAsync(
                fixture.Connection,
                transaction: null,
                CancellationToken.None)).Value);

    }

    [Fact]
    public async Task Exposure_reader_counts_an_unfolded_backlog_inside_the_callers_snapshot()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        await PreFoldDisclosureHistory.InsertAsync(fixture.Connection, Draft(1), 1, CancellationToken.None);

        await PreFoldDisclosureHistory.InsertAsync(fixture.Connection, Draft(2), 2, CancellationToken.None);

        await using SqliteTransaction snapshot = fixture.Connection.BeginTransaction(deferred: true);

        Result<CovenantDisclosureExposure> exposure = await new CovenantDisclosureExposureReader().ReadWithinAsync(
            fixture.Connection,
            snapshot,
            CancellationToken.None);

        Assert.Equal(new CovenantDisclosureExposure(2, CovenantDisclosureCountKind.LowerBound), exposure.Value);

    }

    [Fact]
    public async Task Exposure_reader_folds_only_nonrevocable_rows_by_checked_sum_and_kind_join()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        CovenantDisclosureExposureReader reader = new();

        Assert.Equal(
            new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact),
            (await reader.ReadWithinAsync(
                fixture.Connection,
                transaction: null,
                CancellationToken.None)).Value);

        await InsertExposureAsync(
            fixture,
            destination: 1,
            CovenantDisclosureRevocability.LocallyRevocable,
            CovenantDisclosureCountKind.LowerBound,
            attempts: 41);

        Assert.Equal(
            new CovenantDisclosureExposure(0, CovenantDisclosureCountKind.Exact),
            (await reader.ReadWithinAsync(
                fixture.Connection,
                transaction: null,
                CancellationToken.None)).Value);

        await InsertExposureAsync(
            fixture,
            destination: 2,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.Exact,
            attempts: 3);

        Assert.Equal(
            new CovenantDisclosureExposure(3, CovenantDisclosureCountKind.Exact),
            (await reader.ReadWithinAsync(
                fixture.Connection,
                transaction: null,
                CancellationToken.None)).Value);

        await InsertExposureAsync(
            fixture,
            destination: 3,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.LowerBound,
            attempts: 5);

        Assert.Equal(
            new CovenantDisclosureExposure(8, CovenantDisclosureCountKind.LowerBound),
            (await reader.ReadWithinAsync(
                fixture.Connection,
                transaction: null,
                CancellationToken.None)).Value);

    }

    [Fact]
    public async Task Exposure_reader_refuses_malformed_codes_and_checked_overflow_content_free()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);

        CovenantDisclosureExposureReader reader = new();

        await InsertExposureAsync(
            fixture,
            destination: 1,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.Exact,
            attempts: long.MaxValue);

        await InsertExposureAsync(
            fixture,
            destination: 2,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.Exact,
            attempts: 1);

        Result<CovenantDisclosureExposure> overflow = await reader.ReadWithinAsync(
            fixture.Connection,
            transaction: null,
            CancellationToken.None);

        Assert.True(overflow.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, overflow.Error.Code);

        Assert.DoesNotContain(long.MaxValue.ToString(), overflow.Error.Message, StringComparison.Ordinal);

        await ExecuteAsync(fixture, "DELETE FROM external_disclosure_state;");

        await ExecuteAsync(
            fixture,
            """
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES (
                8, 2, 99, 1, 4, 1,
                CAST(x'01' || zeroblob(31) AS BLOB),
                '2026-08-20T00:00:00.0000000Z');
            PRAGMA ignore_check_constraints = OFF;
            """);

        Result<CovenantDisclosureExposure> malformed = await reader.ReadWithinAsync(
            fixture.Connection,
            transaction: null,
            CancellationToken.None);

        Assert.True(malformed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, malformed.Error.Code);

        Assert.DoesNotContain("99", malformed.Error.Message, StringComparison.Ordinal);

    }

    private static ICovenantDisclosureTransactionWriter Journal() =>
        new CovenantDisclosureTransactionWriter(BootId);

    private static readonly GenerationProvenance Provenance =
        GenerationProvenance.CreateExact([CovenantTask6Fixture.DatasetGeneration]);

    private static readonly ProviderCallSensitivity Sensitivity = new(
        ContentSensitivity.CovenantDerived,
        Provenance,
        CovenantDigests.Sensitivity(new SensitivityDigestInput(
            ContentSensitivity.CovenantDerived,
            Provenance.Mode,
            Provenance.ExactGenerationIds,
            Provenance.BloomBits)));

    private static CovenantDisclosureDraft Draft(
        byte effectSeed,
        CovenantEgressDestination destination = CovenantEgressDestination.Provider,
        CovenantDisclosureRevocability revocability = CovenantDisclosureRevocability.Nonrevocable) =>
        new(
            Installation,
            CovenantDisclosureSubjectKind.Turn,
            TurnId,
            CovenantTask6Fixture.D(effectSeed),
            destination,
            revocability,
            CovenantTask6Fixture.D(80),
            Sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            1_700_000_000_000);

    private static byte[] Or(params byte[][] blooms)
    {

        byte[] result = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        foreach (byte[] bloom in blooms)
        {

            for (int index = 0; index < result.Length; index++)
            {

                result[index] |= bloom[index];

            }

        }

        return result;

    }

    private static async Task<string?> UpdatedAtAsync(
        CovenantCanonicalFixture fixture,
        int destination,
        int revocability)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            SELECT UpdatedAtUtc
            FROM external_disclosure_state
            WHERE DestinationCode = $destination AND RevocabilityCode = $revocability;
            """;

        _ = command.Parameters.AddWithValue("$destination", destination);

        _ = command.Parameters.AddWithValue("$revocability", revocability);

        return await command.ExecuteScalarAsync(CancellationToken.None) as string;

    }

    private static Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string column) =>
        CovenantCapacityFixture.ScalarAsync(
            fixture,
            $"SELECT {column} FROM disclosure_subject_state;",
            CancellationToken.None);

    private static Task<long> CountReceiptsAsync(CovenantCanonicalFixture fixture) =>
        CovenantCapacityFixture.ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM external_disclosure_receipts;",
            CancellationToken.None);

    private static Task InsertExposureAsync(
        CovenantCanonicalFixture fixture,
        int destination,
        CovenantDisclosureRevocability revocability,
        CovenantDisclosureCountKind countKind,
        long attempts) =>
        ExecuteAsync(
            fixture,
            """
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES (
                $destination, $revocability, $kind, 1, $attempts, 1,
                CAST(x'01' || zeroblob(31) AS BLOB), '2026-08-20T00:00:00.0000000Z');
            """,
            ("$destination", destination),
            ("$revocability", (long)revocability),
            ("$kind", (long)countKind),
            ("$attempts", attempts));

    private static async Task ExecuteAsync(
        CovenantCanonicalFixture fixture,
        string sql,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

}
