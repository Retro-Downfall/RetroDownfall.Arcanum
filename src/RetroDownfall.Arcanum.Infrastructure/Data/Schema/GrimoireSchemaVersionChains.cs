namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The three shipped version chains, built once from the catalog.
/// </summary>
/// <remarks>
/// Core is at version 5 and declares four steps, Covenant canonical is at version 2 and declares one,
/// and the Covenant accelerator is still at version 1 and declares none. A tier that never left version 1
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
    ///
    /// <para>Version 4 gave <c>saga_memories</c> two nullable lifecycle columns, <c>RetiredAtUtc</c> and
    /// <c>PinnedAtUtc</c>, and added <c>saga_retirement_suppressions</c> and <c>saga_suppression_key</c>:
    /// the storage an operator's curation verbs need to retire or pin a memory, and to keep a retired
    /// memory from being re-extracted. No verb writes to any of it yet.</para>
    ///
    /// <para>Version 5 settles every stored identity on one spelling, so a comparison can be an exact
    /// indexed equality again. Its sweep counts each identity column it governs before it touches one
    /// and records what it found, which is what tells an installation that already held the canonical
    /// form apart from one that did not. What it repairs is narrower than what it counts, and the sweep
    /// is where that boundary is drawn rather than here: an identity a row is known by cannot be moved
    /// in place at all, because the tables depending on a Session identity refuse the write by trigger.
    /// It also installs the write-time guards that keep the form once it is settled: one
    /// <c>BEFORE INSERT</c> per governed identity column, and one <c>BEFORE UPDATE OF</c> that column
    /// wherever the table does not already refuse every update.</para>
    /// </remarks>
    internal const int CoreSchemaVersion = 5;

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
            // produced it no longer exists. A test reconstructs that tree from those files' frozen
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

            // Read out of the Core head tree immediately before saga_memories.sql gained its lifecycle
            // columns and the two suppression objects were added. Nothing can recompute it either.
            // CoreSchemaVersionThreeFixture reconstructs that tree and a test hashes it, so a wrong
            // value here fails there rather than against every operator's version-3 installation.
            [(GrimoireSchemaTransactionTier.Core, 4)] =
                "2CC5BB384111470F86668C4928B54306C7B8F7DCFDBBB152DF9F7C0CF162CC2F",

            // Read out of the Core head tree immediately before the first identity guard trigger was
            // added. Nothing can recompute it either. CoreSchemaVersionFourFixture reconstructs that
            // tree and a test hashes it, so a wrong value here fails there rather than against every
            // operator's version-4 installation. How that reconstruction is built is kept there, with
            // the objects it names.
            [(GrimoireSchemaTransactionTier.Core, 5)] =
                "35B3B5AD90B8BE3571516C88CB0FDF4F8E61712F86F8D1134D07D92B3F980AC1",

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

            // Version 5's DDL is the guard triggers, plus the replacement the sweep beneath it cannot
            // run without: session_campaign_bindings_guard_update gains a spelling-only exemption, since
            // the version-four guard aborts every update to a binding whose kind is not 3 and every
            // binding carrying a Campaign has kind 2. The sweep is the half of the step that answers for
            // the data: it counts the identity columns it declares before it touches one, so an
            // installation that already holds the canonical form says so in its log rather than passing
            // silently. It repairs a reference only where the identity it names already exists, and the
            // Campaign columns on their own shape, because those name no stored column at all. The
            // attachment family is where it rewrites data rather than verifying it, and the family moves
            // inside one transaction because members of it join to the parent with no foreign key and
            // nothing but the sweep's own declaration pairs them.
            [(GrimoireSchemaTransactionTier.Core, 5)] = new IdentitySpellingBackfill(),

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
