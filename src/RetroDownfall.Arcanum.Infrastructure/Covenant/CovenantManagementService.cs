using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Everything an operator can ask about their own Covenant without changing it.
/// </summary>
/// <remarks>
/// Every read borrows the caller's lease rather than acquiring one, so a page and the page after it
/// are answered under the same admission generation. The one exception is <c>Explain</c>, which
/// builds its own snapshot for the purpose and therefore detaches its own lease: borrowing the
/// caller's would either explain a different snapshot than the one it evaluated, or force a nested
/// acquisition, and a nested acquisition inside a drain is a deadlock.
///
/// <para>Cursors are opaque and authenticated. The facts a cursor binds are chosen by the storage
/// layer; this service only encrypts them, and refuses a cursor whose dataset has moved with
/// <c>StaleCursor</c> rather than silently mixing two pages.</para>
/// </remarks>
internal sealed class CovenantManagementService(
    ICovenantStore store,
    ICovenantLinker linker,
    ICovenantOperationGate gate,
    ICovenantAvailability availability,
    ICovenantEnvelopeCodec codec,
    ICampaignAvailabilityReader campaigns,
    ICovenantSearchIndex? searchIndex = null,
    CovenantSearchQueryCompiler? searchCompiler = null) : ICovenantManagementService
{
    /// <summary>How long a page cursor stays usable.</summary>
    /// <remarks>
    /// The cursor binds a dataset generation and a canonical sequence, so a stale one is refused on
    /// its content rather than its age. The lifetime is a second bound, not the primary one.
    /// </remarks>
    private static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(15);

    public async ValueTask<Result<CovenantPageDto>> ListAsync(
        CovenantListRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        CovenantDigest filterDigest = FilterDigest(request);

        Result<CovenantListCursorBody?> after = ResolveListCursor(request.Cursor, filterDigest);

        if (after.IsFailure)
        {
            return after.Error;
        }

        Result<CovenantListPage> page = await store
            .ReadListPageAsync(
                new CovenantListQuery(
                    request.Scope,
                    request.CampaignId,
                    request.Lane,
                    request.Lifecycle,
                    request.EffectiveLimit,
                    after.Value?.Keyset),
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (page.IsFailure)
        {
            return page.Error;
        }

        // The cursor binds the dataset it was issued against, and the page was read from whatever the
        // dataset is now. A continuation across a change would splice two result sets.
        if (after.Value is { } continuation)
        {
            Result continued = CheckContinuation(CovenantCursorBodyValidator.Validate(
                continuation,
                filterDigest,
                SnapshotOf(page.Value),
                codec.KeySnapshot.MasterKeyVersion));

            if (continued.IsFailure)
            {
                return continued.Error;
            }
        }

        return Project(page.Value, request.EffectiveForCampaignId, filterDigest);
    }

    public async ValueTask<Result<CovenantPageDto>> QueryAsync(
        CovenantQueryRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (searchIndex is null || searchCompiler is null)
        {
            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "Covenant free-text inspection is not available on this host.");
        }

        ICovenantSearchIndex availableSearchIndex = searchIndex;

        CovenantSearchQueryCompiler availableSearchCompiler = searchCompiler;

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        Result<CovenantCompiledSearchTerms> compiled = availableSearchCompiler.Compile(request.Query);

        if (compiled.IsFailure)
        {
            return compiled.Error;
        }

        CovenantDigest queryDigest = new(SHA256.HashData(Encoding.UTF8.GetBytes(compiled.Value.MatchExpression)));

        Result<SearchCursor?> cursor = ResolveSearchCursor(request.Cursor);

        if (cursor.IsFailure)
        {
            return cursor.Error;
        }

        // One read of the published tier state serves the query and the health report it returns, so the
        // rows and the report describe the same tier.
        CovenantCapabilityState accelerator = availability.Current.Accelerator;

        CovenantSearchQuery query = new(
            compiled.Value,
            request.Scope,
            request.CampaignId,
            request.Lane,
            request.Lifecycle,
            request.EffectiveLimit,
            cursor.Value?.Keyset,
            accelerator);

        Result<CovenantSearchPage> searched = await availableSearchIndex
            .SearchAsync(query, readLease, cancellationToken)
            .ConfigureAwait(false);

        if (searched.IsFailure)
        {
            return searched.Error;
        }

        CovenantSearchPage page = searched.Value;

        CovenantDigest filterDigest = QueryFilterDigest(request, queryDigest, page.ExecutionMode);

        if (cursor.Value is { } continuation)
        {
            if (continuation.ExecutionMode != page.ExecutionMode
                || continuation.FilterDigest != filterDigest)
            {
                return new Error(ErrorCodes.Covenant.StaleCursor, "This Covenant cursor belongs to a different query.");
            }

            if (continuation.Sources != page.Sources)
            {
                return new Error(ErrorCodes.Covenant.StaleCursor, "The Covenant changed after this cursor was issued.");
            }
        }

        // The heads arrive with the hits, read in the snapshot that ranked them, so the page is one
        // dataset and a first page has nothing to be stale against.
        List<CovenantHeadDto> items = [.. page.Hits.Select(hit => Head(hit.Head, request.EffectiveForCampaignId))];

        return new CovenantPageDto(
            [.. items],
            page.NextKeyset is { } next ? IssueSearchCursor(new SearchCursor(
                page.ExecutionMode,
                filterDigest,
                page.Sources,
                next)) : null,
            Hex(filterDigest),
            new CovenantSearchHealthDto(
                // The same rule status applies, over the same published tier state the query was answered
                // under. A page answered from the index is the synchronized case, and one that was not is
                // waiting on the outbox or on a rebuild, which status calls synchronizing too, unless the
                // tier itself is degraded, which status reports as degraded.
                CovenantSearchHealthRule.State(
                    acceleratorUnavailable: page.Guidance is CovenantSearchRebuildGuidance.AcceleratorUnavailable,
                    acceleratorDegraded: accelerator is CovenantCapabilityState.Degraded,
                    synchronized: page.ExecutionMode is CovenantSearchExecutionMode.Fts),
                page.ExecutionMode,
                page.Guidance),
            page.Truncated || page.NextKeyset is not null,
            page.Truncated
                ? CovenantPageTruncation.FallbackCandidateCapReached
                : page.NextKeyset is not null
                    ? CovenantPageTruncation.PageSizeReached
                    : CovenantPageTruncation.None);
    }

    public async ValueTask<Result<CovenantDetailDto>> DetailAsync(
        CovenantDetailRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        CovenantOperationScope scope = request.Scope is CovenantScope.Global || request.CampaignId is not { } id
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(id);

        Result<CovenantDetail> detail = await store
            .ReadDetailAsync(
                new CovenantDetailQuery(scope, new CovenantKey(request.Key).Value),
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (detail.IsFailure)
        {
            return detail.Error;
        }

        return new CovenantDetailDto(
            request.Scope,
            request.CampaignId,
            detail.Value.NormalizedKey,
            detail.Value.EntryId,
            detail.Value.ConfirmedHead is { } confirmed ? Head(confirmed, null) : null,
            detail.Value.ProposedHead is { } proposed ? Head(proposed, null) : null,
            detail.Value.KeyEpoch,

            // Provenance is a separate bounded read per version, so detail reports the heads and the
            // sources route reports their leaves. Folding an unbounded read into a lookup would make
            // one key's detail cost depend on how much the agent attached to it.
            ConfirmedSources: null,
            ProposedSources: null,
            detail.Value.ConfirmedCuration,
            detail.Value.ProposedCuration);
    }

    public async ValueTask<Result<CovenantVersionPageDto>> VersionsAsync(
        CovenantVersionsRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        CovenantDigest filterDigest = VersionFilterDigest(request);

        Result<CovenantVersionCursorBody?> after = ResolveVersionCursor(request.Cursor, filterDigest);

        if (after.IsFailure)
        {
            return after.Error;
        }

        Result<CovenantVersionPage> page = await store
            .ReadVersionPageAsync(
                new CovenantVersionQuery(request.EntryId, request.Lane, request.EffectiveLimit, after.Value?.Keyset),
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (page.IsFailure)
        {
            return page.Error;
        }

        if (after.Value is { } continuation)
        {
            Result continued = CheckContinuation(CovenantCursorBodyValidator.Validate(
                continuation,
                filterDigest,
                SnapshotOf(page.Value),
                codec.KeySnapshot.MasterKeyVersion));

            if (continued.IsFailure)
            {
                return continued.Error;
            }
        }

        return new CovenantVersionPageDto(
            [.. page.Value.Items.Select(Version)],
            NextCursor(page.Value, filterDigest),
            Hex(filterDigest),
            page.Value.Truncated);
    }

    public async ValueTask<Result<CovenantSourcesDto>> SourcesAsync(
        CovenantSourcesRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {
            return validated.Error;
        }

        Result<CovenantSourcePage> page = await store
            .ReadSourcePageAsync(new CovenantSourceQuery(request.VersionId), readLease, cancellationToken)
            .ConfigureAwait(false);

        return page.IsFailure ? page.Error : Sources(page.Value);
    }

    /// <summary>
    /// Runs the real loader and linker over a snapshot made for the question.
    /// </summary>
    /// <remarks>
    /// Explain is the one read that must not answer from a cache: an operator asking why a preference
    /// is not being honoured needs the decision the live code would make now, not the decision some
    /// earlier turn recorded.
    ///
    /// <para>It evaluates the Campaign the request names. Evaluating Global-only regardless was not a
    /// narrower answer, it was a wrong one: the reply still carried the Campaign identity back, so an
    /// operator reading a Campaign explanation was shown Global sections under their Campaign's name,
    /// and the Campaign Proposed Section — the only place an agent's proposal is ever rendered — was
    /// empty for every Campaign there has ever been. That made an agent-authored proposal impossible
    /// to read on any surface this build maps, and a proposal an operator cannot read is one they can
    /// neither confirm nor retire.</para>
    /// </remarks>
    public async ValueTask<CovenantLeasedServiceResult<CovenantExplainDto>> ExplainAsync(
        CovenantExplainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        Result<CanonicalCampaignContext> resolved = validated.IsFailure
            ? Result<CanonicalCampaignContext>.Failure(validated.Error)
            : await ResolveEvaluationCampaignAsync(request.CampaignId, cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure)
        {
            return CovenantLeasedServiceResult<CovenantExplainDto>.Create(
                Result<CovenantExplainDto>.Failure(resolved.Error),
                NullLease.Instance);
        }

        CanonicalCampaignContext campaign = resolved.Value;

        Result<CovenantTurnLease> lease = await gate
            .AcquireTurnAsync(campaign, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {
            return CovenantLeasedServiceResult<CovenantExplainDto>.Create(
                Result<CovenantExplainDto>.Failure(lease.Error),
                NullLease.Instance);
        }

        // From here the registration is the only owner of the lease until Create hands it to the
        // response writer, which disposes it exactly once. Anything that unwinds before that point —
        // the request token cancelling the snapshot read, a storage fault, a linker that throws —
        // would otherwise leave a live registration no later exclusive acquisition can ever drain.
        bool transferred = false;

        try
        {
            CovenantAvailabilitySnapshot health = availability.Current;

            Result<CovenantTurnSnapshot> snapshot = await store
                .ReadTurnSnapshotAsync(campaign, lease.Value, cancellationToken)
                .ConfigureAwait(false);

            CovenantLeasedServiceResult<CovenantExplainDto> leased;

            if (snapshot.IsFailure)
            {
                leased = CovenantLeasedServiceResult<CovenantExplainDto>.Create(
                    Result<CovenantExplainDto>.Failure(snapshot.Error),
                    lease.Value);
            }
            else
            {
                Result<CovenantTurnPlan> plan = linker.Link(snapshot.Value);

                leased = CovenantLeasedServiceResult<CovenantExplainDto>.Create(
                    plan.IsFailure
                        ? Result<CovenantExplainDto>.Failure(plan.Error)
                        : Result<CovenantExplainDto>.Success(Explain(request, health, plan.Value)),
                    lease.Value);
            }

            // Set only once Create has returned: it can throw, and a lease it did not take is still ours.
            transferred = true;

            return leased;
        }
        finally
        {
            if (!transferred)
            {
                await lease.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Builds the canonical context an explanation evaluates under, from the Campaign it named.
    /// </summary>
    /// <remarks>
    /// The decision itself is <see cref="CanonicalCampaignResolutionPolicy"/>, the same pure table a
    /// live turn resolves through, so an explanation cannot bind a Campaign a turn would refuse. The
    /// two sources a turn also offers — the Session's immutable binding and a supplied working
    /// directory — are genuinely absent here: an inspection call is made from no Session and stands in
    /// no directory, and inventing either would be asserting a fact rather than reading one. Every
    /// other row of the table still applies, including the one that refuses a Campaign that no longer
    /// exists.
    ///
    /// <para>The availability generation is read through the same reader the turn resolver uses, and
    /// it is what the gate binds the turn lease to; a context that carried a stale generation would
    /// hold a lease over a Campaign that had already been deleted underneath it.</para>
    /// </remarks>
    private async ValueTask<Result<CanonicalCampaignContext>> ResolveEvaluationCampaignAsync(
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        if (campaignId is not { } named)
        {
            return Result<CanonicalCampaignContext>.Success(CanonicalCampaignContext.GlobalOnly);
        }

        Result<long?> generation = await campaigns
            .FindAvailabilityGenerationAsync(named, cancellationToken)
            .ConfigureAwait(false);

        return generation.IsFailure
            ? Result<CanonicalCampaignContext>.Failure(generation.Error)
            : CanonicalCampaignResolutionPolicy.Resolve(
                session: null,
                named,
                workspace: null,
                workingDirectorySupplied: false,
                generation.Value);
    }

    /// <summary>
    /// What this installation holds, in counts and ceilings only.
    /// </summary>
    /// <remarks>
    /// Acquires its own installation read capability rather than borrowing one, because the port
    /// takes no lease: status is reachable from the ordinary memory surface, and a census that
    /// crosses every Campaign needs a capability that does too.
    ///
    /// <para>A census that cannot be read reports zero counts beside honest health rather than
    /// failing the whole status call. An operator asking "is my memory working" is entitled to the
    /// health answer even when the count behind it is momentarily unavailable.</para>
    ///
    /// <para>The zero is only defensible because <see cref="CovenantStatusDto.Census"/> says whether
    /// it is a measurement. Canonical health cannot carry that sentence on its own: the gate refuses
    /// the installation capability whenever an exclusive operation is closing the scope, and refuses
    /// the census on any authority, dataset, or capability-generation move — all of which happen while
    /// the tier reports itself Healthy. Without the field an operator was shown "available, no
    /// entries" for an installation that was simply not read.</para>
    ///
    /// <para>A disabled installation is not censused at all. Health is still answered — that is the
    /// question a disabled operator is asking — but the scan itself opens a canonical connection,
    /// which latches <c>CovenantProcessResidence</c> one way and closes the offline host-tools
    /// transition for the whole process. Paying that for a count nothing wrote is the worst possible
    /// trade, and it made a bare <c>arcanum memory status</c> enough to close the transition on an
    /// installation that never enabled Covenant (§10.12).</para>
    /// </remarks>
    public async ValueTask<Result<CovenantStatusDto>> StatusAsync(CancellationToken cancellationToken)
    {
        CovenantAvailabilitySnapshot snapshot = availability.Current;

        CovenantScopeCensus census = CovenantScopeCensus.Empty;

        CovenantCensusReadState state = CovenantCensusReadState.Refused;

        // Gated on the feature, and deliberately not on reported health. The gate already refuses a
        // capability over a tier it cannot serve, so a health check here would only duplicate that
        // refusal — and it would suppress the counts of a degraded-but-readable tier, telling an
        // operator they held nothing when the truth was that search was slower.
        if (snapshot.FeatureEnabled)
        {
            Result<CovenantInstallationReadLease> lease = await gate
                .AcquireInstallationReadAsync(cancellationToken)
                .ConfigureAwait(false);

            if (lease.IsSuccess)
            {
                await using CovenantInstallationReadLease owned = lease.Value;

                Result<CovenantScopeCensus> read = await store
                    .ReadScopeCensusAsync(owned, cancellationToken)
                    .ConfigureAwait(false);

                if (read.IsSuccess)
                {
                    census = read.Value;

                    state = CovenantCensusReadState.Read;
                }
                else
                {
                    state = CovenantCensusReadState.Failed;
                }
            }
        }

        return new CovenantStatusDto(
            snapshot.FeatureEnabled,
            snapshot.Canonical is CovenantCapabilityState.Healthy,
            state,
            [
                .. census.Rows.Select(static row => new CovenantScopeCountDto(
                    row.Scope,
                    row.Lane,
                    row.Lifecycle,
                    row.Count)),
            ],
            census.GlobalConfirmedRenderedBytes,
            census.MaxCampaignConfirmedRenderedBytes,
            census.MaxCampaignProposedRenderedBytes,

            // One field stands for three placement ceilings, which is only honest while the three are
            // the same number. CovenantDomainContractTests pins them equal, so giving one section a
            // ceiling of its own fails there rather than here, where an operator would be comparing a
            // Campaign total against a Global bound and see nothing wrong.
            CovenantLimits.MaxGlobalConfirmedRenderedBytes,
            new CovenantSearchHealthDto(
                SearchHealth(snapshot),
                ExecutionMode(snapshot),
                RebuildGuidance(snapshot)),
            CovenantRetentionSummary,

            // Canonical first because it is the more severe of the two: a caller that cannot read the
            // canonical tier has a worse problem than one whose search is slow, and reporting only the
            // canonical code would leave an accelerator failure with no code at all.
            snapshot.CanonicalDiagnosticCode ?? snapshot.AcceleratorDiagnosticCode);
    }

    /// <summary>
    /// The four states search can actually be in, from the published availability snapshot.
    /// </summary>
    /// <remarks>
    /// Derived rather than named, and derived by <see cref="CovenantSearchHealthRule"/> rather than at each
    /// caller. This DTO is frozen and reaches an operator through the API, the CLI, and the ordinary memory
    /// status block alike, and a search page carries the same one; a second producer computing its own
    /// answer would give one contract several fields that mean different things depending on which of
    /// them replied.
    /// </remarks>
    private static CovenantSearchHealthState SearchHealth(CovenantAvailabilitySnapshot snapshot) =>
        CovenantSearchHealthRule.State(
            acceleratorUnavailable: snapshot.Accelerator is CovenantCapabilityState.Unavailable,
            acceleratorDegraded: snapshot.Accelerator is CovenantCapabilityState.Degraded,
            synchronized: snapshot.FtsSynchronization is CovenantFtsSynchronizationState.Synchronized);

    /// <summary>
    /// How the next query would run, by the same rule the store itself applies.
    /// </summary>
    /// <remarks>
    /// The store answers from the accelerator only while it is healthy and synchronized, and falls
    /// back to the bounded canonical scan otherwise. Reporting a fixed mode here would tell an
    /// operator their search was indexed while it was in fact scanning, or the reverse.
    /// </remarks>
    private static CovenantSearchExecutionMode ExecutionMode(CovenantAvailabilitySnapshot snapshot) =>
        snapshot.Accelerator is CovenantCapabilityState.Healthy
            && snapshot.FtsSynchronization is CovenantFtsSynchronizationState.Synchronized
            ? CovenantSearchExecutionMode.Fts
            : CovenantSearchExecutionMode.CanonicalFallback;

    /// <summary>
    /// The one remediation this snapshot actually calls for, most specific first.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantSearchHealthRule.Guidance"/> decides, and a search page asks the same function
    /// with the facts it was answered from, so the two routes agree on a state. The order it applies, and
    /// why, is documented there: an unavailable accelerator cannot be waited out, a synchronized snapshot
    /// asks for nothing even while a fresh installation's recorded rebuild is still owed, a published
    /// tuple for this dataset means the outbox will carry search the rest of the way, and only without
    /// one does the recorded rebuild decide, which includes a restore that kept its heads: the outbox will
    /// not adopt a projection it cannot replay those heads onto.
    /// </remarks>
    private static CovenantSearchRebuildGuidance RebuildGuidance(CovenantAvailabilitySnapshot snapshot) =>
        CovenantSearchHealthRule.Guidance(
            acceleratorUnavailable: snapshot.Accelerator is CovenantCapabilityState.Unavailable,
            synchronized: snapshot.FtsSynchronization is CovenantFtsSynchronizationState.Synchronized,
            outboxCanContinue: snapshot.Accelerator is CovenantCapabilityState.Healthy
                && snapshot.DatasetGeneration is { } dataset
                && snapshot.AppliedDatasetGeneration == dataset,
            rebuildOwed: snapshot.RebuildRequired);

    private static CovenantExplainDto Explain(
        CovenantExplainRequest request,
        CovenantAvailabilitySnapshot health,
        CovenantTurnPlan plan) =>
        new(
            request.CampaignId,
            health.FeatureEnabled,
            health.Canonical is CovenantCapabilityState.Healthy,
            Hex(plan.Snapshot.Digest),
            Hex(plan.Digest),
            [
                Section(plan.GlobalConfirmedSection, request.ShowContent),
                Section(plan.CampaignConfirmedSection, request.ShowContent),
                Section(plan.CampaignProposedSection, request.ShowContent),
            ],
            [.. plan.Decisions.Select(Decision)],
            ConfirmedTokens: 0,
            ProposedTokens: 0,

            // The plan alone cannot say what a provider attempt admitted: admission depends on the
            // attempt's own budget and tokenizer. Reporting the plan's decisions and leaving admission
            // unclaimed is the honest shape for a question asked outside any turn.
            CovenantAdmissionDecision.Admitted,
            request.ShowContent);

    private static CovenantExplainSectionDto Section(CovenantTurnSection section, bool showContent) =>
        new(
            section.Placement,
            section.Candidates.Length,
            section.RenderedBytes.Length,
            EstimatedTokens: 0,
            Hex(section.Digest),
            showContent && !section.RenderedBytes.IsEmpty
                ? System.Text.Encoding.UTF8.GetString(section.RenderedBytes.AsSpan())
                : null);

    private static CovenantExplainDecisionDto Decision(CovenantPlanCandidateDecision decision) =>
        new(
            decision.Candidate.EntryId,
            decision.Candidate.VersionId,
            decision.Candidate.NormalizedKey.Value,
            decision.Candidate.Lane,
            decision.Decision,
            decision.Placement,
            decision.ShadowingVersionId,
            decision.Candidate.CompiledBytes,
            Hex(decision.Candidate.FragmentDigest));

    private CovenantPageDto Project(
        CovenantListPage page,
        Guid? effectiveForCampaignId,
        CovenantDigest filterDigest) =>
        new(
            [.. page.Items.Select(item => Head(item, effectiveForCampaignId))],
            NextCursor(page, filterDigest),
            Hex(filterDigest),
            new CovenantSearchHealthDto(
                CovenantSearchHealthState.Healthy,
                CovenantSearchExecutionMode.CanonicalFallback,
                CovenantSearchRebuildGuidance.None),
            page.Truncated,
            page.Truncated ? CovenantPageTruncation.PageSizeReached : CovenantPageTruncation.None);

    /// <summary>
    /// Projects one head, reporting effective state only when an evaluation Campaign was named.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantEffectiveShadowState.NotEvaluated"/> is not a hedge. Whether a Global entry
    /// is shadowed depends on which Campaign is asking, so a page that guessed would be reporting a
    /// fact about a Campaign the operator did not name.
    /// </remarks>
    private static CovenantHeadDto Head(CovenantHeadItem item, Guid? effectiveForCampaignId) =>
        new(
            item.EntryId,
            item.VersionId,
            item.Scope,
            item.CampaignId,
            item.NormalizedKey,
            item.Lane,
            item.LaneRevision,
            item.Lifecycle,
            item.Origin,
            item.AuthoredHash is { } authored ? Hex(authored) : null,
            item.RenderedHash is { } rendered ? Hex(rendered) : null,
            item.CompiledByteCost,
            item.ProvenanceCount,
            Hex(item.ProvenanceDigest),
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            effectiveForCampaignId is null
                ? CovenantEffectiveShadowState.NotEvaluated
                : CovenantEffectiveShadowState.NotShadowed,
            effectiveForCampaignId is null
                ? CovenantEffectiveMaterialization.NotEvaluated
                : item.Lane is CovenantLane.Proposed
                    ? CovenantEffectiveMaterialization.ReviewOnly
                    : CovenantEffectiveMaterialization.Eligible);

    private static CovenantVersionDto Version(CovenantVersionItem item) =>
        new(
            item.VersionId,
            item.EntryId,
            item.Lane,
            item.LaneRevision,
            item.Operation,
            item.Origin,
            item.AuthoredHash is { } authored ? Hex(authored) : null,
            item.RenderedHash is { } rendered ? Hex(rendered) : null,
            item.CompiledByteCost,
            item.CompilerPolicyVersion,
            item.RendererPolicyVersion,
            item.PredecessorVersionId,
            item.MutationId,
            item.ProvenanceCount,
            Hex(item.ProvenanceDigest),
            item.CreatedAtUtc);

    /// <summary>The retention sentence every Covenant surface reports, in one place.</summary>
    private const string CovenantRetentionSummary =
        "Durable immutable versions until an operator erases the entry, its Campaign is deleted, or a "
        + "Covenant reset, family reinitialize, or installation erasure removes it; retirement keeps "
        + "history inspectable. Content already sent to a provider is outside every local erasure path.";

    private static CovenantSourcesDto Sources(CovenantSourcePage page) =>
        new(
            page.VersionId,
            [
                .. page.Items.Select(static source => new CovenantSourceDto(
                    source.Ordinal,
                    source.AttachmentId,
                    source.AttachmentVersionIdentity,
                    source.LogicalKey,
                    Hex(source.ContentHash),
                    source.SourceRangeKind,
                    source.SourceStart,
                    source.SourceEnd,
                    source.SourceTurnId,
                    source.MaterializationReference)),
            ],
            page.StoredProvenanceCount,
            Hex(page.StoredProvenanceDigest),
            Hex(page.RecomputedProvenanceDigest),
            page.DigestMatches);

    private string? NextCursor(CovenantListPage page, CovenantDigest filterDigest) =>
        page.NextKeyset is not { } keyset
            ? null
            : Issue(CovenantCursorBodyCodec.Encode(new CovenantListCursorBody(
                CovenantCursorEndpoint.List,
                filterDigest,
                page.DatasetGeneration,
                page.CanonicalSearchSequence,
                page.CoreCampaignDeletionSequence,
                codec.KeySnapshot.MasterKeyVersion,
                keyset)));

    private string? NextCursor(CovenantVersionPage page, CovenantDigest filterDigest) =>
        page.NextKeyset is not { } keyset
            ? null
            : Issue(CovenantCursorBodyCodec.Encode(new CovenantVersionCursorBody(
                filterDigest,
                page.DatasetGeneration,
                page.CanonicalSearchSequence,
                CoreCampaignDeletionSequence: 0,
                codec.KeySnapshot.MasterKeyVersion,
                keyset)));

    /// <summary>
    /// The dataset facts a canonical list page was read under, in the shape the cursor validator compares.
    /// </summary>
    private static CovenantSearchSourceSnapshot SnapshotOf(CovenantListPage page) =>
        new(
            page.DatasetGeneration,
            page.CanonicalSearchSequence,
            page.CoreCampaignDeletionSequence,
            AppliedDatasetGeneration: null,
            AppliedSearchSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 0);

    /// <summary>
    /// The same, for a version page, whose cursors never bind a Campaign deletion sequence.
    /// </summary>
    private static CovenantSearchSourceSnapshot SnapshotOf(CovenantVersionPage page) =>
        new(
            page.DatasetGeneration,
            page.CanonicalSearchSequence,
            CoreCampaignDeletionSequence: 0,
            AppliedDatasetGeneration: null,
            AppliedSearchSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 0);

    /// <summary>
    /// Maps the validator's verdict onto the error a continuation reports.
    /// </summary>
    /// <remarks>
    /// Stale is a cursor this installation issued whose dataset has since moved, which the operator
    /// answers by asking for a first page. Invalid is one that names a different key version or
    /// endpoint than this installation would have issued.
    /// </remarks>
    private static Result CheckContinuation(CovenantCursorRejection rejection) =>
        rejection switch
        {
            CovenantCursorRejection.None => Result.Success(),

            CovenantCursorRejection.Stale => new Error(
                ErrorCodes.Covenant.StaleCursor,
                "The Covenant changed after this cursor was issued."),

            _ => new Error(
                ErrorCodes.Covenant.InvalidCursor,
                "This Covenant cursor could not be read."),
        };

    private string? Issue(byte[] body)
    {
        Result<string> token = codec.Encode(CovenantEnvelopePurpose.Cursor, body, CursorLifetime);

        // A page that could not mint its continuation reports no continuation rather than an
        // unauthenticated one. The operator sees a short page; nobody sees a forgeable cursor.
        return token.IsSuccess ? token.Value : null;
    }

    private Result<CovenantListCursorBody?> ResolveListCursor(string? cursor, CovenantDigest filterDigest)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return Result<CovenantListCursorBody?>.Success(null);
        }

        Result<CovenantEnvelopeBody> envelope = codec.Decode(CovenantEnvelopePurpose.Cursor, cursor);

        if (envelope.IsFailure)
        {
            return envelope.Error;
        }

        Result<CovenantListCursorBody> body = CovenantCursorBodyCodec.TryDecodeList(envelope.Value.Payload);

        if (body.IsFailure)
        {
            return body.Error;
        }

        // A cursor carried onto a different query is a different question, and answering its second
        // page would splice two result sets together.
        return body.Value.FilterDigest != filterDigest
            ? new Error(
                ErrorCodes.Covenant.StaleCursor,
                "This Covenant cursor belongs to a different query.")
            : Result<CovenantListCursorBody?>.Success(body.Value);
    }

    private Result<CovenantVersionCursorBody?> ResolveVersionCursor(string? cursor, CovenantDigest filterDigest)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return Result<CovenantVersionCursorBody?>.Success(null);
        }

        Result<CovenantEnvelopeBody> envelope = codec.Decode(CovenantEnvelopePurpose.Cursor, cursor);

        if (envelope.IsFailure)
        {
            return envelope.Error;
        }

        Result<CovenantVersionCursorBody> body =
            CovenantCursorBodyCodec.TryDecodeVersion(envelope.Value.Payload);

        if (body.IsFailure)
        {
            return body.Error;
        }

        return body.Value.FilterDigest != filterDigest
            ? new Error(
                ErrorCodes.Covenant.StaleCursor,
                "This Covenant cursor belongs to a different query.")
            : Result<CovenantVersionCursorBody?>.Success(body.Value);
    }

    /// <summary>
    /// The filter a cursor binds, taken from the page size the read will actually use.
    /// </summary>
    /// <remarks>
    /// The clamped size, not the raw one. The raw field is whatever the caller sent, and the digest
    /// refuses anything outside the one-to-two-hundred range with an exception rather than an error —
    /// so a request naming zero pages, which the contract documents as "use the default", left a
    /// mapped route with an unhandled argument failure instead of a page. The clamp is also what keeps
    /// the digest agreeing with the query beside it, which is the comparison a continuation cursor
    /// rests on.
    /// </remarks>
    private static CovenantDigest FilterDigest(CovenantListRequest request) =>
        CovenantDigests.CursorFilter(new CursorFilterDigestInput(
            CovenantCursorEndpoint.List,
            request.Scope,
            request.CampaignId,
            request.EffectiveForCampaignId,
            request.Lane,
            request.Lifecycle,
            QueryDigest: null,
            checked((uint)request.EffectiveLimit),
            CovenantCursorSort.CanonicalHeads));

    private static CovenantDigest VersionFilterDigest(CovenantVersionsRequest request) =>
        CovenantDigests.CursorFilter(new CursorFilterDigestInput(
            CovenantCursorEndpoint.Versions,
            CovenantCursorScopeSelection.Global,
            request.EntryId,
            EvaluationCampaignId: null,
            request.Lane,
            CovenantLifecycle.Any,
            QueryDigest: null,
            checked((uint)request.EffectiveLimit),
            CovenantCursorSort.CanonicalHeads));

    private static CovenantDigest QueryFilterDigest(
        CovenantQueryRequest request,
        CovenantDigest queryDigest,
        CovenantSearchExecutionMode mode) =>
        CovenantDigests.CursorFilter(new CursorFilterDigestInput(
            mode is CovenantSearchExecutionMode.Fts
                ? CovenantCursorEndpoint.FtsQuery
                : CovenantCursorEndpoint.FallbackQuery,
            request.Scope,
            request.CampaignId,
            request.EffectiveForCampaignId,
            request.Lane,
            request.Lifecycle,
            queryDigest,
            checked((uint)request.EffectiveLimit),
            mode is CovenantSearchExecutionMode.Fts
                ? CovenantCursorSort.FtsRank
                : CovenantCursorSort.FallbackHeads));

    private string? IssueSearchCursor(SearchCursor cursor)
    {
        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write((byte)cursor.ExecutionMode);
            writer.Write(cursor.FilterDigest.Bytes);
            writer.Write(cursor.Sources.DatasetGeneration.ToByteArray());
            writer.Write(cursor.Sources.CanonicalSearchSequence);
            writer.Write(cursor.Sources.CoreCampaignDeletionSequence);
            writer.Write(cursor.Sources.AppliedDatasetGeneration?.ToByteArray() ?? Guid.Empty.ToByteArray());
            writer.Write(cursor.Sources.AppliedSearchSequence ?? -1);
            writer.Write(cursor.Sources.AppliedCampaignDeletionSequence ?? -1);
            writer.Write(cursor.Sources.AcceleratorEpoch);
            writer.Write((byte)cursor.Keyset.MatchClass);
            writer.Write(cursor.Keyset.ScoreBits);
            writer.Write(cursor.Keyset.EntryId.ToByteArray());
            writer.Write(cursor.Keyset.VersionId.ToByteArray());
        }

        return Issue(stream.ToArray());
    }

    private Result<SearchCursor?> ResolveSearchCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return Result<SearchCursor?>.Success(null);
        }

        Result<CovenantEnvelopeBody> envelope = codec.Decode(CovenantEnvelopePurpose.Cursor, cursor);

        if (envelope.IsFailure)
        {
            return envelope.Error;
        }

        try
        {
            using MemoryStream stream = new(envelope.Value.Payload, writable: false);

            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadByte() != 1)
            {
                return InvalidSearchCursor();
            }

            CovenantSearchExecutionMode mode = (CovenantSearchExecutionMode)reader.ReadByte();

            CovenantDigest filter = new(reader.ReadBytes(32));

            Guid dataset = new(reader.ReadBytes(16));

            long canonical = reader.ReadInt64();

            long deletion = reader.ReadInt64();

            Guid appliedDatasetValue = new(reader.ReadBytes(16));

            long appliedSearchValue = reader.ReadInt64();

            long appliedDeletionValue = reader.ReadInt64();

            ulong acceleratorEpoch = reader.ReadUInt64();

            CovenantSearchMatchClass matchClass = (CovenantSearchMatchClass)reader.ReadByte();

            ulong scoreBits = reader.ReadUInt64();

            Guid entryId = new(reader.ReadBytes(16));

            Guid versionId = new(reader.ReadBytes(16));

            if (stream.Position != stream.Length
                || !Enum.IsDefined(mode)
                || !Enum.IsDefined(matchClass)
                || entryId == Guid.Empty
                || versionId == Guid.Empty)
            {
                return InvalidSearchCursor();
            }

            CovenantSearchSourceSnapshot sources = new(
                dataset,
                canonical,
                deletion,
                appliedDatasetValue == Guid.Empty ? null : appliedDatasetValue,
                appliedSearchValue < 0 ? null : appliedSearchValue,
                appliedDeletionValue < 0 ? null : appliedDeletionValue,
                acceleratorEpoch);

            return Result<SearchCursor?>.Success(new SearchCursor(
                mode,
                filter,
                sources,
                new CovenantSearchKeyset(matchClass, scoreBits, entryId, versionId)));
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
        {
            return InvalidSearchCursor();
        }
    }

    private static Result<SearchCursor?> InvalidSearchCursor() =>
        Result<SearchCursor?>.Failure(new Error(
            ErrorCodes.Covenant.InvalidCursor,
            "This Covenant cursor could not be read."));

    private sealed record SearchCursor(
        CovenantSearchExecutionMode ExecutionMode,
        CovenantDigest FilterDigest,
        CovenantSearchSourceSnapshot Sources,
        CovenantSearchKeyset Keyset);

    private static string Hex(CovenantDigest digest) => Convert.ToHexStringLower(digest.Bytes);

    /// <summary>
    /// The lease a failed acquisition still has to hand back.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantLeasedServiceResult{T}"/> transfers a live lease to the response writer, so
    /// it requires one even for a refusal. This is the inert one: it revalidates successfully because
    /// it covers nothing, and disposing it releases nothing because it holds nothing.
    /// </remarks>
    private sealed class NullLease : ICovenantOperationLease
    {
        internal static NullLease Instance { get; } = new();

        public CovenantOperationLeaseSnapshot Snapshot => throw new InvalidOperationException(
            "A refused Covenant read has no lease snapshot.");

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
