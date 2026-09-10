namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// What one Entry Weaving tick did, as far as its caller has to care.
/// </summary>
/// <remarks>
/// An enum rather than a <see langword="bool"/> because the two states are not opposites: a tick
/// that wove nothing because nothing was pending and a tick that wove nothing because maintenance
/// owns admission look identical from the outside, and only the second must be kept out of the
/// failure path. Neither member is zero, so a default-initialized outcome cannot read as "the tick
/// ran".
///
/// <para>It is deliberately local to Entry Weaving. Attachment indexing and Saga extraction reach
/// the same frontier but carry durable state this worker does not have — a pending queue identity
/// and a page watermark respectively — and a vocabulary shared before either of them has been
/// written would be a guess about their shape rather than a record of it.</para>
///
/// <para>It carries no failure member. A genuine product failure still throws, and is still logged
/// and backed off by the loop's catch-all; host cancellation is still an
/// <see cref="OperationCanceledException"/> with the stopping token signalled. Those two were
/// already distinct from one another, and this type adds the third case rather than restating
/// them.</para>
/// </remarks>
internal enum EntryWeavingTickOutcome : byte
{

    /// <summary>
    /// The tick held its work lease and ran to its end, whether or not it imprinted anything.
    /// </summary>
    Woven = 1,

    /// <summary>
    /// Maintenance owns Grimoire admission, so the tick performed no provider call and no write.
    /// </summary>
    /// <remarks>
    /// Refused its work lease, the tick also created no scope. Refused its effect group, a scope
    /// exists and the pending selection was read, but nothing billable began and nothing was
    /// written — which is the whole guarantee the frontier exists to give.
    /// </remarks>
    DeferredForMaintenance = 2,

}
