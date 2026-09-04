using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Core.Operations;

/// <summary>
/// The operations this process is actively running, and which generic reconciliation must leave alone.
/// </summary>
/// <remarks>
/// An offline transition stops renewing its durable lease for the length of its closed period, because
/// a renewal advances the row's revision and the authenticated journal has bound itself to the exact
/// revision the launch produced. That leaves the row looking abandoned to anything that decides by the
/// lease alone — and generic reconciliation runs on a background interval, so it would find the row,
/// claim it, and start a second recovery beside the transition that is still running.
///
/// <para>The claim is deliberately process-local and holds no lease, no timer and no durable state. It
/// answers one question — is this process itself already running that operation — which is the only
/// question a lapsed lease can no longer answer. Everything cross-process is settled elsewhere and
/// more strongly: the installation maintenance lock admits one process, and the transition journal
/// admits one active slot per profile.</para>
///
/// <para>A claim that outlived its run would be worse than none, because nothing would ever recover
/// the operation it names. So it is released on every exit, including failure and cancellation, and
/// the release is keyed by the same token that took it — a caller cannot release a claim it does not
/// hold, and a stale release therefore cannot silently unprotect a live run.</para>
/// </remarks>
public sealed class LongRunningOperationOwnership
{

    private readonly ConcurrentDictionary<Guid, Guid> _claims = new();

    /// <summary>
    /// Claims one operation for this process, or reports that it is already claimed.
    /// </summary>
    /// <remarks>
    /// Returns the token a later release has to present. A second claim on the same operation fails
    /// rather than nesting: two callers running the same destructive operation is the condition this
    /// exists to prevent, and a reference count would let the second one believe it had won.
    /// </remarks>
    public bool TryClaim(Guid operationId, out Guid token)
    {

        token = Guid.NewGuid();

        return operationId != Guid.Empty && _claims.TryAdd(operationId, token);

    }

    /// <summary>Releases a claim, but only for the exact token that took it.</summary>
    public bool Release(Guid operationId, Guid token) =>
        _claims.TryGetValue(operationId, out Guid held)
        && held == token
        && _claims.TryRemove(new KeyValuePair<Guid, Guid>(operationId, token));

    /// <summary>Whether this process is already running the named operation.</summary>
    public bool IsClaimed(Guid operationId) => _claims.ContainsKey(operationId);

}
