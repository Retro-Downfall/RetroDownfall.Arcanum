namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// How one attachment-indexing work unit ended, as far as its caller has to care.
/// </summary>
/// <remarks>
/// An enum rather than a <see langword="bool"/> because the two states are not opposites, and
/// separate from <c>SessionAttachmentIndexStatus</c> because that one is a public, JSON-serialized
/// wire vocabulary of five attachment states. A deferral is not a sixth attachment state — the
/// attachment is exactly as it was — so it must not reach that type.
///
/// <para>Neither member is zero, so a default-initialized disposition cannot read as "the request
/// ran". That matters more here than for a worker without durable state: <see cref="Concluded"/> is
/// what releases the pending queue identity and what lets the attempt counter advance, and a call
/// site that silently inherited it would spend both on a request that never began.</para>
///
/// <para>It is deliberately local to attachment indexing. Entry weaving reports a whole tick and
/// Saga extraction will report a page; this reports one dequeued request's disposition. Two samples
/// that already differ are not enough to extract a third shape from, and a vocabulary agreed before
/// the third worker is written would be a guess about it rather than a record of it.</para>
///
/// <para>It carries no failure member. A genuine product failure still throws, and is still logged
/// and backed off by the loop's catch-all; host cancellation is still an
/// <see cref="OperationCanceledException"/> with the stopping token signalled. Those two were
/// already distinct from one another, and this type adds the third case rather than restating
/// them.</para>
/// </remarks>
internal enum SessionAttachmentIndexDisposition : byte
{

    /// <summary>
    /// The work unit held its lease and ran to its end, whether it indexed, failed, or found nothing.
    /// </summary>
    Concluded = 1,

    /// <summary>
    /// Maintenance owns Grimoire admission, so the unit performed no provider call and classified nothing.
    /// </summary>
    /// <remarks>
    /// Refused its work lease, the unit also created no scope and wrote nothing at all. Refused an
    /// effect group, a scope exists and the pending mark and any earlier batch have been written,
    /// but no further provider call began and nothing was classified as a failure — which is the
    /// whole guarantee the frontier exists to give, and the reason the staged generation survives
    /// for the resumed run to continue from.
    /// </remarks>
    DeferredForMaintenance = 2,

}
