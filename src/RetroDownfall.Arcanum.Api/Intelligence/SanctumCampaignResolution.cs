using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Decides which Campaign's Sanctum governs a turn or a web-research run.
/// </summary>
/// <remarks>
/// A Sanctum is a property of the Campaign a turn is bound to, never of how the working directory happens to be spelled, and a refused
/// resolution is never a reason to govern less strictly. Every source the request names is a claim about which Campaign the turn
/// belongs to, and the stricter Sanctum among the claims governs:
///
/// <list type="number">
/// <item>The canonical binding: the invocation's canonical context when the surface resolved one, otherwise the answer of
/// <see cref="ICanonicalCampaignContextResolver"/> for the request's Session, explicit Campaign and working directory (internal callers pass
/// no canonical context, and a subagent or an Apprentice is as contained as the operator's own turn). That resolution recognises a working
/// directory by the physical identity of its registered root and its ancestors.</item>
/// <item>When the canonical seam refuses the combination (the sources disagree), each source is asked on its own: the Session's binding,
/// the explicit Campaign, and the working directory's registered identity. The disagreement says nothing about which source is right, so a
/// Campaign any of them names stays a claim. A source the resolver fails on for a reason other than "it names no Campaign" leaves the
/// Sanctum unreadable.</item>
/// <item>The registered Campaign whose root contains the working directory (<see cref="ICampaignRepository.GetByContainedPathAsync"/>).
/// The physical identity of a root is only known once the root has been registered, and the Campaign table's path is the registry every
/// installation has, so a directory below it, or the root spelled differently, is still that Campaign's workspace. The canonical binding
/// and the containing root can name different Campaigns (a Campaign nested inside another), and then the stricter of the two governs.</item>
/// </list>
///
/// The strictest claim wins: a Strict Sanctum over an audit-only one over one that is switched off, and a Campaign whose policy cannot be
/// read over all of them (<see cref="Resolved.PolicyUnreadable"/>), so the caller refuses what that Sanctum would have judged. A
/// directory that no registered root contains, on a turn bound to no Campaign, stays unrestricted. When two claims are equally strict the
/// earlier one in the list above governs.
/// </remarks>
internal static class SanctumCampaignResolution
{
    /// <summary>
    /// The governing Campaign: its identifier and row, and whether its Sanctum policy could not be read. <see cref="CampaignId"/> is
    /// <see langword="null"/> when nothing binds the turn to a Campaign.
    /// </summary>
    internal readonly record struct Resolved(Guid? CampaignId, Campaign? Loaded, bool PolicyUnreadable = false);

