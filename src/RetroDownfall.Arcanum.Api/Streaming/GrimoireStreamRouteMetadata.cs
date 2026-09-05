namespace RetroDownfall.Arcanum.Api.Streaming;

/// <summary>
/// What a maintenance window is permitted to do to one streaming response.
/// </summary>
/// <remarks>
/// A class rather than a boolean, because "not quiesceable" has two entirely different reasons and an
/// operator reading the inventory has to be able to tell them apart. A billable stream has already
/// spent money by the time a transition begins and is drained so the answer reaches the caller who
/// paid for it; a finite one is drained because it ends on its own in bounded time and there is
/// nothing to stop. Collapsing both into "false" would record the decision without recording which
/// decision it was, and the next reader would have to re-derive it from the handler.
///
/// <para>Zero is deliberately absent. A default-initialized member would read as a real
/// classification on a marker somebody forgot to attach, and a route nobody classified must never be
/// indistinguishable from one that was classified and drains — the first is a hole in the inventory
/// and the second is a finished decision.</para>
/// </remarks>
internal enum GrimoireStreamClass : byte
{

    /// <summary>
    /// Unbounded, declared quiesceable: revocation ends it after the frame already being written.
    /// </summary>
    GrimoireQuiesceableStream = 1,

    /// <summary>Ends on its own in bounded time; stage one drains it through completion.</summary>
    FiniteDrain = 2,

    /// <summary>
    /// Has already spent money on the caller's behalf; drained so the answer reaches whoever paid.
    /// </summary>
    BillableDrain = 3,

}

/// <summary>
/// Whether a streaming response reaches the live Grimoire at all.
/// </summary>
/// <remarks>
/// Recorded beside the class rather than folded into it, because the two answer different questions
/// and the three event routes are the proof: they read no database whatsoever and are still in the
/// complete positive quiesceable set. What makes a route quiesceable is that it is unbounded and
/// declared, not that it holds a connection — so a single enum would have to call
/// <c>/api/events/logs</c> either quiesceable or authority-free, and it is both.
/// </remarks>
internal enum GrimoireStreamAuthority : byte
{

    /// <summary>Reads or writes the live Grimoire at some point in the response.</summary>
    LiveGrimoire = 1,

    /// <summary>Never touches the database; its frames come from process-local state alone.</summary>
    NoGrimoireAuthority = 2,

}

/// <summary>
/// Marks one route as a streaming response and names which kind of one it is.
/// </summary>
/// <remarks>
/// Endpoint metadata rather than a path list, so the fact travels with the route it describes and a
/// moved or renamed route carries its classification with it. It is a marker type rather than a bare
/// enum in the metadata collection because metadata is retrieved by type: a bare
/// <see cref="GrimoireStreamClass"/> would be found by anything else that ever put an enum of that
/// shape on a route, and every sibling marker in this codebase — the admission exemption, the
/// recovery-route markers — is a type for the same reason.
///
/// <para>Attaching it is what makes the admission stage take a
/// <c>GrimoireRequestKind.QuiesceableStream</c> lease instead of a finite one. An unmarked route
/// still takes a finite lease, which is the safe default: a finite lease is drained through
/// completion, so forgetting the marker makes a transition slow rather than making a stream stop
/// mid-frame. The inventory is what stops it staying forgotten.</para>
/// </remarks>
internal sealed class GrimoireStreamRouteMetadata
{

    internal GrimoireStreamRouteMetadata(GrimoireStreamClass streamClass)
    {

        if (!Enum.IsDefined(streamClass))
        {

            throw new ArgumentOutOfRangeException(nameof(streamClass));

        }

        Class = streamClass;

    }

    /// <summary>The one unbounded class, carried by exactly the five declared SSE routes.</summary>
    internal static GrimoireStreamRouteMetadata Quiesceable { get; } =
        new(GrimoireStreamClass.GrimoireQuiesceableStream);

    /// <summary>Carried by a stream that ends on its own.</summary>
    internal static GrimoireStreamRouteMetadata FiniteDrain { get; } =
        new(GrimoireStreamClass.FiniteDrain);

    /// <summary>Carried by a stream a provider is already charging for.</summary>
    internal static GrimoireStreamRouteMetadata BillableDrain { get; } =
        new(GrimoireStreamClass.BillableDrain);

    /// <summary>The one class this route was classified as.</summary>
    internal GrimoireStreamClass Class { get; }

}
