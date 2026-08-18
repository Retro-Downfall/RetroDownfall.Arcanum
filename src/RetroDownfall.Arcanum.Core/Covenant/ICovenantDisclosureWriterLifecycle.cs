using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The one way a destructive Covenant operation stops, and later restarts, the disclosure writer.
/// </summary>
/// <remarks>
/// The disclosure writer is the only component that can still append to the family after admission
/// has closed: it is warm by design, because a disclosure receipt written late is a disclosure nobody
/// can prove happened. That makes it the one thing an erasure has to shut down by name rather than
/// by draining leases, and the reason this is a contract instead of a private detail of whoever owns
/// the writer today (§10.20.4).
///
/// <para>The two members are deliberately not symmetric in what they permit. Quiescing is safe at any
/// point and is performed before the first artifact is touched, so a writer that was already stopped
/// simply stays stopped. Reopening may happen only after
/// <see cref="ICovenantAuthorityTransitionPublisher"/> has published the committed transition: a warm
/// writer reacquired against the old authority would append receipts under keys the erasure has
/// already invalidated.</para>
///
/// <para>A failure from either member is recoverable rather than terminal, and neither may report
/// success it cannot prove. A quiesce that could not confirm the writer stopped must fail, because
/// the caller's next act is to delete the rows the writer would otherwise be appending to.</para>
/// </remarks>
public interface ICovenantDisclosureWriterLifecycle
{

    /// <summary>
    /// Stops the disclosure writer and confirms it is no longer able to append.
    /// </summary>
    /// <remarks>
    /// Idempotent. An erasure that resumes from a durable checkpoint calls this again on a writer it
    /// already stopped, and a second call has to be a no-op rather than an error, or every resume
    /// would fail on the step that was already finished.
    /// </remarks>
    ValueTask<Result> QuiesceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Acquires a new warm writer lease against the freshly published authority.
    /// </summary>
    /// <remarks>
    /// Called only after publication succeeded and only while the exclusive gate is still held. A
    /// failure here happens before the caller's one reopening decision, so it selects
    /// <see cref="CovenantExclusiveLeaseDisposition.KeepClosed"/> rather than reversing an erasure
    /// that is already proven.
    /// </remarks>
    ValueTask<Result> ReopenAsync(CancellationToken cancellationToken);

}
