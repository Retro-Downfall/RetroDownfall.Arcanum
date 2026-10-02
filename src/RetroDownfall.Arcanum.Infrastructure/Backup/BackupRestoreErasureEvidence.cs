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
/// not be proven and the three ways out: restore readability and retry, reset the erasure key on this
/// installation, or reset the installation.</para>
/// </remarks>
internal sealed record BackupRestoreErasureEvidence(
    BackupRestoreErasureEvidenceKind Kind,
    byte[] KeyId,
    MemoryErasureEvidenceSnapshot Rows,
    BackupVerifyIssue? Refusal)
{
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
    internal static BackupRestoreErasureEvidence Refused(string code, MemoryErasureEvidenceSnapshot? rows) =>
        new(
            BackupRestoreErasureEvidenceKind.Refused,
            [],
            rows ?? MemoryErasureEvidenceSnapshot.Empty,
            new BackupVerifyIssue(code, MessageFor(code)));

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

    private static string MessageFor(string code) =>
        code switch
        {
            BackupRestoreErasureCodes.KeyMissing => Compose(
                "This installation's Grimoire records erasures that the erasure key now in its OS credential "
                + "store did not record: the key that did is missing or was replaced.",
                "Put the original erasure key back"),
            BackupRestoreErasureCodes.KeyUnavailable => Compose(
                "This installation's Grimoire records erasures, and its OS credential store could not provide "
                + "the erasure key that proves them.",
                "Make the OS credential store readable"),
            BackupRestoreErasureCodes.EvidenceUnavailable => Compose(
                "This installation's Grimoire could not be read, and its OS credential store does not show that "
                + "it never erased anything.",
                "Make the Grimoire's database, key-derivation sidecar and secret readable again"),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a destination erasure evidence refusal."),
        };

    /// <summary>What could not be proven, then the three ways out every erasure refusal names.</summary>
    private static string Compose(string cause, string readability) =>
        cause
        + " The restore cannot prove which archived items were erased here, so it stopped and the current "
        + "installation is unchanged. "
        + readability
        + " and retry, run 'arcanum memory erasure reset-key' on this installation, which discards the "
        + "erasures it cannot prove, or perform a full installation reset.";
}
