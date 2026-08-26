namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The three shipped version chains, built once from the catalog.
/// </summary>
/// <remarks>
/// Core is at version 2 and declares one step; both Covenant tiers are still at version 1 and declare
/// none. A tier that never left version 1 keeps the cheapest state there is - the loader, the planner's
/// evolve arm, the installer's step arm, and the backfill driver all run in production and find nothing
/// to do - and a tier that has left it pays for the step exactly once per installation.
///
/// <para>Authoring a version step means three edits in one change: the statement files under the
/// tier's <c>Transitions/V&lt;n&gt;/</c> folder, the tier's version constant here, and the pin for
/// the version the step leaves. The pin must be copied from the tier's <i>currently published</i>
/// fingerprint before any object file is edited, because nothing can recompute it afterwards.</para>
/// </remarks>
internal static class GrimoireSchemaVersionChains
{

    /// <summary>The version of the durable schema this binary declares.</summary>
    /// <remarks>
    /// Version 2 gave <c>saga_memories</c> an explicit scope classification and <c>lexicon_entries</c> an
    /// optional Campaign scope, so cross-session recall follows the work rather than the installation.
    /// </remarks>
    internal const int CoreSchemaVersion = 2;

    /// <summary>The version of Covenant's authoritative tables this binary declares.</summary>
    internal const int CovenantCanonicalSchemaVersion = 1;

    /// <summary>The version of Covenant's inspection index this binary declares.</summary>
    internal const int CovenantAcceleratorSchemaVersion = 1;

    /// <summary>
    /// The source-definition fingerprint each tier's head tree published at the version a step
    /// leaves, keyed by the tier and the version that step targets.
    /// </summary>
    private static readonly IReadOnlyDictionary<(GrimoireSchemaTransactionTier Tier, int ToVersion), string> SourcePins =
        new Dictionary<(GrimoireSchemaTransactionTier, int), string>
        {

            // Read out of the Core head tree immediately before saga_memories.sql and
            // lexicon_entries.sql were edited for version 2. Nothing can recompute it: the tree that
            // produced it no longer exists. A test reconstructs that tree from the two files' frozen
            // version-1 text and hashes it, so a wrong value here fails there rather than against every
            // operator's version-1 installation.
            [(GrimoireSchemaTransactionTier.Core, 2)] =
                "8B61C1EB09EC018B7477D56A475E13BCD67ADFA47B45D64BC05CE2C9D5D36EFA",

        };

    /// <summary>The sweep each step depends on, keyed the same way.</summary>
    private static readonly IReadOnlyDictionary<(GrimoireSchemaTransactionTier Tier, int ToVersion), IGrimoireSchemaBackfill> Backfills =
        new Dictionary<(GrimoireSchemaTransactionTier, int), IGrimoireSchemaBackfill>
        {

            // The Lexicon half of version 2 needs no sweep - its column is NOT NULL DEFAULT '' and every
            // existing row is global the moment it exists - so the step depends on the Saga
            // classification alone.
            [(GrimoireSchemaTransactionTier.Core, 2)] = new SagaMemoryCampaignScopeBackfill(),

        };

    private static readonly Lazy<GrimoireSchemaVersionChainSet> LoadedDefault =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static GrimoireSchemaVersionChainSet Default => LoadedDefault.Value;

    private static GrimoireSchemaVersionChainSet Build() =>
        new(
        [
            BuildChain(GrimoireSchemaManifests.Core, GrimoireSchemaCatalog.CoreObjects),
            BuildChain(GrimoireSchemaManifests.CovenantCanonical, GrimoireSchemaCatalog.CovenantCanonicalObjects),
            BuildChain(GrimoireSchemaManifests.CovenantAccelerator, GrimoireSchemaCatalog.CovenantAcceleratorObjects),
        ]);

    private static GrimoireSchemaVersionChain BuildChain(
        GrimoireSchemaManifest headManifest,
        IReadOnlyList<GrimoireSchemaObject> headObjects)
    {

        List<GrimoireSchemaVersionStep> steps = [];

        for (int toVersion = 2; toVersion <= headManifest.Version; toVersion++)
        {

            List<GrimoireSchemaTransitionStatement> statements =
            [
                .. GrimoireSchemaCatalog.TransitionStatements
                    .Where(statement =>
                        statement.TransactionTier == headManifest.TransactionTier
                        && statement.ToVersion == toVersion)
                    .Select(static statement => new GrimoireSchemaTransitionStatement(
                        statement.ResourcePath,
                        statement.Ordinal,
                        statement.Name,
                        statement.Sql)),
            ];

            if (!SourcePins.TryGetValue((headManifest.TransactionTier, toVersion), out string? pin))
            {

                throw new InvalidOperationException(
                    $"The {headManifest.TransactionTier} schema step to version {toVersion} has no pinned "
                    + "source-definition fingerprint for the version it leaves. Record the tier's published "
                    + "fingerprint before editing any object file; it cannot be recovered afterwards.");

            }

            _ = Backfills.TryGetValue((headManifest.TransactionTier, toVersion), out IGrimoireSchemaBackfill? backfill);

            steps.Add(
                new GrimoireSchemaVersionStep(
                    headManifest.Family,
                    headManifest.TransactionTier,
                    toVersion - 1,
                    toVersion,
                    pin,
                    statements,
                    backfill));

        }

        return new GrimoireSchemaVersionChain(headManifest, headObjects, steps);

    }

}
