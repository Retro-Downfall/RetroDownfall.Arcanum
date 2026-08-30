using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Classification of a frozen tool call, the live policy that governs sensitive egress, and the
/// acknowledgement that has to commit before any of it physically happens (§10.14).
/// </summary>
public sealed class CovenantSensitiveEgressTests
{

    private static readonly Guid Installation = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

    [Fact]
    public void A_frozen_covenant_call_classifies_as_sensitive_egress_with_private_arguments()
    {
        Result<ProviderToolCallClassification> proposal = CovenantToolClassifier.Classify(
            CovenantToolNames.ProposeCovenant, Bytes("{\"key\":\"a\",\"content\":\"b\"}"),
            Wards());
        Result<ProviderToolCallClassification> retirement = CovenantToolClassifier.Classify(
            CovenantToolNames.RetireCovenant, Bytes("{\"key\":\"a\",\"lane\":\"Proposed\"}"),
            Wards());

        Assert.Equal(CovenantToolRiskIdentity.CovenantSensitiveEgress, proposal.Value.RiskIdentity);
        Assert.Equal(CovenantToolRiskIdentity.CovenantSensitiveEgress, retirement.Value.RiskIdentity);
        Assert.True(proposal.Value.ArgumentsArePrivate);
        Assert.True(retirement.Value.ArgumentsArePrivate);
        Assert.True(proposal.Value.IsCovenantMutation);
    }

    [Fact]
    public void Formerly_intrinsic_calls_are_ordinary_while_configured_risk_remains_metadata()
    {
        Result<ProviderToolCallClassification> ordinary = CovenantToolClassifier.Classify(
            "read_saga", Bytes("{\"query\":\"a\"}"),
            Wards());
        Result<ProviderToolCallClassification> formerlyIntrinsic = CovenantToolClassifier.Classify(
            ToolRiskClassifier.ApplyPatchToolName, Bytes("{\"patch\":\"a\"}"),
            Wards());
        Result<ProviderToolCallClassification> configured = CovenantToolClassifier.Classify(
            "delete_lexicon", Bytes("{\"name\":\"a\"}"),
            Wards() with { ForbiddenArts = ["delete_lexicon"] });

        Assert.Equal(CovenantToolRiskIdentity.Ordinary, ordinary.Value.RiskIdentity);
        Assert.False(ordinary.Value.ArgumentsArePrivate);
        Assert.Equal(CovenantToolRiskIdentity.Ordinary, formerlyIntrinsic.Value.RiskIdentity);
        Assert.False(formerlyIntrinsic.Value.ArgumentsArePrivate);
        Assert.Equal(CovenantToolRiskIdentity.ConfiguredForbiddenArt, configured.Value.RiskIdentity);
    }

    [Fact]
    public void Classification_binds_the_exact_name_and_the_canonical_arguments()
    {
        ProviderToolCallClassification first = CovenantToolClassifier
            .Classify(CovenantToolNames.ProposeCovenant, Bytes("{\"b\":2,\"a\":1}"), Wards())
            .Value;
        ProviderToolCallClassification reordered = CovenantToolClassifier
            .Classify(CovenantToolNames.ProposeCovenant, Bytes("{\"a\":1,\"b\":2}"), Wards())
            .Value;
        ProviderToolCallClassification different = CovenantToolClassifier
            .Classify(CovenantToolNames.ProposeCovenant, Bytes("{\"a\":1,\"b\":3}"), Wards())
            .Value;

        // RFC 8785 ordering means the same request cannot produce two different evidence digests.
        Assert.Equal(first.CanonicalArgumentDigest, reordered.CanonicalArgumentDigest);
        Assert.NotEqual(first.CanonicalArgumentDigest, different.CanonicalArgumentDigest);
        Assert.NotEqual(
            first.FrozenNameDigest,
            CovenantToolClassifier.Classify("read_saga", Bytes("{}"), Wards()).Value.FrozenNameDigest);
    }

    [Fact]
    public void Classification_refuses_arguments_that_are_not_valid_json()
    {
        Result<ProviderToolCallClassification> malformed = CovenantToolClassifier.Classify(
            CovenantToolNames.ProposeCovenant, Bytes("{\"key\":"),
            Wards());

        Assert.Equal(ErrorCodes.Hub.ProviderToolCallInvalid, malformed.Error.Code);
    }

    [Fact]
    public void An_absent_argument_body_classifies_as_the_empty_object()
    {
        ProviderToolCallClassification empty = CovenantToolClassifier
            .Classify("read_saga", Bytes(string.Empty), Wards())
            .Value;
        ProviderToolCallClassification explicitEmpty = CovenantToolClassifier
            .Classify("read_saga", Bytes("{}"), Wards())
            .Value;

        Assert.Equal(explicitEmpty.CanonicalArgumentDigest, empty.CanonicalArgumentDigest);
    }

