using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one caller-side adapter that starts, or replays, a Covenant operation the caller named itself.
/// </summary>
/// <remarks>
/// Deliberately thin. Every durable behaviour — the single-transaction identity row, the fixed-time
/// digest comparison, the replay-without-a-second-lease rule, and the
/// <c>Security.IdempotencyConflict</c> refusal — belongs to the shipped store and coordinator, and a
/// second implementation of any of them here would be a second opinion about whether two requests are
/// the same request (§10.16).
///
/// <para>It exists so that a Covenant caller cannot accidentally construct a half-present identity
/// triplet. The three fields are all required together: an operation named without its apply digest
/// replays anything, and an apply digest without a name replays nothing.</para>
/// </remarks>
internal sealed class CovenantRequestedOperationStarter(ILongRunningOperationCoordinator coordinator)
{

    private readonly ILongRunningOperationCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>
    /// Starts the named operation, or resolves the one that name already refers to.
    /// </summary>
    /// <remarks>
    /// Every optional correlation field on the create request is passed as null. A Covenant family
    /// reinitialize has no owning Session, run, inference, reservation, or claim, and inventing a link
    /// so the row "looks complete" would give recovery a relationship to reason about that does not
    /// exist.
    /// </remarks>
    public Task<Result<LongRunningOperationRequestIdentityResult>> StartRequestedAsync(
        string kind,
        LongRunningOperationRecoveryPolicy recoveryPolicy,
        string publicSummary,
        DateTimeOffset createdAt,
        Guid requestedOperationId,
        CovenantDigest applyRequestDigest,
        CovenantDigest effectDigest,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        ArgumentException.ThrowIfNullOrWhiteSpace(publicSummary);

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        LongRunningOperationCreateRequest request = new(
            kind,
            recoveryPolicy,
            publicSummary,
            createdAt);

        LongRunningOperationRequestIdentity identity = new(
            requestedOperationId,
            applyRequestDigest,
            effectDigest);

        return _coordinator.StartWithRequestIdentityAsync(
            request,
            identity,
            ownerId,
            leaseDuration,
            cancellationToken);

    }

}
