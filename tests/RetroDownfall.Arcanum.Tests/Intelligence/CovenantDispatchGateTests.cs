using System.Collections.Immutable;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The live turn's adoption of the Covenant: one plan, one admission per attempt, and a durable
/// disclosure receipt committed before any tainted payload leaves the process.
/// </summary>
public sealed class CovenantDispatchGateTests
{

    private static readonly Guid InstallationId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task An_ineligible_invocation_injects_nothing_and_reads_no_session_label()
    {

        RecordingSensitivityLedger ledger = new();

        CovenantDispatchGate gate = CreateGate(new RefusingContextProvider(), new RecordingJournal(), ledger);

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        Assert.False(scope.HasPlan);

        Assert.True(scope.PlanContent.IsEmpty);

        Assert.Equal(0, ledger.Reads);

    }

    [Fact]
    public async Task A_clean_dispatch_on_a_clean_session_commits_no_disclosure_at_all()
    {

        RecordingJournal journal = new();

        CovenantDispatchGate gate = CreateGate(new RefusingContextProvider(), journal, new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        CovenantDispatchPlan plan = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, plan);

        Assert.Equal(ContentSensitivity.None, sensitivity.Level);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            plan,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        Assert.Null(admitted.Value.Receipt);

        Assert.Null(admitted.Value.Disclosure);

        Assert.Empty(journal.Drafts);

    }

    [Fact]
    public async Task An_admitted_plan_renders_its_sections_and_discloses_before_dispatch()
    {

        RecordingJournal journal = new();

        CovenantTurnPlan plan = Plan(confirmed: 2, proposed: 2);

        CovenantDispatchGate gate = CreateGate(
            new PlanningContextProvider(plan),
            journal,
            new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        Assert.True(scope.HasPlan);

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        Assert.True(dispatch.HasAdmittedContent);

        Assert.True(dispatch.Content.HasGlobalConfirmed);

        Assert.True(dispatch.Content.HasProposed);

        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, dispatch);

        Assert.Equal(ContentSensitivity.CovenantDerived, sensitivity.Level);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        Assert.NotNull(admitted.Value.Receipt);

        Assert.NotNull(admitted.Value.Disclosure);

        CovenantDisclosureDraft draft = Assert.Single(journal.Drafts);

        Assert.Equal(CovenantEgressDestination.Provider, draft.Destination);

        Assert.Equal(CovenantDisclosureRevocability.Nonrevocable, draft.Revocability);

        Assert.Equal(InstallationId, draft.OriginInstallationId);

        Assert.Equal(admitted.Value.Receipt!.Digest, draft.AdmissionDigest);

        Assert.Equal(CovenantDisclosureEffectCategory.ProviderDispatch, Assert.Single(journal.Categories));

    }

    [Fact]
    public async Task A_refused_disclosure_refuses_the_dispatch()
    {

        RecordingJournal journal = new()
        {
            Failure = new Error(ErrorCodes.Covenant.Unavailable, "closed"),
        };

        CovenantDispatchGate gate = CreateGate(
            new PlanningContextProvider(Plan(confirmed: 1, proposed: 0)),
            journal,
            new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(CovenantDispatchGate.ResolveSensitivity(scope, dispatch)),
            CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, admitted.Error.Code);

    }

    [Fact]
    public async Task A_tainted_session_discloses_even_when_this_turn_admits_nothing()
    {

        RecordingJournal journal = new();

        CovenantDispatchGate gate = CreateGate(
            new RefusingContextProvider(),
            journal,
            new RecordingSensitivityLedger { Tainted = true });

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(scope.HistoryTainted);

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        Assert.False(dispatch.HasAdmittedContent);

        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, dispatch);

        Assert.Equal(ContentSensitivity.CovenantDerived, sensitivity.Level);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Assert.True(admitted.IsSuccess);

        // No plan means no admission to sign, but the disclosure is still owed: the provider is being
        // shown a transcript an earlier turn already tainted.
        Assert.Null(admitted.Value.Receipt);

        Assert.NotNull(admitted.Value.Disclosure);