    [Theory]
    [InlineData(InvocationAttendance.Attended)]
    [InlineData(InvocationAttendance.Unattended)]
    public void Eligible_retirement_is_ungated_regardless_of_attendance(InvocationAttendance attendance)
    {
        CovenantEgressWardDecision retirement = CovenantEgressWardPolicy.Resolve(
            Classified(CovenantToolNames.RetireCovenant),
            EligibleInvocation(attendance));

        Assert.Equal(CovenantEgressAuthorization.UngatedRetirement, retirement.Authorization);
        Assert.False(retirement.IsDenied);
    }

    [Fact]
    public void Ward_configuration_cannot_change_retirement_authorization()
    {
        WardSettings[] settings =
        [
            Wards(),
            Wards(enabled: false),
            Wards(autoApproveEnabled: true, autoApproveTools: [CovenantToolNames.RetireCovenant]),
            Wards(autoApproveEnabled: false, autoApproveTools: [CovenantToolNames.RetireCovenant]),
        ];

        CovenantEgressAuthorization[] authorizations =
        [
            .. settings.Select(wards => CovenantEgressWardPolicy.Resolve(
                CovenantToolClassifier.Classify(
                    CovenantToolNames.RetireCovenant,
                    Bytes("{\"key\":\"a\",\"lane\":\"Proposed\"}"),
                    wards).Value,
                EligibleInvocation()).Authorization),
        ];

        Assert.All(
            authorizations,
            static authorization => Assert.Equal(
                CovenantEgressAuthorization.UngatedRetirement,
                authorization));
    }

    [Fact]
    public void A_proposal_remains_sensitive_payload_only()
    {
        CovenantEgressWardDecision proposal = CovenantEgressWardPolicy.Resolve(
            Classified(CovenantToolNames.ProposeCovenant),
            EligibleInvocation());

        Assert.Equal(CovenantEgressAuthorization.SensitivePayloadOnly, proposal.Authorization);
        Assert.False(proposal.IsDenied);
    }

    [Fact]
    public void An_ordinary_call_is_outside_this_policy_entirely()
    {
        CovenantEgressWardDecision decision = CovenantEgressWardPolicy.Resolve(
            Classified("read_saga"),
            EligibleInvocation());

        Assert.Equal(CovenantEgressAuthorization.NotSensitive, decision.Authorization);
        Assert.False(decision.IsDenied);
    }

    [Fact]
    public void An_ineligible_turn_is_denied_before_any_ward_is_placed()
    {
        CovenantEgressWardDecision decision = CovenantEgressWardPolicy.Resolve(
            Classified(CovenantToolNames.RetireCovenant),
            ArcanumInvocationContext.None);

        Assert.Equal(CovenantEgressAuthorization.DeniedIneligibleTurn, decision.Authorization);
        Assert.True(decision.IsDenied);
    }

    [Fact]
    public void No_live_policy_method_produces_a_ward_receipt()
    {
        Assert.DoesNotContain(
            typeof(CovenantEgressWardPolicy).GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            static method => method.ReturnType == typeof(Result<CovenantToolWardReceipt>));
    }

    [Fact]
    public async Task The_effect_never_runs_until_its_receipt_has_committed()
    {
        RecordingJournal journal = new();

        CovenantToolEgressGuard guard = new(journal);

        List<string> order = [];

        journal.OnAcknowledge = () => order.Add("acknowledged");

        Result<string> outcome = await guard.DiscloseThenAsync(
            Attempt(1),
            (receipt, _) =>
            {
                order.Add("effect");

                return ValueTask.FromResult(Result<string>.Success("sent"));
            },
            CancellationToken.None);

        Assert.True(outcome.IsSuccess, outcome.Error.Message);
        Assert.Equal(["acknowledged", "effect"], order);
    }

