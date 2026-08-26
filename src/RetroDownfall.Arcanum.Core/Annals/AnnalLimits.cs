namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// The bounds the Annals schema enforces, restated so a caller can refuse before the database does.
/// </summary>
/// <remarks>
/// The database is the authority, not this type. A bound that lived only in a writer could be
/// bypassed by any other writer, which is why <c>annal_dependencies</c> carries the same ceiling as a
/// <c>CHECK</c> on its ordinal. These constants exist so a caller can produce a useful message rather
/// than a constraint abort, and a change here is a change to the schema file as well.
/// </remarks>
public static class AnnalLimits
{

    /// <summary>Matches <c>CHECK (Ordinal BETWEEN 1 AND 16)</c> on <c>annal_dependencies</c>.</summary>
    public const int MaxDependenciesPerVersion = 16;

}
