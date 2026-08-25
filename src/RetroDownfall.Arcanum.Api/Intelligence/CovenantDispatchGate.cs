using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Logging;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// One logical turn's Covenant participation, from the single plan to the last dispatch.
/// </summary>
/// <remarks>
/// The scope exists so that "this turn never asked about the Covenant" and "this turn asked and was
/// told there is nothing" are different values rather than two spellings of null. Buffered
/// inference, streaming inference, provider fallback, tool rounds, and compression rebuilds all
/// share exactly one of these, which is what stops a retry from observing a mutation another turn
/// published halfway through this one.
///
/// <para>Disposal releases the underlying turn lease. Holding it for the whole turn is deliberate: a
/// destructive operation drains live leases before it reports that it may change anything, so a turn
/// that returned its lease early could still be rendering Covenant bytes into a response while reset
/// was telling the operator the data was gone.</para>
/// </remarks>
public sealed class CovenantTurnScope : IAsyncDisposable
{

    private readonly CovenantTurnContext _context;

    private ulong _attemptOrdinal;

    internal CovenantTurnScope(
        CovenantTurnContext context,
        Guid logicalTurnId,
        Guid? sessionId,
        bool historyTainted)
    {

        _context = context;

        LogicalTurnId = logicalTurnId;

        SessionId = sessionId;

        HistoryTainted = historyTainted;

    }

    /// <summary>The inert scope for a turn that is not eligible to read Covenant content at all.</summary>
    /// <remarks>
    /// Carries <see cref="CovenantTurnAbsence.NotEligible"/> rather than a null context so that every
    /// caller downstream reads the same shape whether the gate ran or was never composed.
    /// </remarks>
    public static CovenantTurnScope NotEligible() =>
        new(CovenantTurnContext.Absent(CovenantTurnAbsence.NotEligible), Guid.Empty, null, historyTainted: false);

    public Guid LogicalTurnId { get; }

    public Guid? SessionId { get; }

    /// <summary>Whether this Session already holds Covenant-derived history.</summary>
    /// <remarks>
    /// Read once per turn, before the first dispatch. A previously tainted Session keeps its
    /// protected-read and disclosure obligations even on a turn that admits nothing and even while
    /// injection is switched off, because the taint describes what the provider is about to be shown,
    /// not what this turn chose to add.
    /// </remarks>
    public bool HistoryTainted { get; }

    /// <summary>The sensitivity of the last dispatch this turn actually made, if any was protected.</summary>
    /// <remarks>
    /// What the reply inherits. A turn that showed the provider protected content produces a protected
    /// answer, and the answer's label is read by the next turn to decide whether it in turn owes a
    /// disclosure — so this is the link that makes taint travel forward rather than stopping at the one
    /// turn that introduced it.
    /// </remarks>
    public ProviderCallSensitivity? DerivedSensitivity { get; private set; }

    internal void RecordDerived(ProviderCallSensitivity sensitivity) => DerivedSensitivity = sensitivity;

    /// <summary>The last admission this turn minted, which is the branch a seal has to commit.</summary>
    /// <remarks>
    /// The last one rather than the first. Every staging tool call records the ordinal of the
    /// admission that produced it, and a seal takes the lineage up to the ordinal it is given, so
    /// sealing against an earlier attempt would silently drop every mutation a later tool round
    /// staged — the exact acknowledgement the tool had already reported to the model.
    /// </remarks>
    private CovenantAdmissionReceipt? _lastAdmission;

    internal void RecordAdmitted(CovenantAdmissionReceipt receipt) => _lastAdmission = receipt;

    /// <summary>
    /// The staged batch this turn owes its answer, or absent when it staged nothing to publish.
    /// </summary>
    /// <remarks>
    /// Absent is the ordinary answer. Almost every turn stages nothing, and a binding minted anyway
    /// would make "this turn has a profile change to publish" indistinguishable from "this turn has a
    /// collector", which every eligible turn has.
    /// </remarks>
    public CovenantTurnCommitBinding? StagedCommit() =>
        _context.Collector is { StagedCount: > 0 } collector
            && _context.Plan is { } plan
            && _lastAdmission is { } admission
                ? new CovenantTurnCommitBinding(
                    collector,
                    admission.BranchId,
                    admission.BranchOrdinal,
                    plan.Snapshot.DatasetGeneration.Value,
                    plan.Snapshot.KeyReclamationEpoch)
                : null;