    [Fact]
    public async Task A_journal_that_cannot_commit_stops_the_effect_from_happening_at_all()
    {
        RecordingJournal journal = new()
        {
            Failure = new Error(ErrorCodes.Covenant.Unavailable, "The disclosure journal is closed."),
        };

        CovenantToolEgressGuard guard = new(journal);

        bool ran = false;

        Result<string> outcome = await guard.DiscloseThenAsync(
            Attempt(1),
            (_, _) =>
            {
                ran = true;

                return ValueTask.FromResult(Result<string>.Success("sent"));
            },
            CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(ErrorCodes.Covenant.Unavailable, outcome.Error.Code);
    }

    [Fact]
    public async Task Every_physical_attempt_receives_its_own_effect_identity()
    {
        RecordingJournal journal = new();

        CovenantToolEgressGuard guard = new(journal);

        _ = await guard.DiscloseThenAsync(Attempt(1), Sent, CancellationToken.None);
        _ = await guard.DiscloseThenAsync(Attempt(2), Sent, CancellationToken.None);

        // A retry or a reconnect is a second physical disclosure, and the ordinal is what makes the
        // journal count it rather than fold it into the first.
        Assert.Equal(2, journal.Drafts.Count);
        Assert.NotEqual(journal.Drafts[0].EffectIdentityDigest, journal.Drafts[1].EffectIdentityDigest);
        Assert.All(
            journal.Categories,
            category => Assert.Equal(CovenantDisclosureEffectCategory.McpToolUse, category));
    }

    [Fact]
    public async Task One_physical_attempt_replayed_keeps_one_effect_identity()
    {
        RecordingJournal journal = new();

        CovenantToolEgressGuard guard = new(journal);

        CovenantToolEgressAttempt attempt = Attempt(1);

        _ = await guard.DiscloseThenAsync(attempt, Sent, CancellationToken.None);
        _ = await guard.DiscloseThenAsync(attempt, Sent, CancellationToken.None);

        Assert.Equal(journal.Drafts[0].EffectIdentityDigest, journal.Drafts[1].EffectIdentityDigest);
    }

    [Fact]
    public async Task The_receipt_carries_the_ward_evidence_and_the_admission_that_authorized_it()
    {
        RecordingJournal journal = new();

        CovenantToolEgressGuard guard = new(journal);

        CovenantToolEgressAttempt attempt = Attempt(1);

        _ = await guard.DiscloseThenAsync(attempt, Sent, CancellationToken.None);

        CovenantDisclosureDraft draft = Assert.Single(journal.Drafts);

        Assert.Equal(CovenantDisclosureSubjectKind.Turn, draft.SubjectKind);
        Assert.Equal(attempt.LogicalTurnId, draft.SubjectId);
        Assert.Equal(attempt.WardEvidenceDigest, draft.WardEvidenceDigest);
        Assert.Equal(attempt.ProducingAdmissionDigest, draft.AdmissionDigest);
        Assert.Equal(CovenantDisclosureRevocability.Nonrevocable, draft.Revocability);
    }

    private static ValueTask<Result<string>> Sent(CovenantDisclosureReceipt receipt, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result<string>.Success("sent"));

    private static readonly CovenantDigest CapabilityNonceDigest =
        CovenantToolCapabilityNonce.Create().ToDigest();

    /// <summary>
    /// One capability, one call, one set of arguments: the physical attempt ordinal is the only
    /// thing that varies, so a differing effect identity can only have come from it.
    /// </summary>
    private static CovenantToolEgressAttempt Attempt(ulong ordinal) =>
        new(
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            ordinal,
            CovenantTask6Fixture.D(20),
            CapabilityNonceDigest,
            "call-1",
            CovenantTask6Fixture.D(21),
            CovenantTask6Fixture.D(22),
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(23),
            Sensitivity(),
            CovenantTask6Fixture.D(24),
            Timestamp: 1_700_000_000);

    private static ProviderCallSensitivity Sensitivity() =>
        CovenantTask6Fixture.ProviderCall().Sensitivity;

    private static ProviderToolCallClassification Classified(
        string toolName,
        string arguments = "{\"key\":\"a\"}") =>
        CovenantToolClassifier.Classify(toolName, Bytes(arguments), Wards()).Value;

    /// <summary>
    /// The argument bytes exactly as a transport that already framed the call hands them over.
    /// </summary>
    /// <remarks>
    /// These used to be assembled through a streaming fragment buffer that no production path ever
    /// called. Routing the tests through the same complete-name, complete-body shape the in-process
    /// MCP server actually produces keeps them describing a call the system can really receive.
    /// </remarks>
    private static byte[] Bytes(string arguments) => Encoding.UTF8.GetBytes(arguments);

    private static WardSettings Wards(
        bool enabled = true,
        bool autoApproveEnabled = false,
        string[]? autoApproveTools = null) =>
        new()
        {
            Enabled = enabled,
            AutoApproveEnabled = autoApproveEnabled,
            AutoApproveTools = autoApproveTools ?? [],
        };

    private static ArcanumInvocationContext EligibleInvocation(
        InvocationAttendance attendance = InvocationAttendance.Attended) =>
        ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantCapabilityFixtures.Campaign(),
            attendance,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Installation,
                runtimeAuthorityGeneration: 1,
                authorityEpoch: 7)).Value;

    private sealed class RecordingJournal : ICovenantDisclosureJournal
    {

        public List<CovenantDisclosureDraft> Drafts { get; } = [];

        public List<CovenantDisclosureEffectCategory> Categories { get; } = [];

        public Error? Failure { get; init; }

        public Action? OnAcknowledge { get; set; }

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {
            OnAcknowledge?.Invoke();

            Drafts.Add(draft);

            Categories.Add(category);

            return ValueTask.FromResult(
                Failure is { } failure
                    ? Result<CovenantDisclosureReceipt>.Failure(failure)
                    : Result<CovenantDisclosureReceipt>.Success(
                        new CovenantDisclosureReceipt(draft, (ulong)Drafts.Count)));
        }

    }

}
