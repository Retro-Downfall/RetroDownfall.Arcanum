namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The three shipped version chains, built once from the catalog.
/// </summary>
/// <remarks>
/// Core is at version 15 and declares fourteen steps, Covenant canonical is at version 6 and declares five,
/// and the Covenant accelerator is still at version 1 and declares none. A tier that never left version 1
/// keeps the cheapest state there is - the loader, the planner's evolve arm, the installer's step arm,
/// and the backfill driver all run in production and find nothing to do - and a tier that has left it
/// pays for each step exactly once per installation.
///
/// <para>Authoring a version step means three edits in one change: the statement files under the
/// tier's <c>Transitions/V&lt;n&gt;/</c> folder, the tier's version constant here, and the pin for
/// the version the step leaves. The pin must be copied from the tier's <i>currently published</i>
/// fingerprint before any object file is edited, because nothing can recompute it afterwards.</para>
///
/// <para>The pins through Core version 6 and every Covenant pin were taken with
/// <see cref="GrimoireSchemaCatalog.ComputeRawSourceFingerprint"/>, which is what those installations
/// recorded. Core moved its published value to the normalized computation at version 6, so the pin for
/// the step leaving version 6 is normalized too. A tier always pins the exact computation its source
/// version published; choosing the other one would refuse every installation at that version.</para>
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
    /// memory from being re-extracted. The step declares no sweep: both columns are nullable and both
    /// tables start empty, so there is no existing row for one to classify.</para>
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
    ///
    /// <para>Version 6 is the step the tier's published fingerprint changes under, and it carries three
    /// unrelated pieces of work that all need one. It settles <c>LongRunningOperations</c>' three
    /// reference columns on the spelling their store now writes, so a column cannot hold two eras of
    /// rendering; it adds the expression indexes the retention sweep's normalized identity predicates
    /// need, since SQLite cannot answer <c>lower(replace(col, '-', ''))</c> from an ordinary column
    /// index and every one of those predicates was a full scan; and it gives
    /// <c>workspace_file_chunks</c> a <c>FileLength</c> column, because mtime equality alone cannot
    /// tell a rewritten file from an unchanged one when a filesystem hands back the timestamp it
    /// started with.</para>
    ///
    /// <para>Version 7 gives each Saga extraction watermark the exact Grimoire entry sequence it has
    /// committed and records each new native assistant finalization's extraction frontier on its
    /// durable replay guard. Its bounded sweep pages through each timestamp-only inherited watermark's
    /// Entries in sequence order and journals the last proven sequence between transactions. The first
    /// timestamp after the watermark ends that conservative prefix, so non-monotonic timestamps can
    /// cause replay but never skip an unpaid entry. Inherited finalization guards keep a null frontier
    /// because a later turn may already have made their original boundary impossible to reconstruct
    /// safely.</para>
    ///
    /// <para>Version 8 aligns <c>Sessions.TotalCostUsd</c> with EF's exact decimal TEXT mapping. The
    /// former NUMERIC affinity coerced provider decimal strings through SQLite REAL arithmetic and
    /// discarded significant digits. The atomic step renames the old column, adds the TEXT replacement,
    /// copies every inherited value, and drops the legacy column without rebuilding or detaching any
    /// Session-owned foreign key.</para>
    ///
    /// <para>Version 9 gives every persisted instant one fixed-width UTC <c>Z</c> spelling and moves
    /// every authoritative USD amount away from SQLite numeric affinity into exact decimal TEXT.
    /// Its managed, bounded sweep prevalidates each page and temporarily removes only the current
    /// table's update triggers inside the same transaction as the repairs, so immutable ledgers keep
    /// their guards across success, cancellation, failure, and restart. Version 10 adds the private
    /// durable claim that makes batch accounting recovery and artifact cleanup crash-resumable
    /// without exposing a nonstandard batch status.</para>
    ///
    /// <para>Version 11 adds Lexicon retirement, pinning, and a curation generation, distinguishes
    /// legacy and snapshot Annals hashes, and preserves content-free attachment coordinates by
    /// Annals version. Its atomic step replaces every Lexicon FTS trigger and rebuilds the search
    /// projection from active rows alone.</para>
    ///
    /// <para>Version 12 adds the Annals review queue: one review event per head change, recorded by
    /// triggers on <c>annal_heads</c>, with decision receipts and per-scope review markers. Its bounded
    /// sweep records an event for every head that already existed, so the queue starts with the
    /// installation's current memories rather than only the ones written after the upgrade.</para>
    ///
    /// <para>Version 13 adds erasure evidence: content-free erasure fingerprints, erasure receipts with
    /// their subject digests, and a guard that lets a receipt only clear its WAL checkpoint reason and
    /// become Verified once no reason remains. It also indexes the disclosure subjects whose receipts
    /// have not been folded yet, turns on FTS5 secure delete for <c>lexicon_fts</c>, and merges that
    /// index once so tokens left by earlier deletes are gone. The step declares no sweep: every new
    /// table starts empty, and the index, the setting and the merge all complete inside the step's own
    /// transaction.</para>
    ///
    /// <para>Version 14 adds three expression indexes in the normalized shape a Session delete compares
    /// with, <c>lower(replace(SessionId, '-', ''))</c>, on <c>attachment_memory_consultations</c>,
    /// <c>saga_extraction_watermarks</c> and <c>SessionContextPins</c>, so those deletes and their
    /// post-commit counts search rather than scan. The step declares no sweep: an index is built inside
    /// the step's own transaction.</para>
    ///
    /// <para>Version 15 settles the file identities on the canonical uppercase dashed spelling the rest of the
    /// schema holds. <c>UploadedFiles.Id</c> and the three file roles a batch names, <c>InputFileId</c>,
    /// <c>OutputFileId</c> and <c>ErrorFileId</c>, were written in lowercase dashed form while every lookup
    /// wrapped the column in <c>lower(replace(col, '-', ''))</c>, so deleting an uploaded file and checking
    /// whether a batch still named it scanned both tables. The step rewrites any non-canonical value in
    /// place, in the statement's own transaction, and indexes the three batch columns. It declares no sweep:
    /// both tables are small relative to the Entries family, no trigger or foreign key names either column,
    /// and the rewrite is one statement per column.</para>
    /// </remarks>
    internal const int CoreSchemaVersion = 15;

    /// <summary>The version of Covenant's authoritative tables this binary declares.</summary>
    /// <remarks>
    /// Version 2 added the curation substrate: which scoped lane heads an operator has pinned against
    /// agent authorship, and which Global keys a Campaign has masked. Version 3 rebuilt
    /// <c>covenant_versions</c> so new AgentApproved retirements can carry no Ward receipt while every
    /// historical Ward-backed tuple remains unchanged. Version 4 canonicalizes the authoritative
    /// Covenant tier's inherited instant text under its own failure domain.
    ///
    /// <para>Version 5 adds the Covenant review queue: one review event per head change, recorded by
    /// triggers on <c>covenant_heads</c>, with decision receipts and per-scope review markers. Its bounded
    /// sweep records an event for every head that already existed.</para>
    ///
    /// <para>Version 6 prepares the tier for erasing one entry. It gives <c>covenant_key_epochs</c> a
    /// fixed binding epoch, <c>IncarnationEpoch</c>, which every existing key row takes from its current
    /// <c>KeyEpoch</c> so live curation stays live, and which a key row created afterwards starts at 0; a
    /// guard makes it immutable. It gives <c>covenant_mutation_receipts</c> the entry each receipt
    /// resolved, with an index. It purges the curation a pre-fix family reset left behind: rows at a
    /// nonzero epoch whose key has no epoch row. It admits the entry-erasure authorization in the delete
    /// guards on an entry's closure and on the search outbox, and adds delete guards to
    /// <c>covenant_key_epochs</c> and <c>covenant_curation_heads</c>. The step declares no sweep: the
    /// backfill and the purge are single statements inside the step's own transaction.</para>
    /// </remarks>
    internal const int CovenantCanonicalSchemaVersion = 6;

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

            // Read out of the Core head tree before any object file was edited for
            // version 6 and before the tier's published computation moved to the normalized one. It is
            // therefore a raw value, like every pin above it and unlike the head fingerprint Core
            // publishes from version 6 onward, and that is not an inconsistency: this is the number a
            // version-5 installation wrote into grimoire_feature_schemas, so it is the only number that
            // can recognize one. CoreSchemaVersionFiveFixture reconstructs that tree and a test hashes
            // it raw, so a wrong value here fails there rather than against every operator's version-5
            // installation.
            [(GrimoireSchemaTransactionTier.Core, 6)] =
                "EFD0E3F2981B3462337E83BAAD2BE696AD3279452E85A11903CA6B636AC1B6F9",

            // Read out of the normalized Core version-6 head tree immediately before
            // saga_extraction_watermarks.sql gained its sequence cursor and
            // assistant_entry_finalizations.sql gained its replay frontier. Core has published
            // normalized fingerprints since version 6, so this pin deliberately uses the normalized
            // computation rather than the raw computation used by the older pins.
            // CoreSchemaVersionSixFixture reconstructs that tree and proves the literal below still
            // identifies it.
            [(GrimoireSchemaTransactionTier.Core, 7)] =
                "410CB4FD182E22CB7FA72955E337296177A2A0E92ACB4AF73137236285B0D8CB",

            // Read out of the normalized Core version-7 head tree immediately before Sessions.sql
            // changed TotalCostUsd from NUMERIC affinity to EF's exact decimal TEXT mapping.
            // CoreSchemaVersionSevenFixture reconstructs that tree and proves this literal still
            // recognizes an installation that published it.
            [(GrimoireSchemaTransactionTier.Core, 8)] =
                "814D62096F2A034B8CD8092FBA4D5A6A83BE22E2976EE4EECFEB6D3C9FB1E24A",

            // Captured from the normalized Core version-8 head before any authoritative USD column
            // or UTC-instant diagnostic object was edited for version 9. Core has published the
            // normalized computation since version 6. Version 8 shipped only inside this version-9
            // change, but its intermediate journal identity still has to name the exact tree produced
            // by the version-8 step before the version-9 step can accept it.
            [(GrimoireSchemaTransactionTier.Core, 9)] =
                "D1BC1D6158669F4E1070B7D7B00CA2F5697E3E6CCC9081EEA15167CE8D49C7B6",

            // Captured from the normalized Core version-9 head immediately before the private
            // batch-accounting recovery claim table was added. CoreSchemaVersionNineFixture removes
            // only that new object from the shipped tree and proves this pin still names version 9.
            [(GrimoireSchemaTransactionTier.Core, 10)] =
                "B0C9CE2CA6C343080B23E8DA8D79E6E3BC14B1120862C71C0095CC6B4668AD5E",

            // Captured before any version-11 head edit. The version-10 fixture freezes every
            // replaced object and excludes historical Lexicon provenance to preserve this identity.
            [(GrimoireSchemaTransactionTier.Core, 11)] =
                "B484778B9288D99C4337FA3C95BEB56B95FDAE6B1D149C6A9A2A822591BBE951",

            [(GrimoireSchemaTransactionTier.Core, 12)] =
                "A42B44B75CC2EA1E3D8E1949A37EE82D6D4D4DB37AA3335F732A54788DC9FF0B",

            // Captured from the normalized Core version-12 head before any version-13 head edit.
            // CoreSchemaVersionTwelveFixture removes the memory_erasure_ objects, freezes the three
            // disclosure tables whose text version 13 changes, and proves this literal still names
            // version 12. Its frozen copies also keep the raw version-1 to version-5 pins still.
            [(GrimoireSchemaTransactionTier.Core, 13)] =
                "616E371CA834F78D84C484E4918C4124F8399686B17E1E8D497557303C08063B",

            // Captured from the normalized Core version-13 head before any version-14 head edit.
            // CoreSchemaVersionThirteenFixture freezes the three tables version 14 appends an index to
            // and proves this literal still names version 13.
            [(GrimoireSchemaTransactionTier.Core, 14)] =
                "E46E5902803F25CD43236A77882E5B057374A7B902840E0AC1308427513A8D84",

            // Captured from the normalized Core version-14 head before any version-15 head edit.
            // CoreSchemaVersionFourteenFixture freezes the table version 15 appends indexes to and proves
            // this literal still names version 14.
            [(GrimoireSchemaTransactionTier.Core, 15)] =
                "F699757C9C5F2EDA486ECBF0CD1017762936D5730B8C01557FB33E5338347372",

            // Read out of the Covenant canonical head tree immediately before the curation objects were
            // added. Nothing can recompute it either. CovenantCanonicalSchemaVersionOneFixture
            // reconstructs that tree by removing those objects from the shipped list and a test hashes
            // it, so a wrong value here fails there rather than against every operator's version-1
            // installation.
            [(GrimoireSchemaTransactionTier.CovenantCanonical, 2)] =
                "7F906C4C832FDF824EC3B6A56431E9E6098DC9BB83EDA5BAE02EC62CE3B4E105",

            // Read out of the Covenant canonical head tree immediately before covenant_versions.sql
            // admitted receipt-free AgentApproved retirements. CovenantCanonicalSchemaVersionTwoFixture
            // reconstructs that tree with its frozen table resource and hashes it, so a wrong value here
            // fails before any version-two installation can be refused during the rebuild.
            [(GrimoireSchemaTransactionTier.CovenantCanonical, 3)] =
                "BC0914DABEF7A54B0637E66697EE47CC7F2077E67B40BCE6D824EDE2913EDC61",

            // Captured from the exact raw Covenant canonical version-3 head published by commit
            // 1e89b6e5, immediately before its UTC-instant diagnostic object was added for version 4.
            // This tier still publishes the raw computation, so the pin deliberately does too.
            [(GrimoireSchemaTransactionTier.CovenantCanonical, 4)] =
                "E85966D8DA8878566A10B08A75FBDD623064D0D7FABCFEBF4CA17F24A9E66BB1",

            [(GrimoireSchemaTransactionTier.CovenantCanonical, 5)] =
                "7E7B7B2B590EA4A4D4EEA8A0C463E813319BECA7E07750CAF51258F5BA35C6A2",

            // Captured from the raw Covenant canonical version-5 head before any version-6 head edit.
            // This tier still publishes the raw computation, so the pin does too.
            // CovenantCanonicalSchemaVersionFiveFixture removes the three objects version 6 added,
            // freezes the thirteen objects whose text version 6 changes, and proves this literal still
            // names version 5. Its frozen copies also keep the older canonical pins still.
            [(GrimoireSchemaTransactionTier.CovenantCanonical, 6)] =
                "E4C4284B895BBBE50515D18FAC6066348D73C3A7D166F434B96BA675697DA925",
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

            // The added sequence column begins null only while this sweep is pending. Each bounded
            // Entry page journals the Session and last proven sequence in the same transaction; only a
            // page that reaches the first later timestamp or the end replaces the transition-only value
            // with that conservative prefix (or zero where the first Entry is later or none exists).
            // The version is not published until no null remains, and the new write guards prevent a
            // later row from re-entering that state.
            [(GrimoireSchemaTransactionTier.Core, 7)] = new SagaExtractionCursorBackfill(),

            [(GrimoireSchemaTransactionTier.Core, 9)] = new UtcInstantCanonicalizationBackfill(
                "core-utc-instant-canonicalization",
                UtcInstantColumnInventory.CoreVersionNine),

            [(GrimoireSchemaTransactionTier.CovenantCanonical, 4)] = new UtcInstantCanonicalizationBackfill(
                "covenant-utc-instant-canonicalization",
                UtcInstantColumnInventory.CovenantCanonical),

            [(GrimoireSchemaTransactionTier.Core, 12)] = new AnnalReviewEventBackfill(),

            [(GrimoireSchemaTransactionTier.CovenantCanonical, 5)] = new CovenantReviewEventBackfill(),
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