    public CovenantTurnAbsence Absence => _context.Absence;

    public bool HasPlan => _context.HasPlan;

    public CovenantTurnPlan? Plan => _context.Plan;

    public ICovenantMutationCollector? Collector => _context.Collector;

    /// <summary>The bounded head read this turn's staging tool calls may make.</summary>
    public ICovenantTurnHeadProbe? HeadProbe => _context.HeadProbe;

    /// <summary>Whether this turn is provisioned to stage a Covenant mutation of its own.</summary>
    /// <remarks>
    /// Read from the collector and the probe rather than from the invocation context, because those
    /// two are minted from exactly that predicate and are what a staging tool call actually needs.
    /// The dispatch path holds this scope and not the context that produced it, so deriving the
    /// answer here is what stops the admission decision and the provisioning decision from drifting
    /// apart — which they had, in the direction that made the first proposal on an empty Covenant
    /// impossible to author.
    /// </remarks>
    public bool MayStage => _context.Collector is not null && _context.HeadProbe is not null;

    /// <summary>The unpressured content this plan would inject before any budget is applied.</summary>
    public CovenantPromptContent PlanContent => _context.PlanContent;

    public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
        _context.RevalidateAsync(cancellationToken);

    /// <summary>Allocates this turn's next global provider-attempt ordinal.</summary>
    /// <remarks>
    /// Starts at one because the admission receipt refuses a zero ordinal: an attempt that has not
    /// happened and the first attempt must not share an identity.
    /// </remarks>
    internal ulong NextAttemptOrdinal() => Interlocked.Increment(ref _attemptOrdinal);

    public ValueTask DisposeAsync() => _context.DisposeAsync();

}

/// <summary>
/// One turn's staged Covenant batch, and the canonical facts it was staged under.
/// </summary>
/// <remarks>
/// Carries the collector rather than an array already sealed from it, because sealing has to happen
/// inside the finalize path's own failure envelope. A batch frozen before the writer was entered
/// would be a sealed collector nobody had yet committed to publishing, and the turn could still end
/// without publishing it — which is the one state worse than never having sealed at all.
///
/// <para>The Campaign registry epoch is absent by construction rather than defaulted. An agent
/// proposal reaches exactly one Campaign — the canonical schema forbids a Global Proposed head — so
/// the batch binds no registry, and the kernel skips a comparison that a stand-in value would have
/// failed on every installation whose registry had ever advanced.</para>
/// </remarks>
public sealed record CovenantTurnCommitBinding(
    ICovenantMutationCollector Collector,
    Guid CommittedBranchId,
    ulong FinalBranchOrdinal,
    Guid DatasetGeneration,
    long ExpectedKeyReclamationEpoch);

/// <summary>
/// What one provider attempt may inject, decided before its prompt is built.
/// </summary>
/// <remarks>
/// Two phases exist because the admission receipt has to freeze the exact bytes sent, and which
/// bytes those are is what this phase decides. Measuring candidate content against the attempt's own
/// budget therefore happens first, against the reused plan, and the receipt is minted afterwards
/// over the prompt that measurement produced.
/// </remarks>
/// <param name="Content">What this attempt injects.</param>
/// <param name="Admission">The attempt's admission outcome, or <see langword="null"/> when it had no plan.</param>
/// <param name="AvailableTokenBudget">
/// The head-room this attempt planned against, carried so the receipt can bind the budget the
/// decision was made under. The cost of what was admitted is not that number: the receipt checks its
/// per-candidate evidence against this bound, and the two are measured differently -- per fragment
/// against per section -- so handing it the admitted cost compares a sum of message estimates against
/// a single one and refuses an admission its own planner accepted.
/// </param>
public sealed record CovenantDispatchPlan(
    CovenantPromptContent Content,
    CovenantAdmissionPlan? Admission,
    ulong AvailableTokenBudget)
{

    /// <summary>The dispatch that injects nothing, for a turn with no plan.</summary>
    public static CovenantDispatchPlan Empty { get; } = new(CovenantPromptContent.None, null, 0);

    public bool HasAdmittedContent => !Content.IsEmpty;

}

