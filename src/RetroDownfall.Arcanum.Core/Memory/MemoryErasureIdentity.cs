using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// The scope byte an erasure fingerprint binds.
/// </summary>
/// <remarks>
/// Saga uses all four codes, which are <see cref="SagaMemoryScopeKind"/>'s own. Lexicon and Covenant
/// use only <see cref="Global"/> and <see cref="Campaign"/>. The codes are written into keyed
/// preimages, so they are append-only.
/// </remarks>
public enum MemoryErasureScopeKind : byte
{
    Unclassified = 0,

    Global = 1,

    Campaign = 2,

    LegacyUnresolved = 3,
}

/// <summary>
/// One erased identity: the exact thing a fingerprint suppresses in one exact scope.
/// </summary>
/// <remarks>
/// <para><see cref="Value"/> is the identity itself. For Saga it is the exact stored content, byte for
/// byte, so text that differs only in trailing whitespace, line endings, or Unicode normalization form
/// is a different identity. For Lexicon it is the name trimmed and upper-cased under the invariant
/// culture, always computed from the name rather than read from a stored normalized column. For
/// Covenant it is the normalized key as given.</para>
///
/// <para>A Campaign is named exactly when the scope is <see cref="MemoryErasureScopeKind.Campaign"/>,
/// and the preimage binds its sixteen GUID bytes rather than any spelling of it.</para>
/// </remarks>
public readonly record struct MemoryErasureIdentity(
    MemoryReviewStore Store,
    MemoryErasureScopeKind Scope,
    Guid? CampaignId,
    string Value)
{
    public static MemoryErasureIdentity ForSaga(SagaMemoryScopeKind scope, Guid? campaignId, string exactContent)
    {
        MemoryErasureScopeKind kind = scope switch
        {
            SagaMemoryScopeKind.Unclassified => MemoryErasureScopeKind.Unclassified,
            SagaMemoryScopeKind.Global => MemoryErasureScopeKind.Global,
            SagaMemoryScopeKind.Campaign => MemoryErasureScopeKind.Campaign,
            SagaMemoryScopeKind.LegacyUnresolved => MemoryErasureScopeKind.LegacyUnresolved,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), "A Saga erasure identity names a recognized scope."),
        };

        return Checked(new(MemoryReviewStore.Saga, kind, campaignId, exactContent));
    }

    public static MemoryErasureIdentity ForLexicon(Guid? campaignId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        MemoryErasureScopeKind kind = campaignId is null
            ? MemoryErasureScopeKind.Global
            : MemoryErasureScopeKind.Campaign;

        return Checked(new(MemoryReviewStore.Lexicon, kind, campaignId, name.Trim().ToUpperInvariant()));
    }

    public static MemoryErasureIdentity ForCovenant(CovenantScope scope, Guid? campaignId, string normalizedKey) =>
        Checked(new(MemoryReviewStore.Covenant, ScopeOf(scope), campaignId, normalizedKey));

    /// <summary>Maps a Covenant scope onto the erasure scope byte, refusing anything unrecognized.</summary>
    internal static MemoryErasureScopeKind ScopeOf(CovenantScope scope) =>
        scope switch
        {
            CovenantScope.Global => MemoryErasureScopeKind.Global,
            CovenantScope.Campaign => MemoryErasureScopeKind.Campaign,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), "A Covenant erasure identity names a recognized scope."),
        };

    /// <summary>
    /// Refuses a Campaign named outside Campaign scope, or a Campaign scope without a nonempty one.
    /// </summary>
    internal static void RequireCampaignPairing(MemoryErasureScopeKind scope, Guid? campaignId)
    {
        bool paired = scope == MemoryErasureScopeKind.Campaign
            ? campaignId is { } campaign && campaign != Guid.Empty
            : campaignId is null;

        if (!paired)
        {
            throw new ArgumentException(
                "An erasure scope names exactly one nonempty Campaign when it is Campaign scope, and none otherwise.",
                nameof(campaignId));
        }
    }

    /// <summary>
    /// Refuses a shape no fingerprint can describe: an unknown store, a scope that store never uses, a
    /// Campaign pairing that does not match the scope, or an empty identity.
    /// </summary>
    /// <remarks>
    /// Run by the grammar as well as the factories, because the positional constructor and
    /// <see langword="default"/> both bypass the factories.
    /// </remarks>
    internal void ThrowIfInvalid()
    {
        bool scopeAllowed = Store switch
        {
            MemoryReviewStore.Saga => Scope is MemoryErasureScopeKind.Unclassified
                or MemoryErasureScopeKind.Global
                or MemoryErasureScopeKind.Campaign
                or MemoryErasureScopeKind.LegacyUnresolved,
            MemoryReviewStore.Lexicon or MemoryReviewStore.Covenant =>
                Scope is MemoryErasureScopeKind.Global or MemoryErasureScopeKind.Campaign,
            _ => false,
        };

        if (!scopeAllowed)
        {
            throw new ArgumentException("This erasure identity pairs a store with a scope that store never uses.", nameof(Scope));
        }

        RequireCampaignPairing(Scope, CampaignId);

        if (string.IsNullOrEmpty(Value))
        {
            throw new ArgumentException("An erasure identity is never empty.", nameof(Value));
        }
    }

    private static MemoryErasureIdentity Checked(MemoryErasureIdentity identity)
    {
        identity.ThrowIfInvalid();

        return identity;
    }
}
