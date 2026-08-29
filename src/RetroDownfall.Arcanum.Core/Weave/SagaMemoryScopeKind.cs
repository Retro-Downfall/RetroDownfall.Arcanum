namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// Whose memory a Saga row is, recorded on the row itself rather than derived from a join at
/// retrieval time.
/// </summary>
/// <remarks>
/// Codes 1 to 3 are deliberately the codes <c>session_campaign_bindings.BindingKindCode</c> already
/// uses, because a memory's scope is its owning Session's binding at the moment it was written. Code 0
/// exists only for a row an upgrade has not reached yet.
///
/// <para>A single nullable Campaign column would have collapsed <see cref="Global"/> and
/// <see cref="LegacyUnresolved"/> into one null, and those two must never be the same answer: an
/// explicitly installation-global memory is retrievable everywhere, and one whose ownership was never
/// resolved is retrievable nowhere until an operator resolves it.</para>
/// </remarks>
public enum SagaMemoryScopeKind
{

    /// <summary>An upgrade has not classified this row yet. Never retrievable under Campaign scoping.</summary>
    Unclassified = 0,

    /// <summary>Explicitly installation-scoped: retrievable inside every Campaign and outside all of them.</summary>
    Global = 1,

    /// <summary>Owned by exactly one Campaign, which the row names. Campaign deletion does not change it.</summary>
    Campaign = 2,

    /// <summary>
    /// The owning Session's binding is unresolved, or the Session is gone. It supplies no authority, so
    /// it is retrievable nowhere until the binding is resolved.
    /// </summary>
    LegacyUnresolved = 3,

}