/// <summary>
/// The evidence one provider attempt earned before its bytes were allowed to leave.
/// </summary>
public sealed record CovenantDispatchAdmission(
    CovenantAdmissionReceipt? Receipt,
    CovenantDisclosureReceipt? Disclosure,
    ProviderCallSensitivity Sensitivity)
{

    public bool IsCovenantDerived => Sensitivity.Level is ContentSensitivity.CovenantDerived;

}

/// <summary>
/// The one place a live turn adopts the Covenant, and the only path its bytes take to a provider.
/// </summary>
/// <remarks>
/// Everything here already existed as a component and was reachable by nothing. The gate is the
/// composition: one plan per logical turn, one admission per provider attempt, and one durable
/// disclosure receipt committed before any tainted payload leaves the process.
///
/// <para>Ordering is the contract, not an implementation detail. A journal written after dispatch
/// cannot record the one case it exists for — a call that left the process and then crashed — and the
/// operator would be told nothing was disclosed while the provider already held the payload.</para>
/// </remarks>
public sealed class CovenantDispatchGate(
    ICovenantContextProvider contextProvider,
    ICovenantDisclosureJournal disclosureJournal,
    IArtifactSensitivityLedger sensitivityLedger,
    ICovenantAuthoritySnapshotProvider authority,
    TimeProvider timeProvider,
    ILogger<CovenantDispatchGate> logger)
{

    /// <summary>
    /// Acquires this turn's single Covenant plan, or a typed reason why it has none.
    /// </summary>
    /// <remarks>
    /// Never fails the turn. Every absence the provider reports is a fact about Covenant state that a
    /// turn is entitled to proceed without; a genuine authority or storage failure is logged and
    /// degraded to <see cref="CovenantTurnAbsence.CapabilityUnavailable"/> rather than taking down an
    /// inference the operator did not ask to be Covenant-bearing. What is *not* degraded is the
    /// disclosure obligation: a tainted Session still reaches
    /// <see cref="AcknowledgeDispatchAsync"/> below, and that path does fail closed.
    /// </remarks>
    public async ValueTask<CovenantTurnScope> BeginTurnAsync(
        ArcanumInvocationContext invocation,
        Guid logicalTurnId,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(invocation);

        bool tainted = await ReadHistoryTaintAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (logicalTurnId == Guid.Empty)
        {

            return Record(
                new CovenantTurnScope(
                    CovenantTurnContext.Absent(CovenantTurnAbsence.NotEligible),
                    Guid.Empty,
                    sessionId,
                    tainted),
                invocation.Campaign?.CampaignId);

        }

        Result<CovenantTurnContext> begun = await contextProvider
            .BeginTurnAsync(invocation, logicalTurnId, cancellationToken)
            .ConfigureAwait(false);

        if (begun.IsFailure)
        {

            logger.LogWarning(
                "Covenant turn acquisition failed with {ErrorCode}; the turn proceeds without Covenant content.",
                begun.Error.Code);

            return Record(
                new CovenantTurnScope(
                    CovenantTurnContext.Absent(CovenantTurnAbsence.CapabilityUnavailable),
                    logicalTurnId,
                    sessionId,
                    tainted),
                invocation.Campaign?.CampaignId);

        }

        return Record(
            new CovenantTurnScope(begun.Value, logicalTurnId, sessionId, tainted),
            invocation.Campaign?.CampaignId);

    }

    /// <summary>The empty provenance every absence record carries, allocated once rather than per turn.</summary>
    private static readonly GenerationProvenance NoProvenance = GenerationProvenance.CreateExact([]);

    /// <summary>
    /// Emits this turn's single content-free participation record and returns the scope unchanged.
    /// </summary>
    /// <remarks>
    /// The typed answer to "why was my preference not injected this turn" is decided once per turn and
    /// was previously readable only by holding the scope, which nothing outside the dispatch path
    /// does. One record per turn makes it answerable from a log without a debugger attached.
    ///
    /// <para>Debug rather than Information because it fires on every live turn, including the
    /// overwhelmingly common one where a plan was present and nothing was wrong; a per-turn
    /// Information line would drown the warnings above it. The payload is a
    /// <see cref="CovenantProtectedLogScope"/> so the type, not this call site, is what guarantees
    /// no Covenant key, fragment, or content digest can ever be added to it.</para>
    /// </remarks>
    private CovenantTurnScope Record(CovenantTurnScope scope, Guid? campaignId)
    {

        logger.LogDebug(
            "Covenant turn {LogicalTurnId} resolved to {CovenantAbsence} ({CovenantScope}), history tainted {HistoryTainted}.",
            scope.LogicalTurnId,
            scope.Absence,
            CovenantProtectedLogScope.FromSensitivity(
                ContentSensitivity.None,
                NoProvenance,
                scope.SessionId,

                // The Campaign this turn resolved to, which is the other half of "whose memory is this
                // record about". A per-turn absence line without it cannot be read across Campaigns at
                // all: every Campaign in an installation produces the same sentence, and the operator
                // asking why one of them injects nothing has no way to tell which lines are theirs.
                campaignId),
            scope.HistoryTainted);

        return scope;

    }

    /// <summary>
    /// Decides what this attempt may inject, from the reused plan and this attempt's own budget.
    /// </summary>
    /// <remarks>
    /// Pure, and deliberately static: two fallback candidates with different tokenizers and different
    /// remaining budgets must reach different admissions from the same bytes and the same revision
    /// vector, and nothing about that decision may depend on gate state that a retry could have moved.
    /// </remarks>
    public static CovenantDispatchPlan PlanDispatch(
        CovenantTurnScope scope,
        ulong availableTokenBudget,
        Func<CovenantPromptContent, ulong> measureSections,
        Func<string, ulong> measureFragment)
    {

        ArgumentNullException.ThrowIfNull(scope);

        ArgumentNullException.ThrowIfNull(measureSections);

        ArgumentNullException.ThrowIfNull(measureFragment);

        if (scope.Plan is not { } plan)
        {

            return CovenantDispatchPlan.Empty;

        }

        CovenantAdmissionPlan admission = CovenantAdmissionPlanner.Plan(
            plan,
            availableTokenBudget,
            measureSections,
            measureFragment);

        return new CovenantDispatchPlan(admission.AdmittedContent, admission, availableTokenBudget);

    }

    /// <summary>
    /// The sensitivity this attempt's payload carries, before its envelope is frozen.
    /// </summary>
    /// <remarks>
    /// Conservative in exactly one direction. Admitted Covenant content taints the call, and so does
    /// history that was already tainted by an earlier turn, because the provider is shown both. The
    /// reverse — reporting a clean call while tainted bytes are in the transcript — is the failure this
    /// method exists to make impossible.
    /// </remarks>
    public static ProviderCallSensitivity ResolveSensitivity(CovenantTurnScope scope, CovenantDispatchPlan plan)
    {

        ArgumentNullException.ThrowIfNull(scope);

        ArgumentNullException.ThrowIfNull(plan);

        bool derived = plan.HasAdmittedContent || scope.HistoryTainted;

        if (!derived)
        {

            GenerationProvenance clean = GenerationProvenance.CreateExact([]);

            return new ProviderCallSensitivity(
                ContentSensitivity.None,
                clean,
                CovenantDigests.Sensitivity(new SensitivityDigestInput(
                    ContentSensitivity.None,
                    clean.Mode,
                    clean.ExactGenerationIds,
                    clean.BloomBits)));

        }

        // The dataset generation is the honest provenance for an injected plan: the exact rows are
        // already named by the admission vector, and repeating them here would put content identities
        // into disclosure accounting that is required to stay content-free. A tainted-history call
        // with no plan of its own has no generation to name at all, so it declares the taint without
        // claiming to know which generation produced it.
        GenerationProvenance provenance = scope.Plan is { } turnPlan && plan.HasAdmittedContent
            ? GenerationProvenance.CreateExact([turnPlan.Snapshot.DatasetGeneration.Value])
            : GenerationProvenance.CreateExact([scope.LogicalTurnId]);

        return new ProviderCallSensitivity(
            ContentSensitivity.CovenantDerived,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.CovenantDerived,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));

    }

    /// <summary>
    /// Commits this attempt's admission receipt and its durable disclosure, before dispatch.
    /// </summary>
    /// <remarks>
    /// A clean call on a clean Session performs no database work at all — that absence is the
    /// disabled-path guarantee, not an oversight. Every call that discloses fails closed: if the
    /// journal cannot commit, the bytes do not leave.
    ///
    /// <para>A receipt and a disclosure are separate obligations, not two names for one. Showing the
    /// provider Covenant bytes is what owes a disclosure; holding the collector and the head probe is
    /// what owes an admission, because that receipt is the whole of a staging tool call's authority.
    /// A clean turn on an empty Covenant owes the second and not the first, and that is the turn on
    /// which an agent authors its first proposal.</para>
    /// </remarks>
    public async ValueTask<Result<CovenantDispatchAdmission>> AcknowledgeDispatchAsync(
        CovenantTurnScope scope,
        CovenantDispatchPlan plan,
        ProviderCallEnvelope providerCall,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(scope);

        ArgumentNullException.ThrowIfNull(plan);

        ArgumentNullException.ThrowIfNull(providerCall);

        ProviderCallSensitivity sensitivity = providerCall.Sensitivity;

        if (sensitivity.Level is not ContentSensitivity.CovenantDerived)
        {

            // Nothing Covenant-derived is leaving, so there is nothing to disclose and the reply this
            // call produces stays unlabelled. A turn that may stage still earns its admission here:
            // the staging capability is minted from that receipt and from nothing else, so a gate
            // that returned none would advertise the proposal tool on every healthy installation and
            // refuse every call it ever made against an empty Covenant.
            CovenantAdmissionReceipt? stagingAdmission = scope.MayStage
                ? BuildReceipt(scope, plan, providerCall, scope.NextAttemptOrdinal())
                : null;

            if (stagingAdmission is not null)
            {

                scope.RecordAdmitted(stagingAdmission);

            }

            return Result<CovenantDispatchAdmission>.Success(
                new CovenantDispatchAdmission(stagingAdmission, null, sensitivity));

        }

        Guid installationId = ResolveInstallationIdentity();

        if (installationId == Guid.Empty)
        {

            return Result<CovenantDispatchAdmission>.Failure(new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "This installation has no established Covenant authority to disclose under."));

        }

        ulong attemptOrdinal = scope.NextAttemptOrdinal();

        CovenantAdmissionReceipt? receipt = BuildReceipt(scope, plan, providerCall, attemptOrdinal);

        // The subject is the logical turn even when there is no plan. A tainted Session with nothing
        // admitted still discloses under the turn that showed the provider its history, which is the
        // subject an operator would later ask about.
        CovenantDigest effectIdentity = CovenantDigests.ProviderDispatchEffect(new ProviderDispatchEffectDigestInput(
            scope.LogicalTurnId == Guid.Empty ? installationId : scope.LogicalTurnId,
            attemptOrdinal,
            receipt?.Digest ?? providerCall.Digest,
            providerCall.Digest,
            DestinationIdentity(providerCall.ProviderIdentity)));

        CovenantDisclosureDraft draft = new(
            installationId,
            CovenantDisclosureSubjectKind.Turn,
            scope.LogicalTurnId == Guid.Empty ? installationId : scope.LogicalTurnId,
            effectIdentity,
            CovenantEgressDestination.Provider,

            // Nonrevocable by construction. Local erasure cannot reach a payload a provider already
            // holds, and reporting it as revocable would promise an undo Arcanum cannot perform.
            CovenantDisclosureRevocability.Nonrevocable,
            DestinationIdentity(providerCall.ProviderIdentity),
            sensitivity.Digest,
            null,
            receipt?.Digest,
            null,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        Result<CovenantDisclosureReceipt> acknowledged = await disclosureJournal
            .AcknowledgeAsync(draft, CovenantDisclosureEffectCategory.ProviderDispatch, sensitivity, cancellationToken)
            .ConfigureAwait(false);

        if (acknowledged.IsFailure)
        {

            return Result<CovenantDispatchAdmission>.Failure(acknowledged.Error);

        }

        scope.RecordDerived(sensitivity);

        // Recorded only once the disclosure is durable. An admission remembered before its receipt
        // committed could become the branch a seal publishes against for a dispatch whose bytes were
        // never allowed to leave.
        if (receipt is not null)
        {

            scope.RecordAdmitted(receipt);

        }

        return Result<CovenantDispatchAdmission>.Success(
            new CovenantDispatchAdmission(receipt, acknowledged.Value, sensitivity));

    }

    /// <summary>
    /// Mints the attempt's admission receipt, or reports that this attempt had no plan to admit.
    /// </summary>
    /// <remarks>
    /// A tainted-history dispatch with no Covenant plan is a real disclosure with no admission: there
    /// is no plan, no candidate vector, and nothing that could honestly be signed as admitted. Forcing
    /// a receipt there would fabricate one.
    /// </remarks>
    private static CovenantAdmissionReceipt? BuildReceipt(
        CovenantTurnScope scope,
        CovenantDispatchPlan plan,
        ProviderCallEnvelope providerCall,
        ulong attemptOrdinal)
    {

        if (scope.Plan is not { } turnPlan || plan.Admission is not { } admission)
        {

            return null;

        }

        return new CovenantAdmissionReceipt(
            turnPlan,
            attemptOrdinal,
            scope.Collector?.CurrentBranchId ?? scope.LogicalTurnId,
            attemptOrdinal,
            null,
            providerCall,
            plan.AvailableTokenBudget,
            admission.Candidates);

    }

    private async ValueTask<bool> ReadHistoryTaintAsync(Guid? sessionId, CancellationToken cancellationToken)
    {

        if (sessionId is not { } id || id == Guid.Empty)
        {

            return false;

        }

        Result<SessionSensitivityProjection> projection = await sensitivityLedger
            .ReadSessionProjectionAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (projection.IsFailure)
        {

            // Fail toward taint. "We could not read the label" and "there is no label" are different
            // facts, and treating the first as the second is exactly how an unlabelled tainted
            // dispatch would leave without a receipt.
            logger.LogWarning(
                "Session sensitivity could not be read ({ErrorCode}); the turn is treated as tainted.",
                projection.Error.Code);

            return true;

        }

        return projection.Value.IsTainted;

    }

    private Guid ResolveInstallationIdentity() =>
        Guid.TryParse(authority.Current?.InstallationIdentity, out Guid installationId)
            ? installationId
            : Guid.Empty;

    /// <summary>
    /// The content-free identity of the destination this payload is about to reach.
    /// </summary>
    /// <remarks>
    /// A destination class and an opaque identity, never a URL. The journal is accounting: it records
    /// that something left and where it left to in the coarsest terms that still distinguish two
    /// providers, because a disclosure ledger that stores endpoints becomes a second copy of the
    /// configuration an operator asked Arcanum to keep out of its logs.
    /// </remarks>
    private static CovenantDigest DestinationIdentity(string providerIdentity) =>
        new(System.Security.Cryptography.SHA256.HashData(
        [
            .. System.Text.Encoding.ASCII.GetBytes("Arcanum.Covenant.ProviderDestinationIdentity.v1"),
            0x00,
            .. System.Text.Encoding.UTF8.GetBytes(providerIdentity),
        ]));

}
