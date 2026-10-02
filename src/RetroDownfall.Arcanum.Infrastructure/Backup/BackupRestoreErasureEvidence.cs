using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>The refusal codes a restore reports when it cannot keep erased items from returning.</summary>
internal static class BackupRestoreErasureCodes
{
    /// <summary>The destination's Grimoire holds evidence that the key now in the keychain did not record.</summary>
    internal const string KeyMissing = "backup.restore_erasure_key_missing";

    /// <summary>The destination's Grimoire holds evidence and the keychain could not provide its key.</summary>
    internal const string KeyUnavailable = "backup.restore_erasure_key_unavailable";

    /// <summary>The destination's Grimoire could not be read while its keychain shows it may have erased.</summary>
    internal const string EvidenceUnavailable = "backup.restore_erasure_evidence_unavailable";

    /// <summary>The staged generation could not be brought to a schema that can take the evidence.</summary>
    internal const string EvidenceUnjoinable = "backup.restore_erasure_evidence_unjoinable";

    /// <summary>The staged generation did not prove the evidence applied before commit.</summary>
    internal const string VerificationFailed = "backup.restore_erasure_verification_failed";
}

/// <summary>What one read of the destination's erasure evidence found.</summary>
internal enum BackupRestoreErasureEvidenceKind
{
    /// <summary>No evidence, or provably none: there is nothing a restore has to keep from returning.</summary>
    None = 1,

    /// <summary>Evidence and the key that recorded every row of it.</summary>
    Present = 2,

    /// <summary>The destination could not prove what it erased, so the restore must not proceed.</summary>
    Refused = 3,
}

/// <summary>
/// The destination's erasure evidence as a restore carries it: the rows as values, the key identifier
/// that recorded them, and the refusal when there is one.
/// </summary>
/// <remarks>
/// <para>Destination-authoritative. A restore applies this installation's evidence to whatever the
/// archive brings, never the archive's to this installation, so an item erased here stays erased
/// whichever backup is restored over it.</para>
///
/// <para>Content-free throughout. The rows are keyed digests and counts, the key identifier is a
/// truncated keyed hash of the key and never the key, and every refusal message names only what could
/// not be proven and the ways out that can work in that state.</para>
/// </remarks>
internal sealed record BackupRestoreErasureEvidence(
    BackupRestoreErasureEvidenceKind Kind,
    byte[] KeyId,
    MemoryErasureEvidenceSnapshot Rows,
    BackupVerifyIssue? Refusal)
{
    /// <summary>The middle of every refusal: what the operator cannot rely on, and that nothing changed.</summary>
    private const string Unchanged =
        " The restore cannot prove which archived items were erased here, so it stopped and the current "
        + "installation is unchanged. ";

    /// <summary>A destination with nothing to keep from returning.</summary>
    internal static BackupRestoreErasureEvidence None { get; } =
        new(BackupRestoreErasureEvidenceKind.None, [], MemoryErasureEvidenceSnapshot.Empty, null);

    /// <summary>A destination whose every evidence row <paramref name="keyId"/> recorded.</summary>
    internal static BackupRestoreErasureEvidence Present(byte[] keyId, MemoryErasureEvidenceSnapshot rows)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        ArgumentNullException.ThrowIfNull(rows);

        return new(BackupRestoreErasureEvidenceKind.Present, [.. keyId], rows, null);
    }

    /// <summary>A destination that could not prove what it erased.</summary>
    /// <param name="code">One of the three destination-read codes in <see cref="BackupRestoreErasureCodes"/>.</param>
    /// <param name="rows">What the Grimoire yielded before the key refused it, or null when it could not be read.</param>
    /// <param name="keyState">
    /// What the keychain answered, when it shapes the way out: an item that is not a key has to be removed
    /// before the erasure key can be reset.
    /// </param>
    internal static BackupRestoreErasureEvidence Refused(
        string code,
        MemoryErasureEvidenceSnapshot? rows,
        MemoryErasureKeyState? keyState = null) =>
        new(
            BackupRestoreErasureEvidenceKind.Refused,
            [],
            rows ?? MemoryErasureEvidenceSnapshot.Empty,
            new BackupVerifyIssue(code, MessageFor(code, keyState)));

    /// <summary>The status and per-store counts a plan reports.</summary>
    internal BackupRestoreErasureEvidenceSummary ToSummary() =>
        new(
            Kind switch
            {
                BackupRestoreErasureEvidenceKind.None => BackupRestoreErasureEvidenceStatus.None,
                BackupRestoreErasureEvidenceKind.Present => BackupRestoreErasureEvidenceStatus.Present,
                BackupRestoreErasureEvidenceKind.Refused => BackupRestoreErasureEvidenceStatus.Refused,
                _ => throw new InvalidOperationException("The erasure evidence kind is not recognized."),
            },
            Fingerprints(MemoryReviewStore.Saga),
            Fingerprints(MemoryReviewStore.Lexicon),
            Fingerprints(MemoryReviewStore.Covenant),
            Rows.Receipts.Count);

    private long Fingerprints(MemoryReviewStore store) => Rows.Fingerprints.Count(row => row.Store == store);

    /// <summary>The content-free refusal message for one code, shaped by what the keychain answered.</summary>
    /// <remarks>
    /// Every message says what could not be proven and that nothing changed, then the ways out that can
    /// work in that state. A key that is missing, replaced or unreadable leaves three: make the key
    /// readable and retry, reset the erasure key, or reset the installation. An item that is not a key
    /// has to be removed before a reset can run, because the reset never overwrites one, and the message
    /// keeps the order that cannot cost a valid key: unlock and retry first, remove only an item confirmed
    /// invalid. An unreadable Grimoire cannot be reset through the host, which cannot open it, so that
    /// message offers only readability and a full reset.
    /// </remarks>
    private static string MessageFor(string code, MemoryErasureKeyState? keyState) =>
        (code, keyState) switch
        {
            (BackupRestoreErasureCodes.KeyMissing, _) =>
                "This installation's Grimoire records erasures that the erasure key now in its OS credential "
                + "store did not record: the key that did is missing or was replaced." + Unchanged
                + "Put the original erasure key back and retry, run 'arcanum memory erasure reset-key' on this "
                + "installation, which discards the erasures it cannot prove, or perform a full installation reset.",
            (BackupRestoreErasureCodes.KeyUnavailable, MemoryErasureKeyState.Malformed) =>
                "This installation's Grimoire records erasures, and the erasure key item in its OS credential "
                + "store is not a valid key." + Unchanged
                + "If the credential store is locked or did not answer, unlock it and retry. Only if the item is "
                + "confirmed malformed, remove it with the OS credential tool and run 'arcanum memory erasure "
                + "reset-key' on this installation: removing a key makes every erasure fingerprint unverifiable, "
                + "and the reset discards them, so erased content could be learned again. Otherwise perform a "
                + "full installation reset.",
            (BackupRestoreErasureCodes.KeyUnavailable, _) =>
                "This installation's Grimoire records erasures, and its OS credential store could not provide "
                + "the erasure key that proves them." + Unchanged
                + "Make the OS credential store readable and retry; once it answers, 'arcanum memory erasure "
                + "reset-key' on this installation discards the erasures its key cannot prove; or perform a full "
                + "installation reset.",
            (BackupRestoreErasureCodes.EvidenceUnavailable, _) =>
                "This installation's Grimoire is missing or could not be read, and its OS credential store does "
                + "not show that it never erased anything." + Unchanged
                + "Make the Grimoire's database, key-derivation sidecar and secret readable again and retry, or "
                + "perform a full installation reset.",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a destination erasure evidence refusal."),
        };
}