    internal static async Task<Resolved> ResolveAsync(
        Guid? sessionId,
        Guid? explicitCampaignId,
        string? workingDirectory,
        CanonicalCampaignContext? known,
        ICanonicalCampaignContextResolver resolver,
        ICampaignRepository campaigns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        ArgumentNullException.ThrowIfNull(campaigns);

        string? directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory;

        Claims claims = new();

        if (known is { } resolved && resolved != default)
        {
            claims.Add(resolved.CampaignId);
        }
        else if (sessionId is not null || explicitCampaignId is not null || directory is not null)
        {
            Result<CanonicalCampaignContext> answer = await resolver
                .ResolveAsync(
                    new CanonicalCampaignResolutionRequest(sessionId, explicitCampaignId, directory),
                    cancellationToken)
                .ConfigureAwait(false);

            if (answer.IsSuccess)
            {
                claims.Add(answer.Value.CampaignId);
            }
            else
            {
                await ClaimEverySourceAsync(
                        sessionId,
                        explicitCampaignId,
                        directory,
                        answer,
                        resolver,
                        claims,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        Campaign? containing = null;

        if (directory is not null)
        {
            containing = await campaigns
                .GetByContainedPathAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            if (containing is not null)
            {
                claims.Add(containing.Id);
            }
        }

        return await ChooseAsync(claims, containing, campaigns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the resolver about each source on its own, after it refused them together. With one source there is nothing to separate and the
    /// refusal already is that source's answer.
    /// </summary>
    private static async Task ClaimEverySourceAsync(
        Guid? sessionId,
        Guid? explicitCampaignId,
        string? directory,
        Result<CanonicalCampaignContext> refusal,
        ICanonicalCampaignContextResolver resolver,
        Claims claims,
        CancellationToken cancellationToken)
    {
        int sources = (sessionId is null ? 0 : 1) + (explicitCampaignId is null ? 0 : 1) + (directory is null ? 0 : 1);

        if (sessionId is not null)
        {
            Result<CanonicalCampaignContext> answer = sources == 1
                ? refusal
                : await resolver
                    .ResolveAsync(new CanonicalCampaignResolutionRequest(sessionId, null, null), cancellationToken)
                    .ConfigureAwait(false);

            claims.Absorb(answer, naming: null, SourceKind.Session);
        }

        if (explicitCampaignId is not null)
        {
            Result<CanonicalCampaignContext> answer = sources == 1
                ? refusal
                : await resolver
                    .ResolveAsync(new CanonicalCampaignResolutionRequest(null, explicitCampaignId, null), cancellationToken)
                    .ConfigureAwait(false);

            // A Campaign the resolver cannot find is still the Campaign the request named. Its Sanctum cannot be read, which is a refusal
            // of its own, not a way to run without one.
            claims.Absorb(answer, naming: explicitCampaignId, SourceKind.Explicit);
        }

        if (directory is not null)
        {
            Result<CanonicalCampaignContext> answer = sources == 1
                ? refusal
                : await resolver
                    .ResolveAsync(new CanonicalCampaignResolutionRequest(null, null, directory), cancellationToken)
                    .ConfigureAwait(false);

            claims.Absorb(answer, naming: null, SourceKind.Directory);
        }
    }

    /// <summary>The strictest Sanctum among the claimed Campaigns, the earliest claim winning a tie.</summary>
    private static async Task<Resolved> ChooseAsync(
        Claims claims,
        Campaign? alreadyLoaded,
        ICampaignRepository campaigns,
        CancellationToken cancellationToken)
    {
        Guid? chosenId = null;

        Campaign? chosen = null;

        SanctumStrictness chosenStrictness = SanctumStrictness.Disabled;

        foreach (Guid id in claims.Ids)
        {
            Campaign? candidate = alreadyLoaded is not null && alreadyLoaded.Id == id
                ? alreadyLoaded
                : await TryLoadAsync(campaigns, id, cancellationToken).ConfigureAwait(false);

            SanctumStrictness strictness = candidate is null
                ? SanctumStrictness.Unreadable
                : CampaignRepository.GetSanctumStrictness(candidate);

            if (chosenId is null || strictness > chosenStrictness)
            {
                chosenId = id;

                chosen = candidate;

                chosenStrictness = strictness;
            }
        }

        bool unreadable = claims.Unreadable || chosenStrictness == SanctumStrictness.Unreadable;

        return chosenId is null
            ? new Resolved(null, null, claims.Unreadable)
            : new Resolved(chosenId, unreadable ? null : chosen, unreadable);
    }

    private static async Task<Campaign?> TryLoadAsync(ICampaignRepository campaigns, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await campaigns.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The Campaigns the request's sources name, in precedence order, and whether a source could not be asked at all.</summary>
    private sealed class Claims
    {
        private readonly List<Guid> _ids = [];

        public IReadOnlyList<Guid> Ids => _ids;

        public bool Unreadable { get; private set; }

        public void Add(Guid? campaignId)
        {
            if (campaignId is { } id && !_ids.Contains(id))
            {
                _ids.Add(id);
            }
        }

        /// <summary>
        /// Records what one source on its own says. A Campaign the request names explicitly stays a claim whatever the resolver answers: its
        /// identifier is the evidence, and a Campaign nobody can load is a Sanctum nobody can read. Any other source that names no Campaign
        /// (no such Session, a Session that predates binding, a Campaign that is gone, a conflict that cannot arise from one source) claims
        /// nothing, and so does a Session that has no binding row: a Session's immutable binding is written when its first turn begins, so
        /// one created and not yet used is bound to nothing yet. Any other failure means the source could not be read, and a Sanctum that
        /// cannot be read is not a Sanctum that is off.
        /// </summary>
        public void Absorb(Result<CanonicalCampaignContext> answer, Guid? naming, SourceKind source)
        {
            if (answer.IsSuccess)
            {
                Add(answer.Value.CampaignId);

                return;
            }

            if (naming is not null)
            {
                Add(naming);

                return;
            }

            string code = answer.Error.Code;

            if (code is ErrorCodes.Session.NotFound
                or ErrorCodes.Session.CampaignBindingRequired
                or ErrorCodes.Covenant.CampaignBindingConflict
                or ErrorCodes.Campaign.NotFound
                || (source == SourceKind.Session && code == ErrorCodes.Covenant.IntegrityFailure))
            {
                return;
            }

            Unreadable = true;
        }
    }

    /// <summary>Which of the request's sources an answer is about.</summary>
    private enum SourceKind
    {
        Session,

        Explicit,

        Directory,
    }
}
