namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The three shipped version chains, built once from the catalog.
/// </summary>
/// <remarks>
/// Core is at version 3 and declares two steps, Covenant canonical is at version 2 and declares one, and
/// the Covenant accelerator is still at version 1 and declares none. A tier that never left version 1
/// keeps the cheapest state there is - the loader, the planner's evolve arm, the installer's step arm,
/// and the backfill driver all run in production and find nothing to do - and a tier that has left it
/// pays for each step exactly once per installation.
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
    ///
    /// <para>Version 3 added the Annals: durable memory now records what it claimed, who asserted it,
    /// when that was true, when Arcanum came to hold it, and which earlier claims it rests on.</para>
    /// </remarks>
    internal const int CoreSchemaVersion = 3;

    /// <summary>The version of Covenant's authoritative tables this binary declares.</summary>
    /// <remarks>
    /// Version 2 added the curation substrate: which scoped lane heads an operator has pinned against
    /// agent authorship, and which Global keys a Campaign has masked. Every object is new, so the step
    /// adds and alters nothing - which is what lets a fresh installation and an evolved one describe
    /// the same tree, since CREATE TABLE stores its statement verbatim and ALTER TABLE does not.
    /// </remarks>
    internal const int CovenantCanonicalSchemaVersion = 2;

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

            // Read out of the Core head tree immediately before the Annals objects were added. Nothing
            // can recompute it either. CoreSchemaVersionTwoFixture reconstructs that tree by removing
            // the Annals objects from the shipped list and a test hashes it, so a wrong value here fails
            // there rather than against every operator's version-2 installation.
            [(GrimoireSchemaTransactionTier.Core, 3)] =
                "CEFA40F472EB4815F13B257327F8FA78C00B6F671C78DCAB89E4A38B40646F2C",

            // Read out of the Covenant canonical head tree immediately before the curation objects were
            // added. Nothing can recompute it either. CovenantCanonicalSchemaVersionOneFixture
            // reconstructs that tree by removing those objects from the shipped list and a test hashes
            // it, so a wrong value here fails there rather than against every operator's version-1
            // installation.
            [(GrimoireSchemaTransactionTier.CovenantCanonical, 2)] =
                "7F906C4C832FDF824EC3B6A56431E9E6098DC9BB83EDA5BAE02EC62CE3B4E105",

        };

    /// <summary>The sweep each step depends on, keyed the same way.</summary>
    private static readonly IReadOnlyDictionary<(GrimoireSchemaTransactionTier Tier, int ToVersion), IGrimoireSchemaBackfill> Backfills =
        new Dictionary<(GrimoireSchemaTransactionTier, int), IGrimoireSchemaBackfill>
        {

            // The Lexicon half of version 2 needs no sweep - its column is NOT NULL DEFAULT '' and every
            // existing row is global the moment it exists - so the step depends on the Saga
            // classification alone.
            [(GrimoireSchemaTransactionTier.Core, 2)] = new SagaMemoryCampaignScopeBackfill(),

            // Version 3's objects are all new, so the step's DDL needs no sweep to be correct. The sweep
            // is what makes it useful: without it the Annals would hold nothing but claims written after
            // the upgrade, and every memory an installation already had would be unexplained.
            [(GrimoireSchemaTransactionTier.Core, 3)] = new MemoryAnnalsBackfill(),

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