        Assert.Null(Assert.Single(journal.Drafts).AdmissionDigest);

    }

    [Fact]
    public async Task An_unreadable_session_label_is_treated_as_tainted_rather_than_clean()
    {

        RecordingJournal journal = new();

        CovenantDispatchGate gate = CreateGate(
            new RefusingContextProvider(),
            journal,
            new RecordingSensitivityLedger { Failure = new Error(ErrorCodes.Grimoire.WriteFailed, "unreadable") });

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(scope.HistoryTainted);

    }

    [Fact]
    public async Task Pressure_drops_Proposed_and_keeps_Confirmed_in_the_same_attempt()
    {

        CovenantTurnPlan plan = Plan(confirmed: 1, proposed: 3);

        CovenantDispatchGate gate = CreateGate(
            new PlanningContextProvider(plan),
            new RecordingJournal(),
            new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        ulong confirmedOnly = ByteCost(CovenantPromptContent.FromAdmittedPrefix(plan, true, 0));

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, confirmedOnly, ByteCost, FragmentCost);

        Assert.True(dispatch.Content.HasConfirmed);

        Assert.False(dispatch.Content.HasProposed);

        Assert.Equal(3, dispatch.Admission!.ProposedRemovals);

    }

    [Fact]
    public async Task Two_attempts_on_one_turn_take_distinct_positive_ordinals()
    {

        RecordingJournal journal = new();

        CovenantDispatchGate gate = CreateGate(
            new PlanningContextProvider(Plan(confirmed: 1, proposed: 1)),
            journal,
            new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, dispatch);

        Result<CovenantDispatchAdmission> first = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Result<CovenantDispatchAdmission> second = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Assert.Equal(1UL, first.Value.Receipt!.GlobalAttemptOrdinal);

        Assert.Equal(2UL, second.Value.Receipt!.GlobalAttemptOrdinal);

        // Two physical attempts are two effects. Collapsing them would let a retried dispatch hide
        // behind the first one's receipt.
        Assert.NotEqual(journal.Drafts[0].EffectIdentityDigest, journal.Drafts[1].EffectIdentityDigest);

    }

    [Fact]
    public async Task An_installation_without_established_authority_refuses_a_tainted_dispatch()
    {

        CovenantDispatchGate gate = CreateGate(
            new PlanningContextProvider(Plan(confirmed: 1, proposed: 0)),
            new RecordingJournal(),
            new RecordingSensitivityLedger(),
            new FakeAuthority(null));

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        CovenantDispatchPlan dispatch = CovenantDispatchGate.PlanDispatch(scope, 10_000, ByteCost, FragmentCost);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            dispatch,
            ProviderCall(CovenantDispatchGate.ResolveSensitivity(scope, dispatch)),
            CancellationToken.None);

        Assert.True(admitted.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, admitted.Error.Code);

    }

    [Fact]
    public async Task A_stored_covenant_that_cannot_be_rendered_leaves_the_turn_covenant_free()
    {

        // The gate promises never to fail the turn, but it only handles typed failures. Linking is
        // where stored state meets the Section ceilings, so a store holding entries that render past
        // one has to arrive here as an absence rather than as an exception past every catch.
        CovenantDispatchGate gate = CreateGate(
            new LinkingContextProvider(OversizedSnapshot()),
            new RecordingJournal(),
            new RecordingSensitivityLedger());

        await using CovenantTurnScope scope = await gate.BeginTurnAsync(
            ArcanumInvocationContext.None,
            Guid.NewGuid(),
            sessionId: null,
            CancellationToken.None);

        Assert.False(scope.HasPlan);

        Assert.Equal(CovenantTurnAbsence.CapabilityUnavailable, scope.Absence);

    }

    private static CovenantTurnSnapshot OversizedSnapshot() =>
        CovenantTask6Fixture.Snapshot(
            null,
            CovenantTask6Fixture.CreateCandidate(
                "global.huge",
                CovenantTask6Fixture.G1,
                CovenantTask6Fixture.G2,
                1,
                CovenantScope.Global,
                null,
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                CovenantOrigin.Operator,
                CovenantCompiler.CompilerPolicyVersion,
                0,
                CovenantSnapshotCandidateIntegrity.Verified,
                digestSeed: 1,
                compiledFragment: OversizedFragment()));

    private static ImmutableArray<byte> OversizedFragment()
    {

        byte[] bytes = new byte[2 * CovenantLimits.MaxGlobalConfirmedRenderedBytes];

        bytes.AsSpan().Fill((byte)'x');

        bytes[^1] = (byte)'\n';

        return [.. bytes];

    }

    private static CovenantDispatchGate CreateGate(
        ICovenantContextProvider contextProvider,
        ICovenantDisclosureJournal journal,
        IArtifactSensitivityLedger ledger,
        ICovenantAuthoritySnapshotProvider? authority = null) =>
        new(
            contextProvider,
            journal,
            ledger,
            authority ?? new FakeAuthority(InstallationId.ToString()),
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

    private static ProviderCallEnvelope ProviderCall(ProviderCallSensitivity sensitivity) =>
        new(
            "provider.test",
            "model.test",
            CovenantProviderDispatchMode.Buffered,
            "o200k_base",
            128_000,
            0,
            sensitivity,
            FrozenProviderOptions.Create(new ProviderOptionsDigestInput(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                ProviderToolChoice.Auto,
                null,
                CovenantTriStateBoolean.Absent,
                ProviderResponseFormat.Text,
                null,
                null,
                null,
                CovenantTriStateBoolean.Absent,
                null,
                null,
                null,
                CovenantReasoningWireDialect.Standard,
                default)),
            [],
            [],
            new ProviderCallMaterializationSnapshot(false, []),
            [],
            [],
            null);

    private static CovenantTurnPlan Plan(int confirmed, int proposed)
    {

        List<CovenantSnapshotCandidate> candidates = [];

        for (int index = 0; index < confirmed; index++)
        {

            candidates.Add(CovenantTask6Fixture.GlobalConfirmed(
                $"confirmed.{index}",
                CovenantTask6Fixture.GuidFor(100 + index),
                CovenantTask6Fixture.GuidFor(200 + index),
                (ulong)(index + 1),
                (byte)(index + 1)));

        }

        for (int index = 0; index < proposed; index++)
        {

            candidates.Add(CovenantTask6Fixture.CampaignProposed(
                $"proposed.{index}",
                CovenantTask6Fixture.GuidFor(300 + index),
                CovenantTask6Fixture.GuidFor(400 + index),
                (ulong)(confirmed + index + 1),
                (byte)(50 + index),
                CovenantTask6Fixture.CampaignId));

        }

        return new CovenantLinker()
            .Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, [.. candidates]))
            .Value;

    }

    private static ulong ByteCost(CovenantPromptContent content) =>
        (ulong)(content.GlobalConfirmed.Length
            + content.CampaignConfirmed.Length
            + content.CampaignProposed.Length);

    private static ulong FragmentCost(string fragment) => (ulong)fragment.Length;

    private sealed class RefusingContextProvider : ICovenantContextProvider
    {

        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantTurnContext>.Success(
                CovenantTurnContext.Absent(CovenantTurnAbsence.NotEligible)));

    }

    /// <summary>
    /// Hands back a real plan with no lease, which is exactly what a turn sees once the gate,
    /// availability, Campaign, and epoch checks have all passed.
    /// </summary>
    private sealed class PlanningContextProvider(CovenantTurnPlan plan) : ICovenantContextProvider
    {

        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantTurnContext>.Success(
                CovenantTurnContext.ForPlan(
                    plan,
                    new CovenantTurnLease(new StubLeaseRegistration()),
                    null,
                    logicalTurnId)));

    }

    /// <summary>
    /// Links a stored snapshot exactly as the production context provider does, so that whatever the
    /// linker does with unrenderable state is what the gate has to survive.
    /// </summary>
    private sealed class LinkingContextProvider(CovenantTurnSnapshot snapshot) : ICovenantContextProvider
    {

        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken)
        {

            Result<CovenantTurnPlan> linked = new CovenantLinker().Link(snapshot);

            return ValueTask.FromResult(linked.IsFailure
                ? Result<CovenantTurnContext>.Failure(linked.Error)
                : Result<CovenantTurnContext>.Success(CovenantTurnContext.ForPlan(
                    linked.Value,
                    new CovenantTurnLease(new StubLeaseRegistration()),
                    null,
                    logicalTurnId)));

        }

    }

    private sealed class StubLeaseRegistration : ICovenantLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped,
            CovenantOperationScope.Global,
            CovenantTask6Fixture.DatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: 1,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    }

    private sealed class RecordingJournal : ICovenantDisclosureJournal
    {

        public List<CovenantDisclosureDraft> Drafts { get; } = [];

        public List<CovenantDisclosureEffectCategory> Categories { get; } = [];

        public Error? Failure { get; init; }

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {

            Drafts.Add(draft);

            Categories.Add(category);

            return ValueTask.FromResult(Failure is { } error
                ? Result<CovenantDisclosureReceipt>.Failure(error)
                : Result<CovenantDisclosureReceipt>.Success(
                    new CovenantDisclosureReceipt(draft, (ulong)Drafts.Count)));

        }

    }

    private sealed class RecordingSensitivityLedger : IArtifactSensitivityLedger
    {

        public int Reads { get; private set; }

        public bool Tainted { get; init; }

        public Error? Failure { get; init; }

        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {

            Reads++;

            return Task.FromResult(Failure is { } error
                ? Result<SessionSensitivityProjection>.Failure(error)
                : Result<SessionSensitivityProjection>.Success(new SessionSensitivityProjection(
                    sessionId,
                    Tainted ? 1 : 0,
                    Tainted ? ContentSensitivity.CovenantDerived : ContentSensitivity.None,
                    CovenantTask6Fixture.D(7),
                    1)));

        }

    }

    private sealed class FakeAuthority(string? installationIdentity) : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; } = installationIdentity is null
            ? null
            : new CovenantAuthoritySnapshot(
                1,
                installationIdentity,
                1,
                1,
                1,
                CovenantHostToolsState.Clean,
                null);

    }

}
