using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The operator's correction path: a write that names the exact version it replaces.
/// </summary>
/// <remarks>
/// A correction commits as an ordinary <c>OperatorSet</c>. The append-only version chain, the
/// predecessor link, the provenance, and the sensitivity of the version it replaces are all preserved
/// by the substrate rather than by anything here — what this file adds is the set of refusals that
/// happen <i>before</i> the append, so that an operator who names a version they did not see is told
/// so instead of overwriting one they did not read.
/// </remarks>
internal sealed partial class CovenantMutationService
{

    public async ValueTask<Result<CovenantMutationPreflightDto>> PrepareCorrectAsync(
        CovenantCorrectPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result<CovenantCompiledContent> compiled = TryCompile(request.Key, request.Content);

        if (compiled.IsFailure)
        {

            return compiled.Error;

        }

        CovenantOperationScope scope = Scope(request.Scope, request.CampaignId);

        Result<CovenantDigest> target = await ResolveTargetAsync(
                scope,
                compiled.Value.NormalizedKey,
                request.TargetVersionId,
                request.ExpectedRevision,
                request.TargetRenderedHash,
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (target.IsFailure)
        {

            return target.Error;

        }

        return await PrepareAsync(
                scope,
                compiled.Value.NormalizedKey,
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                request.MutationId,
                request.ExpectedRevision,

                // A correction replaces live content. Reinstating a retired key is a different sentence
                // and has its own flag, and the target resolution above has already refused a tombstone.
                reactivate: false,
                compiled.Value,
                readLease,
                cancellationToken,
                request.TargetVersionId,
                target.Value)
            .ConfigureAwait(false);

    }

    public async ValueTask<Result<CovenantMutationResultDto>> CorrectAsync(
        CovenantCorrectRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result<CovenantCompiledContent> compiled = TryCompile(request.Key, request.Content);

        if (compiled.IsFailure)
        {

            return compiled.Error;

        }

        Result<CovenantDigest> renderedHash = ParseDigest(request.TargetRenderedHash);

        if (renderedHash.IsFailure)
        {

            return renderedHash.Error;

        }

        return await ApplyAsync(
                Scope(request.Scope, request.CampaignId),
                compiled.Value.NormalizedKey,
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                request.MutationId,
                request.ExpectedRevision,
                reactivate: false,
                compiled.Value,
                request.PreflightToken,
                writeLease,
                cancellationToken,
                request.TargetVersionId,
                renderedHash.Value)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Proves the named target is the live Confirmed head, or says which way it is not.
    /// </summary>
    /// <remarks>
    /// Every refusal here happens before a token is issued, so an operator never approves a screen
    /// describing a correction that cannot be committed. The order is deliberate: absence first, then
    /// lifecycle, then identity, then content — each answer is more specific than the one above it, and
    /// reporting the specific one for a target that does not exist would be describing a version this
    /// installation has never held.
    /// </remarks>
    private async ValueTask<Result<CovenantDigest>> ResolveTargetAsync(
        CovenantOperationScope scope,
        string normalizedKey,
        Guid targetVersionId,
        long expectedRevision,
        string targetRenderedHash,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        Result<CovenantDetail> detail = await store
            .ReadDetailAsync(new CovenantDetailQuery(scope, normalizedKey), readLease, cancellationToken)
            .ConfigureAwait(false);

        if (detail.IsFailure)
        {

            return detail.Error;

        }

        if (detail.Value.ConfirmedHead is not { } head)
        {

            return Result<CovenantDigest>.Failure(new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This Covenant key has no Confirmed head to correct."));

        }

        if (head.Lifecycle != CovenantLifecycle.Set)
        {

            return Result<CovenantDigest>.Failure(new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This Covenant key is retired. Reinstate it with a write that reactivates it rather than a correction."));

        }

        // The named version and the head's revision are two statements of the same fact, and both are
        // compared. A version id alone does not say which lane it came from, and a revision alone can
        // be guessed from a history an operator half-read.
        if (head.VersionId != targetVersionId || head.LaneRevision != expectedRevision)
        {

            return Result<CovenantDigest>.Failure(new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This Covenant entry moved after the version being corrected was read."));

        }

        Result<CovenantDigest> requested = ParseDigest(targetRenderedHash);

        if (requested.IsFailure)
        {

            return requested;

        }

        // The hash is what a revision number cannot be: proof the operator saw this content rather than
        // a number that happened to be right.
        if (head.RenderedHash is not { } live || live != requested.Value)
        {

            return Result<CovenantDigest>.Failure(new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The content of this Covenant version is not what the correction says it is."));

        }

        return Result<CovenantDigest>.Success(live);

    }

    private static Result<CovenantDigest> ParseDigest(string value)
    {

        try
        {

            return Result<CovenantDigest>.Success(new CovenantDigest(Convert.FromHexString(value)));

        }
        catch (Exception failure) when (failure is FormatException or ArgumentException)
        {

            return Result<CovenantDigest>.Failure(new Error(
                ErrorCodes.Covenant.InvalidScope,
                "The target rendered hash must be a 64-character hexadecimal digest."));

        }

    }

}
