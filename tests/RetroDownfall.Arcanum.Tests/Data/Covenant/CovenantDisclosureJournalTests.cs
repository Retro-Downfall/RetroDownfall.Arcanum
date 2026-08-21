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

    private static CovenantDisclosureDraft Draft(byte effectSeed) =>
        new(
            Installation,
            CovenantDisclosureSubjectKind.Turn,
            TurnId,
            CovenantTask6Fixture.D(effectSeed),
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(80),
            Sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            1_700_000_000_000);

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
