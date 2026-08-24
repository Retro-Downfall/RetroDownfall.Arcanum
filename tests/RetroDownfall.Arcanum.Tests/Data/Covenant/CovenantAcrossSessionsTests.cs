using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The sentence the Covenant exists for: a preference stated once is honored in the next session.
/// </summary>
/// <remarks>
/// Every other test in this area proves one component in isolation. This one refuses to seed storage
/// and refuses to stub a reader: the preference is written through the operator's real prepare and
/// commit path, and then a turn that shares nothing with the writing turn — different logical turn,
/// different Campaign — has to render it. Every component could pass its own suite and this could
/// still fail, which is exactly why it is written separately.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantAcrossSessionsTests
{

    private const string GlobalPreference = "Run build commands from the repository root.";

    private const string CampaignPreference = "This Campaign ships its migrations by hand.";

    private static readonly Guid SessionACampaign = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid SessionBCampaign = CovenantOperationGateFixture.CampaignTwo;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_global_preference_written_in_one_session_is_rendered_in_the_next()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(SessionACampaign, "Session A", Token);

        await fixture.AddCampaignAsync(SessionBCampaign, "Session B", Token);

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability, authority);

        await WriteGlobalAsync(fixture, gate);

        // Session B shares nothing with the turn that wrote: a different logical turn, and a different
        // Campaign entirely. A Global preference that only came back inside its author's own Campaign
        // would be a scoped preference wearing the wrong label.
        CovenantTurnContext session = await BeginTurnAsync(fixture, gate, availability, authority, SessionBCampaign);

        Assert.True(session.HasPlan);

        Assert.Contains(GlobalPreference, session.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        // It arrives as Confirmed, not as an agent suggestion. The operator stated it, so it ranks
        // with their own Codex rather than in the lane that gets evicted first.
        Assert.True(session.PlanContent.HasConfirmed);

        Assert.False(session.PlanContent.HasProposed);

    }

    [Fact]
    public async Task A_campaign_preference_does_not_follow_the_operator_into_another_campaign()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(SessionACampaign, "Session A", Token);

        await fixture.AddCampaignAsync(SessionBCampaign, "Session B", Token);

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability, authority);

        await WriteCampaignAsync(fixture, gate, SessionACampaign);

        CovenantTurnContext own = await BeginTurnAsync(fixture, gate, availability, authority, SessionACampaign);

        Assert.Contains(CampaignPreference, own.PlanContent.CampaignConfirmed, StringComparison.Ordinal);

        CovenantTurnContext other = await BeginTurnAsync(fixture, gate, availability, authority, SessionBCampaign);

        // Scope is a promise in both directions. A Campaign preference leaking across Campaigns is the
        // same failure as a Global one failing to travel, and it is the one an operator cannot see.
        Assert.DoesNotContain(CampaignPreference, other.PlanContent.CampaignConfirmed, StringComparison.Ordinal);

        Assert.DoesNotContain(CampaignPreference, other.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_retired_preference_stops_being_rendered_in_later_sessions()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(SessionBCampaign, "Session B", Token);

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability, authority);

        await WriteGlobalAsync(fixture, gate);

        CovenantTurnContext before = await BeginTurnAsync(fixture, gate, availability, authority, SessionBCampaign);

        Assert.Contains(GlobalPreference, before.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        await RetireGlobalAsync(fixture, gate);

        CovenantTurnContext after = await BeginTurnAsync(fixture, gate, availability, authority, SessionBCampaign);

        // Withdrawal has to travel exactly as far as the statement did. A preference an operator
        // retired and a model still honors is worse than one that never arrived.
        Assert.DoesNotContain(GlobalPreference, after.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

    }

    private static async Task WriteGlobalAsync(CovenantCanonicalFixture fixture, CovenantOperationGate gate)
    {

        CovenantMutationService service = Service(fixture);

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        await using (CovenantInstallationReadLease read =
            (await gate.AcquireInstallationReadAsync(Token)).Value)
        {

            Result<CovenantMutationPreflightDto> prepared = await service.PrepareSetAsync(
                new CovenantSetPrepareRequest(
                    CovenantScope.Global,
                    null,
                    "preference.builds",
                    GlobalPreference,
                    ExpectedRevision: 0,
                    mutationId,
                    Reactivate: false),
                read,
                Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            preflight = prepared.Value.PreflightToken;

        }

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> committed = await service.SetAsync(
            new CovenantSetRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                GlobalPreference,
                ExpectedRevision: 0,
                mutationId,
                Reactivate: false,
                preflight),
            write,
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

    }

    private static async Task WriteCampaignAsync(
        CovenantCanonicalFixture fixture,
        CovenantOperationGate gate,
        Guid campaignId)
    {

        CovenantMutationService service = Service(fixture);

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        await using (CovenantReadLease read =
            (await gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(campaignId), Token)).Value)
        {

            Result<CovenantMutationPreflightDto> prepared = await service.PrepareSetAsync(
                new CovenantSetPrepareRequest(
                    CovenantScope.Campaign,
                    campaignId,
                    "preference.migrations",
                    CampaignPreference,
                    ExpectedRevision: 0,
                    mutationId,
                    Reactivate: false),
                read,
                Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            preflight = prepared.Value.PreflightToken;

        }

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.ForCampaign(campaignId), Token)).Value;

        Result<CovenantMutationResultDto> committed = await service.SetAsync(
            new CovenantSetRequest(
                CovenantScope.Campaign,
                campaignId,
                "preference.migrations",
                CampaignPreference,
                ExpectedRevision: 0,
                mutationId,
                Reactivate: false,
                preflight),
            write,
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

    }

    private static async Task RetireGlobalAsync(CovenantCanonicalFixture fixture, CovenantOperationGate gate)
    {

        CovenantMutationService service = Service(fixture);

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        await using (CovenantInstallationReadLease read =
            (await gate.AcquireInstallationReadAsync(Token)).Value)
        {

            Result<CovenantMutationPreflightDto> prepared = await service.PrepareRetireAsync(
                new CovenantRetirePrepareRequest(
                    CovenantScope.Global,
                    null,
                    "preference.builds",
                    CovenantLane.Confirmed,
                    ExpectedRevision: 1,
                    mutationId),
                read,
                Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            preflight = prepared.Value.PreflightToken;

        }

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> committed = await service.RetireAsync(
            new CovenantRetireRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                CovenantLane.Confirmed,
                ExpectedRevision: 1,
                mutationId,
                preflight),
            write,
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

    }

    private static async Task<CovenantTurnContext> BeginTurnAsync(
        CovenantCanonicalFixture fixture,
        ICovenantOperationGate gate,
        ICovenantAvailability availability,
        FakeCovenantAuthorityProvider authority,
        Guid campaignId)
    {

        CovenantContextProvider provider = new(
            availability,
            gate,
            fixture.Store,
            new CovenantLinker());

        CovenantAuthoritySnapshot live = authority.Current!;

        Result<ArcanumInvocationContext> invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaignId),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,

            // The epoch is read off the live authority rather than pinned to a constant: the turn is
            // admitted against whatever authority the gate is actually running, which is the same
            // check a real turn passes.
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse(live.InstallationIdentity),
                live.RuntimeAuthorityGeneration,
                live.AuthorityEpoch));

        Assert.True(invocation.IsSuccess, invocation.IsFailure ? invocation.Error.Message : string.Empty);

        // A different logical turn every time. Two sessions never share one.
        Result<CovenantTurnContext> context = await provider.BeginTurnAsync(
            invocation.Value,
            Guid.CreateVersion7(),
            Token);

        Assert.True(context.IsSuccess, context.IsFailure ? context.Error.Message : string.Empty);

        return context.Value;

    }

    private static CovenantMutationService Service(CovenantCanonicalFixture fixture) =>
        new(
            fixture.Store,
            new CovenantCompiler(),
            new PassthroughEnvelopeCodec(),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantMutationKernel(),
            new FixedAuthority(),
            TimeProvider.System);

    /// <summary>
    /// A codec that authenticates by construction rather than by key material.
    /// </summary>
    /// <remarks>
    /// The envelope protocol has its own vectors and its own suite. What this one is about is whether
    /// a stated preference survives from one session to the next, so the stand-in keeps the exact
    /// shape — purpose, timestamps, payload — and skips only the cryptography.
    /// </remarks>
    private sealed class PassthroughEnvelopeCodec : ICovenantEnvelopeCodec
    {

        private readonly Dictionary<string, CovenantEnvelopeBody> _issued = new(StringComparer.Ordinal);

        public CovenantEnvelopeKeySnapshot KeySnapshot { get; } =
            new(1, 1, 1, Guid.NewGuid().ToString("D"), Guid.NewGuid());

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null)
        {

            string token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

            // Honoured, not ignored: a stand-in that stamped its own clock would let the body and
            // the header disagree, and the suite would rediscover that as a flake rather than a bug.
            DateTimeOffset now = issuedAtUtc ?? DateTimeOffset.UtcNow;

            _issued[token] = new CovenantEnvelopeBody(
                purpose,
                1,
                1,
                (ulong)_issued.Count + 1,
                DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds((now + lifetime).ToUnixTimeMilliseconds()),
                payload.ToArray());

            return Result<string>.Success(token);

        }

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            token is not null && _issued.TryGetValue(token, out CovenantEnvelopeBody? body)
                && body.Purpose == expectedPurpose
                ? Result<CovenantEnvelopeBody>.Success(body)
                : Result<CovenantEnvelopeBody>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This Covenant token is not valid for this purpose."));

    }

    private sealed class FixedAuthority : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; } = new(
            1,
            Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D"),
            AuthorityEpoch: 11,
            MasterKeyVersion: 1,
            RecoveryEnvelopeEpoch: 1,
            CovenantHostToolsState.Clean,
            null);

    }

}
