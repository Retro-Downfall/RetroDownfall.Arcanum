using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
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
    ];

    [Fact]
    public async Task AcknowledgeAsync_AllocatesOrdinalsAndAdvancesTheSubjectChain()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            CancellationToken.None,
            coreObjects: CoreObjects);
        ICovenantDisclosureJournal journal = Journal(fixture);

        Result<CovenantDisclosureReceipt> first = await journal.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        Result<CovenantDisclosureReceipt> second = await journal.AcknowledgeAsync(
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
        ICovenantDisclosureJournal journal = Journal(fixture);

        Result<CovenantDisclosureReceipt> first = await journal.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        Result<CovenantDisclosureReceipt> replay = await journal.AcknowledgeAsync(
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
        ICovenantDisclosureJournal journal = Journal(fixture);

        _ = await journal.AcknowledgeAsync(
            Draft(1),
            CovenantDisclosureEffectCategory.ProviderDispatch,
            Sensitivity,
            CancellationToken.None);
        _ = await journal.AcknowledgeAsync(
            Draft(2),
            CovenantDisclosureEffectCategory.McpToolUse,
            Sensitivity,
            CancellationToken.None);

        Assert.Equal(2, await ScalarAsync(fixture, "ExternalEffectCount"));
        Assert.Equal(1, await ScalarAsync(fixture, "ProviderAttemptCount"));
    }

    private static ICovenantDisclosureJournal Journal(CovenantCanonicalFixture fixture) =>
        new CovenantDisclosureJournal(
            new FixedCovenantConnectionSource(fixture.Connection),
            BootId);

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

}
