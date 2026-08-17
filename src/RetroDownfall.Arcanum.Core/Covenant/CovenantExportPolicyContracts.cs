using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The conditional read arm one plaintext export runs under.
/// </summary>
/// <remarks>
/// A nullable lease inside a typed value rather than a nullable <see cref="Result{T}"/> payload,
/// because "this installation has no Covenant arm" is a real answer and not a degenerate success. An
/// export that could not tell the two apart would either refuse an installation that has nothing to
/// refuse or proceed on one whose ledger it never read (§10.19.11).
///
/// <para>The lease is the caller's. This value carries it; it never disposes it.</para>
/// </remarks>
public sealed record CovenantExportAdmission(ICovenantSnapshotReadLease? ReadLease)
{

    /// <summary>The arm is absent: the export runs exactly as it did before the Covenant tier existed.</summary>
    public static CovenantExportAdmission Absent { get; } = new((ICovenantSnapshotReadLease?)null);

    /// <summary>Whether this export is a protected response that owes the private header tuple.</summary>
    public bool IsProtected => ReadLease is not null;

}

/// <summary>
/// The content-free answer to whether one Session may leave this installation as plaintext.
/// </summary>
/// <remarks>
/// Counts and one sensitivity code, and nothing else. This value reaches a refusal message and a
/// trace, so it names no artifact, key, Campaign, or generation.
///
/// <para>It is refused on either signal. The label rows are the live evidence; the Session projection
/// is deliberately conservative and may still count taint whose artifacts were purged. Purged taint
/// still bars a plaintext export for the same reason it still bars a cached replay — the Session held
/// Covenant content, and removing the artifact does not unmake that (§10.12).</para>
/// </remarks>
public sealed record CovenantSessionExportSensitivity(
    Guid SessionId,
    long TaintedArtifactCount,
    ContentSensitivity MaximumSensitivity)
{

    /// <summary>The answer for a Session that has produced no derived artifact at all.</summary>
    public static CovenantSessionExportSensitivity Clean(Guid sessionId) =>
        new(sessionId, TaintedArtifactCount: 0, ContentSensitivity.None);

    /// <summary>Whether a plaintext export of this Session is refused before any content byte.</summary>
    public bool IsRefused =>
        TaintedArtifactCount > 0 || MaximumSensitivity is not ContentSensitivity.None;

}

/// <summary>
/// What one Campaign export left behind, as content-free counts.
/// </summary>
/// <remarks>
/// Two counts rather than one total, because they answer separate questions. A canonical entry is
/// Covenant memory the operator authored and this bundle does not carry; a tainted artifact is some
/// other object of this Campaign's that is Covenant-derived. An operator deciding whether the bundle
/// is a complete transfer needs to know which of the two it is short of.
/// </remarks>
public sealed record CovenantCampaignExportExclusions(
    long CovenantEntryCount,
    long TaintedArtifactCount)
{

    /// <summary>Nothing was excluded, and the inventory actually ran to prove it.</summary>
    public static CovenantCampaignExportExclusions None { get; } =
        new(CovenantEntryCount: 0, TaintedArtifactCount: 0);

}

/// <summary>
/// The one reader both plaintext export routes ask before they emit a byte.
/// </summary>
/// <remarks>
/// One port for both routes on purpose. Session export and Campaign export ask different questions of
/// the same two tables, and two implementations that agreed on the day they were written would, at
/// their first divergence, let one surface disclose what the other refuses.
///
/// <para>Every method takes the lease rather than acquiring one. The lease has to be older than the
/// export graph and outlive the last response byte, so only the caller that owns the response can
/// decide when it is released; a port that acquired its own would release it on return, while the
/// archive was still being written (§10.16).</para>
/// </remarks>
public interface ICovenantExportPolicy
{

    /// <summary>
    /// Acquires the conditional read lease this export runs under, or reports that this installation
    /// has no Covenant arm at all.
    /// </summary>
    /// <param name="scope">
    /// The exact scope to lease, or <see langword="null"/> for an installation-wide read. A Session's
    /// labels can name any Campaign, so nothing narrower covers a Session inspection.
    /// </param>
    ValueTask<Result<CovenantExportAdmission>> AcquireConditionalReadAsync(
        CovenantOperationScope? scope,
        CancellationToken cancellationToken);

    /// <summary>Reads whether this Session carries any Covenant-derived artifact.</summary>
    Task<Result<CovenantSessionExportSensitivity>> InspectSessionAsync(
        Guid sessionId,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    /// <summary>Counts what a Campaign export leaves behind, without reading any of it.</summary>
    Task<Result<CovenantCampaignExportExclusions>> InventoryCampaignExclusionsAsync(
        Guid campaignId,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

}
