using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What one kind of offline transition does that the ladder itself does not.
/// </summary>
/// <remarks>
/// The phase ladder is the same for every transition, and that is the point of it: closing, applying,
/// compacting, verifying and reopening are the Grimoire's business, not any particular erasure's. What
/// is not the same is which durable operation a kind is allowed to be, and whether it owes the
/// installation ordinary work that the phases do not describe. Those two facts live here, one
/// implementation per registered pair, so that adding a third kind is adding a registration rather
/// than adding a branch to a coordinator that already has enough of them.
/// </remarks>
internal interface IGrimoireOfflineTransitionEffectHandler
{
    /// <summary>The journal kind this handler answers for.</summary>
    GrimoireOfflineTransitionKind Kind { get; }

    /// <summary>The payload version it answers for, which is never zero.</summary>
    byte PayloadVersion { get; }

    /// <summary>The one durable operation a transition of this kind may be.</summary>
    /// <remarks>
    /// The whole reason the registry is keyed the way the payload table is: a journal names a kind,
    /// this says which operation that kind is, and a checkpoint claiming a different one is refused
    /// before anything is closed. Without it the restriction would have to be re-derived at each entry
    /// point, and an entry point that forgot would adopt a scope it has no right to.
    /// </remarks>
    CovenantExclusiveOperation Operation { get; }

    /// <summary>Whether this kind owes ordinary database work the phase ladder does not describe.</summary>
    bool RequiresOrdinaryContinuation { get; }

    /// <summary>
    /// Whether this transition still owes that ordinary work: refused when the kind requires it and the
    /// run carries none, otherwise owed exactly until the journal records it.
    /// </summary>
    /// <remarks>
    /// Idempotent by consulting the journal rather than by being safe to repeat. Whether the
    /// continuation has run is a one-way sub-state the journal owns, because the phase window cannot
    /// tell a run that completed it from one that crashed before starting it — both sit at the same
    /// phase, which is exactly the ambiguity a resumed transition has to resolve.
    ///
    /// <para>The coordinator runs owed work itself, once, inside the closed period's admitted ledger
    /// window, and then records it in the journal. The window is the coordinator's to open and close:
    /// it opens the promoted connection, runs the work, closes it, clears the pool and reports the
    /// close, and handing the continuation straight to that window keeps it bound to exactly the
    /// window that runs it.</para>
    /// </remarks>
    /// <param name="continuationSupplied">Whether the run carries an ordinary continuation.</param>
    /// <param name="continuationRecorded">Whether the journal already records the continuation as done.</param>
    Result<bool> OwesOrdinaryContinuation(
        bool continuationSupplied,
        bool continuationRecorded);
}

/// <summary>
/// The one handler both registered kinds are instances of.
/// </summary>
/// <remarks>
/// One class registered twice rather than two classes, because the two kinds differ in what they are
/// configured with and not in what they do: a Covenant reset is a transition with no ordinary
/// continuation, and a healthy-catalog factory erasure is the same transition with one. Two classes
/// would put that single difference behind two copies of the same body, and the copies would drift.
/// </remarks>
internal sealed class CovenantOfflineTransitionEffectHandler(
    GrimoireOfflineTransitionKind kind,
    CovenantExclusiveOperation operation,
    bool requiresOrdinaryContinuation) : IGrimoireOfflineTransitionEffectHandler
{
    public GrimoireOfflineTransitionKind Kind { get; } = kind;

    public byte PayloadVersion => 1;

    public CovenantExclusiveOperation Operation { get; } = operation;

    public bool RequiresOrdinaryContinuation { get; } = requiresOrdinaryContinuation;

    public Result<bool> OwesOrdinaryContinuation(
        bool continuationSupplied,
        bool continuationRecorded)
    {
        if (!RequiresOrdinaryContinuation)
        {
            return false;
        }

        if (!continuationSupplied)
        {
            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A healthy-catalog factory erasure requires its ordinary cleanup continuation.");
        }

        return !continuationRecorded;
    }
}

