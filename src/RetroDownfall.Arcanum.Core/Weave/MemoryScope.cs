using RetroDownfall.Arcanum.Core.Lexicon;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// What a memory read is allowed to draw on.
/// </summary>
public enum MemoryScopeKind
{

    /// <summary>
    /// Campaign scoping is off. Every durable memory on the installation is a candidate and the Lexicon
    /// answers from its global tier - which is exactly what both did before scopes existed.
    /// </summary>
    Installation = 0,

    /// <summary>Scoping is on and nothing resolved: the installation-scoped memories alone.</summary>
    GlobalOnly = 1,

    /// <summary>Scoping is on and one Campaign resolved: that Campaign's memories plus the global ones.</summary>
    Campaign = 2,

}

/// <summary>
/// The one description of a turn's memory scope, shared by retrieval and by every surface that inspects
/// it.
/// </summary>
/// <remarks>
/// Shared so that "inspection matches retrieval" is structural rather than a promise four call sites
/// keep separately: <c>memory explain</c> reports this value, and the turn retrieves with it.
///
/// <para>The Campaign is never taken from a caller. It comes from the invocation context a turn already
/// resolved canonically, or from the immutable binding a named Session carries - both of which are
/// statements Arcanum made, not ones a request supplied.</para>
/// </remarks>
public readonly record struct MemoryScope(MemoryScopeKind Kind, Guid? CampaignId)
{

    /// <summary>Today's behaviour: nothing is narrowed.</summary>
    public static MemoryScope Installation => new(MemoryScopeKind.Installation, null);

    /// <summary>
    /// The scope for a resolved Campaign under a given gate state. The single place the gate turns into
    /// a scope, so no surface can decide differently.
    /// </summary>
    public static MemoryScope Resolve(bool campaignScopingEnabled, Guid? campaignId) =>
        !campaignScopingEnabled
            ? Installation
            : campaignId is { } resolved
                ? new MemoryScope(MemoryScopeKind.Campaign, resolved)
                : new MemoryScope(MemoryScopeKind.GlobalOnly, null);

    public bool IsEnforced => Kind != MemoryScopeKind.Installation;

    /// <summary>The Lexicon tier a turn in this scope resolves first.</summary>
    public LexiconScope ToLexiconScope() =>
        Kind == MemoryScopeKind.Campaign && CampaignId is { } id
            ? LexiconScope.ForCampaign(id)
            : LexiconScope.Global;

    /// <summary>The Saga candidate set a turn in this scope may rank.</summary>
    public DivinationCampaignScope ToSagaScope() => SagaStorageKeys.CampaignScope(CampaignId);

}
