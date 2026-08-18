using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Issue #117 — the one shape every direct-deletion route uses to dispatch through the sensitivity
/// purge boundary.
/// </summary>
/// <remarks>
/// Six routes, one sequence: ask the purger about the artifacts, let it remove the labelled ones
/// through the shared kernels, and perform the route's own ordinary delete only for the ones it
/// reported unlabelled. Written once because the interesting mistake is uniform across all six — a
/// route that deleted first and asked afterwards, or that ran its ordinary delete over an artifact the
/// kernel had already removed, would be deleting a row it could no longer distinguish from one somebody
/// else took (§10.20.2).
///
/// <para>A blocked artifact is a refusal, not a silent skip. The artifact is still there and still
/// labelled, and a <c>204 No Content</c> would tell the operator their deletion succeeded.</para>
/// </remarks>
internal static class CovenantSensitiveDeletion
{

    /// <summary>
    /// Dispatches one artifact and reports whether the caller should still delete it itself.
    /// </summary>
    internal static async Task<Result<CovenantSensitivePurgeOutcome>> DispatchAsync(
        ICovenantSensitiveArtifactPurger purger,
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken) =>
        await DispatchAsync(purger, kind, [artifactId], cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Dispatches one bounded page of same-kind artifacts.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="ICovenantSensitiveArtifactPurger.MaxTargets"/> rather than by the caller's
    /// appetite. A bulk delete walks stable identity pages through this method precisely so it cannot
    /// remove an unexamined labelled row through one set-based statement.
    /// </remarks>
    internal static async Task<Result<CovenantSensitivePurgeOutcome>> DispatchAsync(
        ICovenantSensitiveArtifactPurger purger,
        SensitiveArtifactKind kind,
        IReadOnlyList<Guid> artifactIds,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(purger);

        ArgumentNullException.ThrowIfNull(artifactIds);

        if (artifactIds.Count == 0)
        {

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome([], CovenantArtifactErasureProgress.Empty));

        }

        return await purger
            .PurgeAsync(
                [.. artifactIds.Select(id => new CovenantSensitivePurgeTarget(kind, id))],
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// The typed refusal for an artifact the shared kernel could not remove.
    /// </summary>
    /// <remarks>
    /// <c>Covenant.ManualArtifactErasureRequired</c> rather than a generic conflict, because the
    /// operator's next step is genuinely different: nothing they can retry will clear an ownership
    /// mismatch on a file Arcanum no longer recognizes as its own.
    /// </remarks>
    internal static Error BlockedError(CovenantSensitivePurgeOutcome outcome)
    {

        ArgumentNullException.ThrowIfNull(outcome);

        CovenantErasureBlocker blocker = outcome.Results
            .FirstOrDefault(static result =>
                result.Disposition is CovenantSensitivePurgeDisposition.Blocked)
            ?.Blocker ?? CovenantErasureBlocker.IntegrityFailure;

        return new Error(
            blocker is CovenantErasureBlocker.AuthorityStale
                ? ErrorCodes.Covenant.StaleSnapshot
                : ErrorCodes.Covenant.ManualArtifactErasureRequired,
            "A protected artifact selected by this deletion could not be erased and was left unchanged.");

    }

    /// <summary>
    /// Whether this request carries a retention-purge authority at all.
    /// </summary>
    /// <remarks>
    /// Used only to mark a response protected when a purge actually happened. A route that erased
    /// nothing protected keeps the headers it always had.
    /// </remarks>
    internal static void MarkProtectedWhenPurged(HttpContext context, CovenantSensitivePurgeOutcome outcome)
    {

        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(outcome);

        if (!outcome.AllUnlabeled)
        {

            CovenantRequestFeatures.MarkProtectedResponse(context);

        }

    }

}
