using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

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

    public CovenantTurnAbsence Absence => _context.Absence;

    public bool HasPlan => _context.HasPlan;

    public CovenantTurnPlan? Plan => _context.Plan;

    public ICovenantMutationCollector? Collector => _context.Collector;

    /// <summary>The bounded head read this turn's staging tool calls may make.</summary>
    public ICovenantTurnHeadProbe? HeadProbe => _context.HeadProbe;

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
/// What one provider attempt may inject, decided before its prompt is built.
/// </summary>
/// <remarks>
/// Two phases exist because the admission receipt has to freeze the exact bytes sent, and which
/// bytes those are is what this phase decides. Measuring candidate content against the attempt's own
/// budget therefore happens first, against the reused plan, and the receipt is minted afterwards
/// over the prompt that measurement produced.
/// </remarks>
public sealed record CovenantDispatchPlan(CovenantPromptContent Content, CovenantAdmissionPlan? Admission)
{

    /// <summary>The dispatch that injects nothing, for a turn with no plan.</summary>
    public static CovenantDispatchPlan Empty { get; } = new(CovenantPromptContent.None, null);

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

            return new CovenantTurnScope(
                CovenantTurnContext.Absent(CovenantTurnAbsence.NotEligible),
                Guid.Empty,
                sessionId,
                tainted);

        }

        Result<CovenantTurnContext> begun = await contextProvider
            .BeginTurnAsync(invocation, logicalTurnId, cancellationToken)
            .ConfigureAwait(false);

        if (begun.IsFailure)
        {

            logger.LogWarning(
                "Covenant turn acquisition failed with {ErrorCode}; the turn proceeds without Covenant content.",
                begun.Error.Code);

            return new CovenantTurnScope(
                CovenantTurnContext.Absent(CovenantTurnAbsence.CapabilityUnavailable),
                logicalTurnId,
                sessionId,
                tainted);

        }

        return new CovenantTurnScope(begun.Value, logicalTurnId, sessionId, tainted);

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

        return new CovenantDispatchPlan(admission.AdmittedContent, admission);

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
    /// A clean call on a clean Session performs no database work at all and returns no receipt — that
    /// absence is the disabled-path guarantee, not an oversight. Every other call fails closed: if the
    /// journal cannot commit, the bytes do not leave.
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

            return Result<CovenantDispatchAdmission>.Success(
                new CovenantDispatchAdmission(null, null, sensitivity));

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
            admission.EstimatedAdmittedTokens,
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
