using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The durable, append-only record of protected effects that physically left this installation.
/// </summary>
/// <remarks>
/// Acknowledgement commits <em>before</em> the bytes leave Arcanum, never after. A journal written
/// afterwards cannot record the one case it exists for: a dispatch that left the process and then
/// crashed, restarted, or lost its connection. The operator would be told nothing was disclosed
/// while the provider already had the payload (§10.13).
///
/// <para>The journal is accounting, not content. A receipt says that something was disclosed to a
/// destination class; it never says what. Storing the destination itself, a path, or search text
/// would recreate the exposure the journal exists to account for — and receipts are deliberately
/// never cleared by local erasure.</para>
/// </remarks>
public interface ICovenantDisclosureJournal
{

    /// <summary>
    /// Commits one disclosure acknowledgement, returning the existing receipt for an exact replay.
    /// </summary>
    /// <remarks>
    /// Idempotent on the effect identity so a retry that can prove it never dispatched reuses the
    /// acknowledged identity instead of manufacturing a second physical disclosure — while a genuine
    /// second attempt, which carries a different physical attempt ordinal in its effect identity,
    /// is counted separately. Counting is deliberately allowed to overstate and never to understate.
    /// </remarks>
    /// <param name="sensitivity">
    /// The frozen level and provenance the draft's sensitivity digest was computed over.
    /// </param>
    /// <remarks>
    /// The provenance travels beside the draft rather than being reconstructed from its digest,
    /// because a digest is one-way: a journal that had only the digest could record no generation
    /// identities at all, or invent some, and an invented provenance is worse than none. The
    /// implementation recomputes the digest from this value and refuses a mismatch.
    /// </remarks>
    ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category,
        ProviderCallSensitivity sensitivity,
        CancellationToken cancellationToken);

}

/// <summary>
/// Which class of physical effect one receipt accounts for, matching
/// <c>external_disclosure_receipts.EffectCategoryCode</c>.
/// </summary>
public enum CovenantDisclosureEffectCategory : byte
{
    ProviderDispatch = 1,
    McpToolUse = 2,
    WardEgress = 3,
    EncryptedBackup = 4,
    MaintenanceAttempt = 5,
}
