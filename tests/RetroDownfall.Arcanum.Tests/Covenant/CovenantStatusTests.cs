using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// What <c>memory status</c> tells an operator their installation holds.
/// </summary>
/// <remarks>
/// "You have no standing preferences" is the most damaging sentence this surface can say wrongly, and
/// it is the sentence an empty count array produces. These run against the real encrypted canonical
/// tier so the number and the storage cannot agree about a world neither of them read.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantStatusTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Status_reports_the_entries_the_installation_actually_holds()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CovenantOperationGateFixture.CampaignOne, "Status", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run build commands from the repository root.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CovenantOperationGateFixture.CampaignOne,
            "preference.style",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "Prefers terse commit subjects.",
            Token);

        CovenantStatusDto status = await StatusAsync(fixture);

        Assert.Equal(2, status.Counts.Length);

        Assert.Contains(
            status.Counts,
            count => count is
            {
                Scope: CovenantScope.Global,
                Lane: CovenantLane.Confirmed,
                Lifecycle: CovenantLifecycle.Set,
                Count: 1,
            });

        Assert.Contains(
            status.Counts,
            count => count is
            {
                Scope: CovenantScope.Campaign,
                Lane: CovenantLane.Proposed,
                Lifecycle: CovenantLifecycle.Set,
                Count: 1,
            });

        // The rendered totals are what an operator compares against the per-section ceiling, so they
        // have to be the real cost of the real content rather than a placeholder beside real counts.
        Assert.True(status.GlobalConfirmedRenderedBytes > 0);

        Assert.True(status.MaxCampaignProposedRenderedBytes > 0);

        Assert.Equal(0, status.MaxCampaignConfirmedRenderedBytes);

        Assert.Equal(CovenantLimits.MaxGlobalConfirmedRenderedBytes, status.RenderedByteCeilingPerSection);

    }

    [Fact]
    public async Task An_empty_installation_is_reported_as_available_and_holding_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantStatusDto status = await StatusAsync(fixture);

        // "Nothing stored" and "could not be read" are different sentences and only one of them is an
        // emergency. An empty count is only honest while the tier reports itself healthy.
        Assert.True(status.Available);

        // The distinction the whole field exists for: this zero is a measurement, so it reads as
        // emptiness. Nothing else in the block can say that.
        Assert.Equal(CovenantCensusReadState.Read, status.Census);

        Assert.Empty(status.Counts);

        Assert.Null(status.DegradationCode);

    }

    [Fact]
    public async Task An_unreadable_tier_reports_zero_beside_health_that_says_why()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run build commands from the repository root.",
            Token);

        FakeCovenantAvailability availability = new();

        availability.Mutate(static current => current with
        {
            Canonical = CovenantCapabilityState.Unavailable,
            CanonicalDiagnosticCode = "canonical-unavailable",
        });

        CovenantStatusDto status = await StatusAsync(fixture, availability);

        // The count is zero because nothing could be counted, and the operator has an entry. Refused
        // is the mechanism, not a summary: the gate declined the installation capability over a tier
        // it cannot serve, so the store was never asked. A test that only checked the empty array
        // would pass just as well if the census had run and lost the rows.
        Assert.Equal(CovenantCensusReadState.Refused, status.Census);

        Assert.Empty(status.Counts);

        Assert.False(status.Available);

        Assert.Equal("canonical-unavailable", status.DegradationCode);

    }

    /// <summary>
    /// A healthy tier whose capability was declined is never reported as an empty one.
    /// </summary>
    /// <remarks>
    /// This is the branch health cannot cover. An exclusive operation closes the scope and the gate
    /// then refuses every ordinary capability over it — while the canonical tier goes on reporting
    /// itself Healthy, because nothing about it broke. Every count and byte total is zero for the
    /// duration, and without the census state the operator is told, in the same breath, that their
    /// Covenant is available and that it holds nothing.
    /// </remarks>
    [Fact]
    public async Task A_closing_exclusive_operation_is_not_reported_as_an_installation_holding_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run build commands from the repository root.",
            Token);

        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantExclusiveLease closing = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Result<CovenantStatusDto> status = await Management(fixture, gate, availability).StatusAsync(Token);

        Assert.True(status.IsSuccess, status.IsFailure ? status.Error.Message : string.Empty);

        // Healthy, enabled, and zero — which is exactly what an empty installation reports. Only the
        // census state separates the two, and this is the one that must not read as emptiness.
        Assert.True(status.Value.Available);

        Assert.Null(status.Value.DegradationCode);

        Assert.Empty(status.Value.Counts);

        Assert.NotEqual(CovenantCensusReadState.Read, status.Value.Census);

    }

    [Fact]
    public async Task Status_does_not_hold_the_installation_capability_after_it_answers()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        ICovenantManagementService management = Management(fixture, gate, availability);

        _ = (await management.StatusAsync(Token)).Value;

        // Status acquires its own all-scopes capability rather than borrowing one, so it is the only
        // thing that can release it. An exclusive operation is what proves the release: it drains
        // outstanding leases first, so a leaked read would stall the drain until it timed out — and a
        // reset or a restore blocked by a read-only status call is a failure nobody would attribute
        // to reading status.
        Result<CovenantExclusiveLease> drained = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : string.Empty);

        await drained.Value.DisposeAsync();

    }

    private static async Task<CovenantStatusDto> StatusAsync(
        CovenantCanonicalFixture fixture,
        FakeCovenantAvailability? availability = null)
    {

        FakeCovenantAvailability resolved = availability ?? new FakeCovenantAvailability();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(resolved);

        Result<CovenantStatusDto> status = await Management(fixture, gate, resolved).StatusAsync(Token);

        Assert.True(status.IsSuccess, status.IsFailure ? status.Error.Message : string.Empty);

        return status.Value;

    }

    private static ICovenantManagementService Management(
        CovenantCanonicalFixture fixture,
        ICovenantOperationGate gate,
        ICovenantAvailability availability) =>
        new CovenantManagementService(
            fixture.Store,
            new CovenantLinker(),
            gate,
            availability,
            new UnreachableEnvelopeCodec());

    /// <summary>A codec that fails loudly, because status issues and accepts no envelope.</summary>
    private sealed class UnreachableEnvelopeCodec : ICovenantEnvelopeCodec
    {

        public CovenantEnvelopeKeySnapshot KeySnapshot =>
            throw new NotSupportedException("A status read touches no envelope.");

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null) =>
            throw new NotSupportedException("A status read issues no envelope.");

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            throw new NotSupportedException("A status read accepts no envelope.");

    }

}