/// <summary>
/// The closed table of effect handlers, keyed exactly as the payload table is.
/// </summary>
/// <remarks>
/// Two tables over one key space, deliberately. The payload table says how a journal of a given kind
/// and version is read and written; this one says what a transition of that kind and version is
/// allowed to be and owes. Keeping them apart keeps a decoding concern out of an erasure concern;
/// keeping the key identical is what lets a suite assert that neither table has a kind the other has
/// never heard of, which is the failure a second table would otherwise introduce.
///
/// <para>Composed rather than static. The payload table can be a process-wide constant because
/// decoding depends on nothing but the bytes; an effect handler is reached through the same scope the
/// journal session and the operation store are reached through, and a process-wide instance would
/// invite one to capture a scope it outlives.</para>
/// </remarks>
internal sealed class GrimoireOfflineTransitionEffectHandlerRegistry
{
    private readonly IReadOnlyDictionary<
        (GrimoireOfflineTransitionKind Kind, byte Version),
        IGrimoireOfflineTransitionEffectHandler> _handlers;

    private GrimoireOfflineTransitionEffectHandlerRegistry(
        IReadOnlyDictionary<
            (GrimoireOfflineTransitionKind Kind, byte Version),
            IGrimoireOfflineTransitionEffectHandler> handlers)
    {
        _handlers = handlers;
    }

    /// <summary>Every pair this table answers for, which is what a closure assertion compares.</summary>
    internal IReadOnlyCollection<(GrimoireOfflineTransitionKind Kind, byte Version)> Keys =>
        [.. _handlers.Keys];

    /// <summary>
    /// Builds the table, refusing anything that would make a lookup ambiguous or unanswerable.
    /// </summary>
    /// <remarks>
    /// The same four refusals the payload table makes, and for the same reasons: an undeclared kind or
    /// a zero version is a key nothing can legitimately ask for, a duplicate is two answers to one
    /// question, and an empty table is a registry that would refuse every transition while looking
    /// like it had been composed.
    /// </remarks>
    internal static Result<GrimoireOfflineTransitionEffectHandlerRegistry> Create(
        IEnumerable<IGrimoireOfflineTransitionEffectHandler> handlers)
    {
        if (handlers is null)
        {
            return Unregistered<GrimoireOfflineTransitionEffectHandlerRegistry>();
        }

        Dictionary<
            (GrimoireOfflineTransitionKind Kind, byte Version),
            IGrimoireOfflineTransitionEffectHandler> registrations = [];

        foreach (IGrimoireOfflineTransitionEffectHandler? handler in handlers)
        {
            if (handler is null
                || !Enum.IsDefined(handler.Kind)
                || !Enum.IsDefined(handler.Operation)
                || handler.PayloadVersion == 0
                || !registrations.TryAdd((handler.Kind, handler.PayloadVersion), handler))
            {
                return Unregistered<GrimoireOfflineTransitionEffectHandlerRegistry>();
            }
        }

        return registrations.Count == 0
            ? Unregistered<GrimoireOfflineTransitionEffectHandlerRegistry>()
            : Result<GrimoireOfflineTransitionEffectHandlerRegistry>.Success(
                new GrimoireOfflineTransitionEffectHandlerRegistry(registrations));
    }

    /// <summary>The two kinds this build ships, which is the only composition production uses.</summary>
    internal static IReadOnlyList<IGrimoireOfflineTransitionEffectHandler> Declared { get; } =
    [
        new CovenantOfflineTransitionEffectHandler(
            GrimoireOfflineTransitionKind.CovenantReset,
            CovenantExclusiveOperation.CovenantReset,
            requiresOrdinaryContinuation: false),

        new CovenantOfflineTransitionEffectHandler(
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            requiresOrdinaryContinuation: true),
    ];

    internal Result<IGrimoireOfflineTransitionEffectHandler> Resolve(
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion) =>
        Enum.IsDefined(kind)
        && payloadVersion != 0
        && _handlers.TryGetValue(
            (kind, payloadVersion),
            out IGrimoireOfflineTransitionEffectHandler? handler)
            ? Result<IGrimoireOfflineTransitionEffectHandler>.Success(handler)
            : Unregistered<IGrimoireOfflineTransitionEffectHandler>();

    /// <summary>
    /// The one refusal this table makes, which never names the key it was asked for.
    /// </summary>
    /// <remarks>
    /// A kind and a version an operator did not choose and cannot act on, and putting them in a
    /// message would only invite somebody to try composing the missing registration by hand. What is
    /// actionable is that this build does not run this transition, which is what it says.
    /// </remarks>
    private static Error Unregistered() =>
        new(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "This build does not carry the offline-transition effect handler an authenticated journal "
            + "names, so it cannot run or resume that transition.");

    private static Result<T> Unregistered<T>() => Result<T>.Failure(Unregistered());
}
