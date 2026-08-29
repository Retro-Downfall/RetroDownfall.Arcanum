namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// The one place the Campaign-scoped-memory gate turns into a scope, for every surface that reads or
/// writes durable memory.
/// </summary>
/// <remarks>
/// Retrieval, <c>read_saga</c>, <c>scribe_lexicon</c>, the <c>/api/saga</c> and <c>/api/memory</c>
/// routes, and <c>arcanum memory explain</c> all resolve through here. A surface that read the gate
/// itself could report a scope the turn would not use, and an operator inspecting memory would be shown
/// a set no turn ever sees.
/// </remarks>
public interface IMemoryScopeResolver
{

    /// <summary>Whether Campaign scoping is on at all, for a surface that has to say so.</summary>
    bool IsCampaignScopingEnabled { get; }

    /// <summary>
    /// The scope for a turn whose Campaign has already been resolved canonically.
    /// </summary>
    MemoryScope ForResolvedCampaign(Guid? campaignId);

    /// <summary>
    /// The scope a named Session's turn draws on, taken from that Session's immutable Campaign binding.
    /// </summary>
    /// <remarks>
    /// The binding is the canonical statement of a Session's authority, so this is the same answer the
    /// turn resolved rather than a second derivation of it. A Session that is unknown, unbound, or whose
    /// binding is unresolved supplies no Campaign, and the scope falls to the installation-scoped
    /// memories alone rather than to all of them.
    /// </remarks>
    ValueTask<MemoryScope> ResolveForSessionAsync(Guid? sessionId, CancellationToken cancellationToken);

}
