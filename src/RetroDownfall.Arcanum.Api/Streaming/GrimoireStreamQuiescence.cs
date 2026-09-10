using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Streaming;

/// <summary>
/// One streaming request's maintenance revocation, and the rule about which token may carry it.
/// </summary>
/// <remarks>
/// <b>Revocation belongs to the producer token and never to the frame-write token.</b> That sentence
/// is the whole reason this type exists, and it is a correctness rule rather than a style preference.
/// A frame is written as a <c>data:</c> prefix, a serialized payload, a terminating blank line, and a
/// flush; cancelling the token those writes run on aborts the sequence part way and leaves bytes on
/// the wire that no client can parse and no later frame can repair. The bytes cannot be withdrawn, so
/// the only safe place to stop is between frames.
///
/// <para>Each of the five quiesceable routes hands one token to both its producer and its writer
/// today. Rather than leave five call sites to remember the distinction, they take one of these and
/// ask it for a producer link; the token they write frames on is the one they already had.</para>
///
/// <para>The token is published only for a lease that is actually the quiesceable kind. The gate
/// already declines to revoke any other kind, so the check is belt and braces — but it is the belt
/// that matters, because a billable stream cut halfway would bill for an answer nobody received.
/// Reading the kind here means a later change to the gate's selection cannot silently start cutting
/// the streams that must never be cut.</para>
///
/// <para>Resolved from the request rather than injected, so no composition root has to register it
/// and a host that maps these routes without the Arcanum infrastructure stack still works: it has no
/// Grimoire, so there is no lease to revoke, and <see cref="None"/> answers with a token that can
/// never fire. That is how every other pre-binding stage answers a service a bare host does not
/// compose.</para>
/// </remarks>
internal sealed class GrimoireStreamQuiescence
{

    /// <summary>The answer for a request that holds no revocable lease.</summary>
    private static readonly GrimoireStreamQuiescence NotQuiesceable = new(CancellationToken.None);

    internal GrimoireStreamQuiescence(CancellationToken revocation)
    {

        Revocation = revocation;

    }

    /// <summary>
    /// The maintenance revocation for this request, or a token that can never be cancelled.
    /// </summary>
    /// <remarks>
    /// Never passed to a frame write. Callers link it into the token their producer enumerates on and
    /// test <see cref="IsQuiescing"/> at their own frame boundaries.
    /// </remarks>
    internal CancellationToken Revocation { get; }

    /// <summary>Whether maintenance has asked this stream to stop starting new frames.</summary>
    internal bool IsQuiescing => Revocation.IsCancellationRequested;

    /// <summary>
    /// Resolves this request's quiescence from the admission lease it was admitted on.
    /// </summary>
    internal static GrimoireStreamQuiescence For(HttpContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        return context.RequestServices?.GetService<GrimoireRequestAdmissionScope>() is
            { Lease: { Kind: GrimoireRequestKind.QuiesceableStream } lease }
            ? new GrimoireStreamQuiescence(lease.MaintenanceRevocation)
            : NotQuiesceable;

    }

    /// <summary>
    /// Links a producer's own cancellation with maintenance revocation.
    /// </summary>
    /// <remarks>
    /// The caller disposes the returned source. When this request cannot be revoked the link carries
    /// the caller's token alone rather than pairing it with a permanently uncancellable one, because a
    /// linked source over a token that can never fire is a registration that can never run.
    /// </remarks>
    internal CancellationTokenSource LinkProducer(CancellationToken producerToken) =>
        Revocation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(producerToken, Revocation)
            : CancellationTokenSource.CreateLinkedTokenSource(producerToken);

}
