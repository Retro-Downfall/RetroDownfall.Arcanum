namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// The bounds the Annals schema enforces, restated so a caller can refuse before the database does.
/// </summary>
/// <remarks>
/// The database is the authority, not this type. A bound that lived only in a writer could be
/// bypassed by any other writer, which is why <c>annal_dependencies</c> carries the same ceiling as a
/// <c>CHECK</c> on its ordinal. These constants exist so a caller can produce a useful message rather
/// than a constraint abort, and a change here is a change to the schema file as well.
///
/// <para>No production writer reads the constant yet: <c>AnnalsClaimWriter</c> records one edge per
/// version, at ordinal 1. It is kept, rather than deleted with the other unconsumed limit constants,
/// because frozen schema files cite it by name and editing them would change their source fingerprints, and
/// it is pinned to the schema by
/// <c>AnnalsSchemaInvariantTests.The_edge_after_the_dependency_ceiling_is_refused_and_the_ceiling_is_the_published_constant</c>,
/// which fails if either side moves without the other.</para>
/// </remarks>
public static class AnnalLimits
{
    /// <summary>Matches <c>CHECK (Ordinal BETWEEN 1 AND 16)</c> on <c>annal_dependencies</c>.</summary>
    public const int MaxDependenciesPerVersion = 16;
}
