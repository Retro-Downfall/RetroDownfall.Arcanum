using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

/// <summary>
/// The one value an outer workflow and its nested transition can both derive, and neither can borrow.
/// </summary>
/// <remarks>
/// A nested transition commits to this digest in its journal before it does anything, from the launch
/// it is about to run and the claim the outer record already carries. After its terminal
/// compare-exchange it recomputes the same value from the completion receipt it reads back out of that
/// outer record. Equality is the proof that the two records describe one piece of work.
///
/// <para>The completion phase is in the preimage and the terminal winner digest is not. The phase is
/// there so a claim cannot satisfy a binding — a receipt that has not reported produces a different
/// value, which is the whole difference between "started" and "finished". The winner is absent because
/// it cannot be known before the effect, and a binding that could only be computed afterwards could
/// not be committed beforehand.</para>
/// </remarks>
internal static class GrimoireOfflineTransitionParentReceipt
{

    private const string BindingDomain =
        "arcanum.grimoire.offline-transition.parent-receipt-binding.v1";

    /// <summary>The phase byte a satisfied binding is computed against, and the only one.</summary>
    private const byte CompletedPhase = 2;

    internal static Result<CovenantDigest> BindingDigest(
        Guid outerOperationId,
        Guid nestedOperationId,
        CovenantDigest nestedEffectDigest)
    {

        if (outerOperationId == Guid.Empty
            || nestedOperationId == Guid.Empty
            || outerOperationId == nestedOperationId
            || !nestedEffectDigest.IsValid)
        {

            return Result<CovenantDigest>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The nested transition receipt binding requires two distinct operations and one effect."));

        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(BindingDomain));

        hash.AppendData([0]);

        AppendGuid(hash, outerOperationId);

        AppendGuid(hash, nestedOperationId);

        hash.AppendData(nestedEffectDigest.Bytes);

        hash.AppendData([CompletedPhase]);

        return Result<CovenantDigest>.Success(new CovenantDigest(hash.GetHashAndReset()));

    }

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {

        Span<byte> buffer = stackalloc byte[16];

        _ = value.TryWriteBytes(buffer, bigEndian: true, out _);

        hash.AppendData(buffer);

    }

}

/// <summary>
/// The one write a nested transition may make into the workflow that launched it.
/// </summary>
/// <remarks>
/// Not a general handle on the outer record: it can publish exactly one completion receipt, for
/// exactly the nested operation its binding names, and it answers with a digest recomputed from what
/// the record actually holds afterwards rather than with the value it was asked to write.
/// </remarks>
internal interface IGrimoireOfflineTransitionParentReceiptSink
{

    /// <summary>The value this transition's journal is, or is about to be, bound to.</summary>
    CovenantDigest BindingDigest { get; }

    /// <summary>
    /// Publishes the completion receipt if it is not already exact, then rereads and recomputes.
    /// </summary>
    /// <remarks>
    /// An already-exact receipt is reread and nothing is published. Publishing into the outer record
    /// advances its authenticated envelope revision, and a replay that republished an identical
    /// receipt would invalidate every authority bound to the previous revision for no new fact.
    /// </remarks>
    Task<Result<CovenantDigest>> PublishAndRereadAsync(
        CovenantDigest terminalWinnerDigest,
        CancellationToken cancellationToken);

}

/// <summary>
/// Decides whether a transition is the nested arm of a broader workflow, by reading rather than being told.
/// </summary>
/// <remarks>
/// A sink handed down from the caller could not survive a crash: recovery in a fresh process has no
/// caller, and the journal it resumes already carries a binding it must satisfy. Reading the outer
/// record instead means first entry and recovery reach the same answer from the same evidence, and it
/// makes "a parent-bound journal whose outer record is gone" a state that can be named rather than one
/// that only an absent parameter would imply.
/// </remarks>
internal interface IGrimoireOfflineTransitionParentReceiptResolver
{

    /// <summary>
    /// Answers with the bound sink, the absence of a parent, or a refusal.
    /// </summary>
    /// <param name="heldInstallationLock">The lock the transition already borrowed.</param>
    /// <param name="kind">The transition kind. Only a healthy-catalog factory erasure may be nested.</param>
    /// <param name="nestedEffectDigest">This launch's canonical effect digest.</param>
    /// <param name="committedBindingDigest">
    /// The binding an already-published journal committed to, or <see langword="null"/> on first entry.
    /// A non-null value that the outer record cannot reproduce is a refusal, not an absent parent.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<Result<IGrimoireOfflineTransitionParentReceiptSink?>> ResolveAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionKind kind,
        CovenantDigest nestedEffectDigest,
        CovenantDigest? committedBindingDigest,
        CancellationToken cancellationToken);

}

/// <summary>The resolver a build with no broader workflow composed answers with.</summary>
/// <remarks>
/// It answers "no parent" for a first entry and refuses a resume that names one, which is the honest
/// pair of answers for a host that cannot read an outer record at all. It exists so the phase
/// authority's dependency is required rather than optional: an omitted optional resolver would make
/// every transition standalone by default, and defaulting to standalone is precisely the downgrade the
/// evidence matrix fails closed on.
/// </remarks>
internal sealed class GrimoireOfflineTransitionUnparentedReceiptResolver
    : IGrimoireOfflineTransitionParentReceiptResolver
{

    public Task<Result<IGrimoireOfflineTransitionParentReceiptSink?>> ResolveAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionKind kind,
        CovenantDigest nestedEffectDigest,
        CovenantDigest? committedBindingDigest,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            committedBindingDigest is null
                ? Result<IGrimoireOfflineTransitionParentReceiptSink?>.Success(null)
                : Result<IGrimoireOfflineTransitionParentReceiptSink?>.Failure(
                    new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "A parent-bound offline transition requires its broader workflow record.")));

}
