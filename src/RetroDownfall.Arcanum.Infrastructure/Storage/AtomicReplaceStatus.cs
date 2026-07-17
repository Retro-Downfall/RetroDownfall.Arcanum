namespace RetroDownfall.Arcanum.Infrastructure.Storage;

/// <summary>
/// Outcome of <see cref="AtomicFile.ReplaceAsync"/>. Distinguishes a clean abort before the
/// destination was touched from a post-move verification failure where the destination may already
/// contain the new content.
/// </summary>
internal enum AtomicReplaceStatus
{

    /// <summary>Destination was replaced and post-move hooks (if any) approved.</summary>
    Succeeded,

    /// <summary>
    /// Write aborted before <see cref="File.Move"/> (for example <c>beforeReplace</c> rejected).
    /// The destination was left untouched.
    /// </summary>
    Aborted,

    /// <summary>
    /// <see cref="File.Move"/> completed, <c>afterReplace</c> failed, and a best-effort
    /// restore/quarantine recovered a safe prior state (or removed the unverified destination).
    /// Callers may treat this as a normal failure.
    /// </summary>
    RolledBack,

    /// <summary>
    /// <see cref="File.Move"/> completed, <c>afterReplace</c> failed, and rollback/quarantine
    /// could not fully recover. The destination may contain the new content and must not be
    /// reported as a generic pre-move failure.
    /// </summary>
    ReplacedButUnverified,

}
