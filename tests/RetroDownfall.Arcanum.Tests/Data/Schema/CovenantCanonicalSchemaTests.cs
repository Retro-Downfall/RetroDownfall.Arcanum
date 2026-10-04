using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The Covenant canonical transaction tier: ten tables and the triggers that make them behave like
/// an append-only ledger with a denormalized head projection over it.
/// </summary>
/// <remarks>
/// Every test installs the tier into a fresh encrypted file-backed Grimoire and then writes real
/// rows through it. The point is that the guarantees are enforced by the database rather than by the
/// repository layer above it, so an assertion that only inspected DDL text would prove nothing about
/// what a stray statement can actually do.
/// </remarks>
public sealed class CovenantCanonicalSchemaTests
{

    /// <summary>
    /// The provider has to be installed before the first SQLCipher connection is constructed. The
    /// fixture does this too, but repeating it here keeps a run filtered to this class from
    /// depending on which type happened to be touched first.
    /// </summary>
    static CovenantCanonicalSchemaTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The whole canonical tier. Declared here rather than derived from the catalog, so a table that
    /// silently disappeared from the embedded tree fails the catalog test instead of quietly
    /// shrinking every other test's coverage.
    /// </summary>
    private static readonly string[] CanonicalTableNames =
    [
        "covenant_curation_heads",
        "covenant_curation_receipts",
        "covenant_curation_versions",
        "covenant_entries",
        "covenant_heads",
        "covenant_key_epochs",
        "covenant_mutation_receipts",
        "covenant_review_decision_receipts",
        "covenant_review_events",
        "covenant_review_markers",
        "covenant_search_outbox",
        "covenant_state",
        "covenant_turn_receipt_aggregate",
        "covenant_turn_receipts",
        "covenant_version_attachment_provenance",
        "covenant_versions",
    ];

    /// <summary>
    /// Tables whose rows are written once and never revised, each paired with an update that would
    /// otherwise be perfectly valid. Every one carries an unconditional update guard and a delete
    /// guard only an authorized cleanup scope can pass.
    /// </summary>
    private static readonly string[][] ImmutableTables =
    [
        ["covenant_entries", "UPDATE covenant_entries SET AuthoredKey = 'rewritten';"],
        ["covenant_versions", "UPDATE covenant_versions SET CompiledByteCost = 1;"],
        [
            "covenant_version_attachment_provenance",
            "UPDATE covenant_version_attachment_provenance SET LogicalKey = 'rewritten';",
        ],
        ["covenant_mutation_receipts", "UPDATE covenant_mutation_receipts SET SourceTurnId = 'rewritten';"],
        ["covenant_turn_receipts", "UPDATE covenant_turn_receipts SET MutationCount = 99;"],
    ];

    /// <summary>
    /// The guarded tables an entry erasure empties for its entry, in an order that deletes every child
    /// before its parent. The outbox is guarded separately, under its own authorization set.
    /// </summary>
    private static readonly string[] EntryClosureTables =
    [
        "covenant_version_attachment_provenance",
        "covenant_versions",
        "covenant_entries",
        "covenant_mutation_receipts",
        "covenant_curation_heads",
        "covenant_curation_versions",
        "covenant_curation_receipts",
        "covenant_key_epochs",
    ];

    /// <summary>The theory row that opens no authorization at all.</summary>
    private const string NoAuthorization = "None";

    /// <summary>
    /// A fixed instant rather than the clock, so a failure diff is a real difference and not a
    /// timestamp that moved between two runs.
    /// </summary>
    private static readonly string Timestamp =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);

    private static readonly string LaterTimestamp =
        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);

    public static TheoryData<string> AuthorizationsOutsideTheEntryClosure =>
        AuthorizationsExcept(
            CovenantSqliteAuthorizationKind.OwnerCleanup,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance,
            CovenantSqliteAuthorizationKind.CovenantEntryErasure);

    public static TheoryData<string> AuthorizationsOutsideTheOutbox =>
        AuthorizationsExcept(
            CovenantSqliteAuthorizationKind.AcceleratorSynchronization,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance,
            CovenantSqliteAuthorizationKind.CovenantEntryErasure);

    [Fact]
    public void Canonical_catalog_contains_every_declared_table()
    {

        List<string> tables = [];

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantCanonicalObjects)
        {

            if (definition.Category == GrimoireSchemaCategory.Tables)
            {

                tables.Add(definition.Name);

            }

        }

        tables.Sort(StringComparer.Ordinal);

        Assert.Equal(CanonicalTableNames, tables);

    }

    /// <summary>
    /// Covenant is an optional tier that can fail to install without taking the Grimoire with it.
    /// That only holds while nothing canonical points into the durable core: a foreign key into
    /// <c>Campaigns</c>, <c>Sessions</c>, <c>Entries</c>, or <c>SessionAttachments</c> would make
    /// core deletion depend on an optional tier being present and healthy. Owner identities are
    /// therefore carried as plain historical text and reconciled through the core deletion journal.
    /// </summary>
    [Fact]
    public async Task Canonical_tables_reference_only_canonical_tables()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        int referenceCount = 0;

        foreach (string table in CanonicalTableNames)
        {

            List<string> referenced = await ReadColumnAsync(
                database,
                $"PRAGMA foreign_key_list('{table}');",
                ordinal: 2);

            foreach (string target in referenced)
            {

                referenceCount++;

                Assert.True(
                    CanonicalTableNames.Contains(target, StringComparer.Ordinal),
                    $"{table} references non-canonical table {target}");

            }

        }

        // Guards against the assertion above passing because the pragma returned nothing at all.
        Assert.True(referenceCount > 0, "no canonical foreign keys were read");

    }

    /// <summary>
    /// Global and Campaign keys occupy separate namespaces. One unique index over
    /// <c>(CampaignId, NormalizedKey)</c> could not express that, because SQLite treats every NULL
    /// as distinct and would let the same Global key be claimed without limit. Two partial indexes
    /// split the rule instead: one over the key alone where there is no Campaign, one over the pair
    /// where there is.
    /// </summary>
    [Fact]
    public async Task Global_and_Campaign_keys_use_separate_partial_unique_indexes()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        string? globalIndex = await database.ScalarStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'ux_covenant_entries_global_key';",
            CancellationToken.None);

        string? campaignIndex = await database.ScalarStringAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'ux_covenant_entries_campaign_key';",
            CancellationToken.None);

        Assert.NotNull(globalIndex);

        Assert.NotNull(campaignIndex);

        Assert.Contains("WHERE", globalIndex, StringComparison.Ordinal);

        Assert.Contains("WHERE", campaignIndex, StringComparison.Ordinal);

        string campaignA = NewId();

        string campaignB = NewId();

        await database.ExecuteAsync(
            InsertEntry(NewId(), scopeCode: 1, campaignId: null, normalizedKey: "shared.key"),
            CancellationToken.None);

        SqliteException duplicateGlobal = await AssertRaisesAsync(
            database,
            InsertEntry(NewId(), scopeCode: 1, campaignId: null, normalizedKey: "shared.key"));

        Assert.Contains("UNIQUE constraint failed", duplicateGlobal.Message, StringComparison.Ordinal);

        await database.ExecuteAsync(
            InsertEntry(NewId(), scopeCode: 2, campaignId: campaignA, normalizedKey: "shared.key"),
            CancellationToken.None);

        // The same key under a different Campaign is a different key, and the Global row above does
        // not block either of them.
        await database.ExecuteAsync(
            InsertEntry(NewId(), scopeCode: 2, campaignId: campaignB, normalizedKey: "shared.key"),
            CancellationToken.None);

        SqliteException duplicateCampaign = await AssertRaisesAsync(
            database,
            InsertEntry(NewId(), scopeCode: 2, campaignId: campaignA, normalizedKey: "shared.key"));

        Assert.Contains("UNIQUE constraint failed", duplicateCampaign.Message, StringComparison.Ordinal);

        Assert.Equal(
            3L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_entries;", CancellationToken.None));

    }

    /// <summary>
    /// A head denormalizes its entry's scope, Campaign, and key alongside its version's byte cost
    /// and origin, so resolution and quota reads answer from one row. The composite foreign key
    /// proves the head points at a version of the right entry, lane, revision, and operation; the
    /// validation triggers prove the copied fields were transcribed from that entry and version
    /// rather than invented.
    /// </summary>
    [Fact]
    public async Task Version_revision_and_head_projection_have_composite_integrity()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await AllocateSearchRowIdsAsync(database);

        string campaignA = NewId();

        string campaignB = NewId();

        string entryOne = NewId();

        string versionOne = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryOne, scopeCode: 2, campaignId: campaignA, normalizedKey: "alpha.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionOne, entryOne, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 128),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertHead(
                entryOne,
                laneCode: 1,
                versionId: versionOne,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "alpha.key",
                compiledByteCost: 128,
                originCode: 1,
                searchRowId: 1),
            CancellationToken.None);

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_heads;", CancellationToken.None));

        // A second entry with no head yet, so every rejection below is the rule under test rather
        // than the primary key of the head already installed.
        string entryTwo = NewId();

        string versionTwo = NewId();

        string versionTwoProposed = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryTwo, scopeCode: 2, campaignId: campaignA, normalizedKey: "beta.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionTwo, entryTwo, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 256),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionTwoProposed, entryTwo, laneCode: 2, laneRevision: 1, operationCode: 1, compiledByteCost: 256),
            CancellationToken.None);

        SqliteException driftedScope = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 1,
                campaignId: null,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 2));

        Assert.Contains("scope of its entry", driftedScope.Message, StringComparison.Ordinal);

        SqliteException driftedCampaign = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignB,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 3));

        Assert.Contains("Campaign of its entry", driftedCampaign.Message, StringComparison.Ordinal);

        SqliteException driftedKey = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "alpha.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 4));

        Assert.Contains("normalized key of its entry", driftedKey.Message, StringComparison.Ordinal);

        SqliteException driftedCost = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 999,
                originCode: 1,
                searchRowId: 5));

        Assert.Contains("compiled byte cost", driftedCost.Message, StringComparison.Ordinal);

        SqliteException driftedOrigin = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 2,
                searchRowId: 6));

        Assert.Contains("origin of its current version", driftedOrigin.Message, StringComparison.Ordinal);

        // A third entry whose version nothing points at yet, so the foreign key is what refuses the
        // cross-entry head rather than the unique index over CurrentVersionId.
        string entryThree = NewId();

        string versionThree = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryThree, scopeCode: 2, campaignId: campaignA, normalizedKey: "gamma.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionThree, entryThree, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 256),
            CancellationToken.None);

        SqliteException foreignEntry = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionThree,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 7));

        Assert.Contains("FOREIGN KEY constraint failed", foreignEntry.Message, StringComparison.Ordinal);

        // The head copies the Proposed version's own origin, so the composite reference is the only
        // rule left for the lane mismatch to break.
        SqliteException foreignLane = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwoProposed,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 2,
                searchRowId: 8));

        Assert.Contains("FOREIGN KEY constraint failed", foreignLane.Message, StringComparison.Ordinal);

        SqliteException foreignRevision = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 2,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 9));

        Assert.Contains("FOREIGN KEY constraint failed", foreignRevision.Message, StringComparison.Ordinal);

        SqliteException foreignOperation = await AssertRaisesAsync(
            database,
            InsertHead(
                entryTwo,
                laneCode: 1,
                versionId: versionTwo,
                laneRevision: 1,
                operationCode: 2,
                scopeCode: 2,
                campaignId: campaignA,
                normalizedKey: "beta.key",
                compiledByteCost: 256,
                originCode: 1,
                searchRowId: 10));

        Assert.Contains("FOREIGN KEY constraint failed", foreignOperation.Message, StringComparison.Ordinal);

        // None of the rejected shapes landed.
        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_heads;", CancellationToken.None));

    }

    /// <summary>
    /// New AgentApproved retirements are receipt-free, while the retained Ward vocabulary still admits
    /// only the historical interactive and configured-auto-approval evidence tuples.
    /// </summary>
    [Fact]
    public async Task AgentApproved_Ward_evidence_tuples_preserve_legacy_rows_and_admit_receipt_free_retirement()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        (long Origin, string WardDigest, string AuthorizationMode, bool Accepted)[] cases =
        [
            (3, "NULL", "NULL", true),
            (3, "randomblob(32)", "2", true),
            (3, "randomblob(32)", "3", true),
            (3, "randomblob(32)", "1", false),
            (3, "randomblob(32)", "NULL", false),
            (3, "NULL", "2", false),
            (1, "randomblob(32)", "NULL", false),
            (1, "NULL", "2", false),
            (2, "randomblob(32)", "NULL", false),
            (2, "NULL", "3", false),
        ];

        foreach ((long origin, string wardDigest, string authorizationMode, bool accepted) in cases)
        {

            string entryId = NewId();

            await database.ExecuteAsync(
                InsertEntry(entryId, scopeCode: 2, campaignId: NewId(), normalizedKey: $"ward.tuple.{entryId}"),
                CancellationToken.None);

            string sql = InsertShapedVersion(
                NewId(),
                entryId,
                laneCode: origin == 2 ? 2 : 1,
                laneRevision: 1,
                operationCode: 2,
                ["NULL", "NULL", "NULL", "NULL"],
                origin: origin,
                useDefaultWardFields: false,
                wardReceiptDigest: wardDigest,
                authorizationModeCode: authorizationMode);

            if (accepted)
            {

                await database.ExecuteAsync(sql, CancellationToken.None);

            }
            else
            {

                _ = await AssertRaisesAsync(database, sql);

            }

        }

    }

    /// <summary>
    /// Agent-proposed content is Campaign-scoped by construction. A Global Proposed head would let a
    /// proposal nobody has confirmed apply to every Campaign on the installation at once, so the
    /// combination is refused at the table rather than left to the layer that writes heads.
    /// </summary>
    [Fact]
    public async Task Global_Proposed_is_rejected()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await AllocateSearchRowIdsAsync(database);

        string entryId = NewId();

        string versionId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 1, campaignId: null, normalizedKey: "global.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionId, entryId, laneCode: 2, laneRevision: 1, operationCode: 1, compiledByteCost: 64),
            CancellationToken.None);

        // The head carries the version's own origin, which the Proposed lane pins to the agent
        // proposal: any other value aborts in the validate-insert trigger and would leave the
        // Global-Proposed rule untested.
        SqliteException rejected = await AssertRaisesAsync(
            database,
            InsertHead(
                entryId,
                laneCode: 2,
                versionId: versionId,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 1,
                campaignId: null,
                normalizedKey: "global.key",
                compiledByteCost: 64,
                originCode: 2,
                searchRowId: 1));

        Assert.Contains("CHECK constraint failed", rejected.Message, StringComparison.Ordinal);

        Assert.Equal(
            0L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_heads;", CancellationToken.None));

    }

    /// <summary>
    /// An agent proposal belongs to the Proposed lane. The contracts refuse the Confirmed pairing on
    /// the way in and the snapshot projection refuses it on the way out, but neither runs for a
    /// writer holding a connection, so the table refuses it too. The rule runs one way: an operator
    /// and an approved agent retirement both reach either lane, because retiring a proposal is how a
    /// proposal ends.
    /// </summary>
    [Fact]
    public async Task An_agent_proposal_cannot_be_seated_on_the_Confirmed_lane()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        string entryId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 2, campaignId: NewId(), normalizedKey: "lane.key"),
            CancellationToken.None);

        const string Authored = "'authored covenant text'";

        const string Compiled = "'compiled covenant text'";

        const string Digest = "randomblob(32)";

        string[] set = [Authored, Compiled, Digest, Digest];

        SqliteException proposalOnConfirmed = await AssertRaisesAsync(
            database,
            InsertShapedVersion(NewId(), entryId, laneCode: 1, laneRevision: 1, operationCode: 1, shape: set, origin: 2));

        Assert.Contains("CHECK constraint failed", proposalOnConfirmed.Message, StringComparison.Ordinal);

        // Each accepted pairing lands on its own entry, so the lane-revision uniqueness index is
        // never what admits or refuses one.
        string retiredEntryId = NewId();

        await database.ExecuteAsync(
            InsertEntry(retiredEntryId, scopeCode: 2, campaignId: NewId(), normalizedKey: "lane.retired"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertShapedVersion(NewId(), entryId, laneCode: 1, laneRevision: 1, operationCode: 1, shape: set, origin: 1),
            CancellationToken.None);

        // Retiring a proposal is how a proposal ends, so both non-proposing origins reach the
        // Proposed lane.
        await database.ExecuteAsync(
            InsertShapedVersion(
                NewId(),
                retiredEntryId,
                laneCode: 2,
                laneRevision: 1,
                operationCode: 2,
                shape: ["NULL", "NULL", "NULL", "NULL"],
                origin: 1),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertShapedVersion(
                NewId(),
                entryId,
                laneCode: 2,
                laneRevision: 1,
                operationCode: 2,
                shape: ["NULL", "NULL", "NULL", "NULL"],
                origin: 3),
            CancellationToken.None);

        Assert.Equal(
            3L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_versions;", CancellationToken.None));

    }

    /// <summary>
    /// A Set carries authored and compiled text with a hash of each; a Retire is a tombstone and
    /// carries none of the four. Storing half of either shape is what would let a retirement keep
    /// text the operator asked to remove, or let a Set claim content whose hash was never recorded.
    /// </summary>
    [Fact]
    public async Task Set_and_Retire_content_shapes_are_enforced()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        string entryId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 2, campaignId: NewId(), normalizedKey: "shape.key"),
            CancellationToken.None);

        const string Authored = "'authored covenant text'";

        const string Compiled = "'compiled covenant text'";

        const string Digest = "randomblob(32)";

        // A Set missing any one of the four is rejected, one field at a time.
        string[][] incompleteSets =
        [
            ["NULL", Compiled, Digest, Digest],
            [Authored, "NULL", Digest, Digest],
            [Authored, Compiled, "NULL", Digest],
            [Authored, Compiled, Digest, "NULL"],
        ];

        foreach (string[] shape in incompleteSets)
        {

            SqliteException rejected = await AssertRaisesAsync(
                database,
                InsertShapedVersion(NewId(), entryId, laneCode: 1, laneRevision: 1, operationCode: 1, shape: shape));

            Assert.Contains("CHECK constraint failed", rejected.Message, StringComparison.Ordinal);

        }

        // A Retire carrying any one of the four is rejected the same way.
        string[][] contaminatedRetires =
        [
            [Authored, "NULL", "NULL", "NULL"],
            ["NULL", Compiled, "NULL", "NULL"],
            ["NULL", "NULL", Digest, "NULL"],
            ["NULL", "NULL", "NULL", Digest],
        ];

        foreach (string[] shape in contaminatedRetires)
        {

            SqliteException rejected = await AssertRaisesAsync(
                database,
                InsertShapedVersion(NewId(), entryId, laneCode: 1, laneRevision: 1, operationCode: 2, shape: shape));

            Assert.Contains("CHECK constraint failed", rejected.Message, StringComparison.Ordinal);

        }

        // A tombstone renders nothing, so it costs nothing. The census reads that as a fact about
        // every retirement rather than as a habit of the writer, and skips a lifecycle filter it
        // would otherwise need.
        SqliteException costlyRetire = await AssertRaisesAsync(
            database,
            InsertShapedVersion(
                NewId(),
                entryId,
                laneCode: 1,
                laneRevision: 1,
                operationCode: 2,
                shape: ["NULL", "NULL", "NULL", "NULL"],
                compiledByteCost: 64));

        Assert.Contains("CHECK constraint failed", costlyRetire.Message, StringComparison.Ordinal);

        await database.ExecuteAsync(
            InsertShapedVersion(
                NewId(),
                entryId,
                laneCode: 1,
                laneRevision: 1,
                operationCode: 1,
                shape: [Authored, Compiled, Digest, Digest]),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertShapedVersion(
                NewId(),
                entryId,
                laneCode: 2,
                laneRevision: 1,
                operationCode: 2,
                shape: ["NULL", "NULL", "NULL", "NULL"]),
            CancellationToken.None);

        Assert.Equal(
            2L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_versions;", CancellationToken.None));

    }

    /// <summary>
    /// The canonical ledger is append-only, and its rows leave only through an authorization that
    /// something had to open by name. Both properties are enforced by triggers rather than by the
    /// repository layer, so a stray statement on an otherwise ordinary connection cannot rewrite
    /// evidence a receipt was already issued against.
    /// </summary>
    [Fact]
    public async Task Immutable_rows_reject_update_and_unauthorized_delete()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        string entryId = NewId();

        string versionId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 2, campaignId: NewId(), normalizedKey: "immutable.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionId, entryId, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 32),
            CancellationToken.None);

        await database.ExecuteAsync(InsertProvenance(versionId), CancellationToken.None);

        await database.ExecuteAsync(InsertMutationReceipt(), CancellationToken.None);

        await database.ExecuteAsync(InsertTurnReceipt(), CancellationToken.None);

        foreach (string[] immutable in ImmutableTables)
        {

            SqliteException rejectedUpdate = await AssertRaisesAsync(database, immutable[1]);

            Assert.Contains("append-only", rejectedUpdate.Message, StringComparison.Ordinal);

            SqliteException rejectedDelete = await AssertRaisesAsync(database, $"DELETE FROM {immutable[0]};");

            Assert.Contains("authorized cleanup scope", rejectedDelete.Message, StringComparison.Ordinal);

            Assert.Equal(
                1L,
                await database.ScalarLongAsync(
                    $"SELECT COUNT(*) FROM {immutable[0]};",
                    CancellationToken.None));

        }

        using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            CovenantSqliteAuthorizationKind.OwnerCleanup))
        {

            // Foreign-key safe order: provenance points at the version, the version at the entry.
            await database.ExecuteAsync(
                "DELETE FROM covenant_version_attachment_provenance;",
                CancellationToken.None);

            await database.ExecuteAsync("DELETE FROM covenant_versions;", CancellationToken.None);

            await database.ExecuteAsync("DELETE FROM covenant_entries;", CancellationToken.None);

            await database.ExecuteAsync("DELETE FROM covenant_mutation_receipts;", CancellationToken.None);

            await database.ExecuteAsync("DELETE FROM covenant_turn_receipts;", CancellationToken.None);

        }

        foreach (string[] immutable in ImmutableTables)
        {

            Assert.Equal(
                0L,
                await database.ScalarLongAsync(
                    $"SELECT COUNT(*) FROM {immutable[0]};",
                    CancellationToken.None));

        }

    }

    /// <summary>
    /// The outbox is a work queue of identities, never of text: it names the projection row, the
    /// entry, the lane, and the version the accelerator should hold, and a null version means the
    /// head is absent. Carrying compiled content here would put a second copy of every Covenant
    /// string outside the canonical tables that guard it.
    /// </summary>
    /// <remarks>
    /// A row leaves only when the synchronization worker has applied it, in the same transaction
    /// that advances the applied FTS tuple, or when family maintenance is tearing the dataset down.
    /// Any other delete would drop a delta while the applied tuple still claims the sequence was
    /// published.
    /// </remarks>
    [Fact]
    public async Task Outbox_is_text_free_and_worker_delete_is_narrow()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        List<string> columns = await ReadColumnAsync(
            database,
            "PRAGMA table_info('covenant_search_outbox');",
            ordinal: 1);

        columns.Sort(StringComparer.Ordinal);

        string[] expected =
        [
            "DesiredVersionId",
            "EntryId",
            "LaneCode",
            "Ordinal",
            "SearchRowId",
            "SearchSequence",
        ];

        Assert.Equal(expected, columns);

        foreach (string column in columns)
        {

            Assert.DoesNotContain("Content", column, StringComparison.Ordinal);

        }

        await database.ExecuteAsync(
            $"""
            INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            VALUES
                (1, 0, 1, '{NewId()}', 1, '{NewId()}'),
                (2, 0, 2, '{NewId()}', 1, NULL);
            """,
            CancellationToken.None);

        SqliteException unauthorized = await AssertRaisesAsync(
            database,
            "DELETE FROM covenant_search_outbox WHERE SearchSequence = 1;");

        Assert.Contains("authorized synchronization scope", unauthorized.Message, StringComparison.Ordinal);

        using (CovenantSqliteAuthorizationScope worker = CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            CovenantSqliteAuthorizationKind.AcceleratorSynchronization))
        {

            await database.ExecuteAsync(
                "DELETE FROM covenant_search_outbox WHERE SearchSequence = 1;",
                CancellationToken.None);

        }

        Assert.Equal(
            1L,
            await database.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_search_outbox;",
                CancellationToken.None));

        using (CovenantSqliteAuthorizationScope maintenance = CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))
        {

            await database.ExecuteAsync("DELETE FROM covenant_search_outbox;", CancellationToken.None);

        }

        Assert.Equal(
            0L,
            await database.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_search_outbox;",
                CancellationToken.None));

    }

    /// <summary>
    /// Entry erasure takes an entry and everything that names it in one transaction, so every guard on
    /// that closure admits it beside owner cleanup and family maintenance. Those two keep working
    /// through the same guards, which is what keeps Campaign cleanup, family reset, factory erasure and
    /// the restore purger working.
    /// </summary>
    [Theory]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.OwnerCleanup))]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.CovenantEntryErasure))]
    public async Task Every_entry_closure_delete_guard_admits_cleanup_family_maintenance_and_entry_erasure(
        string kind)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await SeedEntryClosureAsync(database);

        using (CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            Enum.Parse<CovenantSqliteAuthorizationKind>(kind)))
        {

            // Foreign-key safe order: provenance points at the version, the version at the entry, and a
            // curation head at its curation version.
            foreach (string table in EntryClosureTables)
            {

                await database.ExecuteAsync($"DELETE FROM {table};", CancellationToken.None);

            }

        }

        foreach (string table in EntryClosureTables)
        {

            Assert.Equal(
                0L,
                await database.ScalarLongAsync($"SELECT COUNT(*) FROM {table};", CancellationToken.None));

        }

    }

    /// <summary>
    /// The closure's guards open for the three authorizations above and for nothing else. Every other
    /// code a connection can be granted, and no grant at all, still aborts each delete and leaves the
    /// rows in place.
    /// </summary>
    [Theory]
    [MemberData(nameof(AuthorizationsOutsideTheEntryClosure))]
    public async Task No_other_authorization_opens_an_entry_closure_delete_guard(string kind)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await SeedEntryClosureAsync(database);

        using (AuthorizeUnlessNone(database, kind))
        {

            foreach (string table in EntryClosureTables)
            {

                SqliteException refused = await AssertRaisesAsync(database, $"DELETE FROM {table};");

                Assert.Contains(
                    $"{table} delete requires an authorized cleanup scope.",
                    refused.Message,
                    StringComparison.Ordinal);

            }

        }

        foreach (string table in EntryClosureTables)
        {

            Assert.Equal(
                1L,
                await database.ScalarLongAsync($"SELECT COUNT(*) FROM {table};", CancellationToken.None));

        }

    }

    /// <summary>
    /// The outbox keeps its own authorization set: the synchronization worker, family maintenance, and
    /// now entry erasure, which removes the erased entry's pending deltas. Owner cleanup still does not
    /// reach it.
    /// </summary>
    [Theory]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.AcceleratorSynchronization))]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))]
    [InlineData(nameof(CovenantSqliteAuthorizationKind.CovenantEntryErasure))]
    public async Task The_outbox_admits_synchronization_family_maintenance_and_entry_erasure(string kind)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync(InsertOutboxRow(), CancellationToken.None);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            CovenantSqliteAuthorizationKind.OwnerCleanup))
        {

            SqliteException refused = await AssertRaisesAsync(database, "DELETE FROM covenant_search_outbox;");

            Assert.Contains("authorized synchronization scope", refused.Message, StringComparison.Ordinal);

        }

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_search_outbox;", CancellationToken.None));

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            Enum.Parse<CovenantSqliteAuthorizationKind>(kind)))
        {

            await database.ExecuteAsync("DELETE FROM covenant_search_outbox;", CancellationToken.None);

        }

        Assert.Equal(
            0L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_search_outbox;", CancellationToken.None));

    }

    [Theory]
    [MemberData(nameof(AuthorizationsOutsideTheOutbox))]
    public async Task No_other_authorization_opens_the_outbox_delete_guard(string kind)
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync(InsertOutboxRow(), CancellationToken.None);

        using (AuthorizeUnlessNone(database, kind))
        {

            SqliteException refused = await AssertRaisesAsync(database, "DELETE FROM covenant_search_outbox;");

            Assert.Contains("authorized synchronization scope", refused.Message, StringComparison.Ordinal);

        }

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_search_outbox;", CancellationToken.None));

    }

    /// <summary>
    /// The key-epoch row and the curation head had no delete guard before version 6. Both now leave only
    /// through an authorization something had to open by name, like the rest of the closure.
    /// </summary>
    [Fact]
    public async Task Key_epoch_and_curation_head_deletes_require_an_authorized_scope()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync(InsertKeyEpoch("guarded.key", 4), CancellationToken.None);

        await database.ExecuteAsync(InsertCurationSubject("guarded.key", 4), CancellationToken.None);

        SqliteException epoch = await AssertRaisesAsync(database, "DELETE FROM covenant_key_epochs;");

        Assert.Contains(
            "covenant_key_epochs delete requires an authorized cleanup scope.",
            epoch.Message,
            StringComparison.Ordinal);

        SqliteException head = await AssertRaisesAsync(database, "DELETE FROM covenant_curation_heads;");

        Assert.Contains(
            "covenant_curation_heads delete requires an authorized cleanup scope.",
            head.Message,
            StringComparison.Ordinal);

        Assert.Equal(
            1L,
            await database.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_key_epochs WHERE NormalizedKey = 'guarded.key' AND KeyEpoch = 4;",
                CancellationToken.None));

        Assert.Equal(
            1L,
            await database.ScalarLongAsync(
                "SELECT COUNT(*) FROM covenant_curation_heads WHERE NormalizedKey = 'guarded.key' AND IsPinned = 1;",
                CancellationToken.None));

    }

    /// <summary>
    /// Entry erasure is narrower than owner cleanup: turn receipts describe turns, not entries, and the
    /// new code does not reach them.
    /// </summary>
    [Fact]
    public async Task Entry_erasure_does_not_open_turn_receipts()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync(InsertTurnReceipt(), CancellationToken.None);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(
            database.Connection,
            CovenantSqliteAuthorizationKind.CovenantEntryErasure))
        {

            SqliteException refused = await AssertRaisesAsync(database, "DELETE FROM covenant_turn_receipts;");

            Assert.Contains("authorized cleanup scope", refused.Message, StringComparison.Ordinal);

        }

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_turn_receipts;", CancellationToken.None));

    }

    /// <summary>
    /// The entry-erasure function is named by the nine canonical delete guards and by nothing else in
    /// any tier, so the code cannot open a Core guard or a canonical guard outside the entry's closure.
    /// </summary>
    [Fact]
    public void Only_the_nine_canonical_delete_guards_name_the_entry_erasure_function()
    {

        GrimoireSchemaObject[] naming =
        [
            .. GrimoireSchemaCatalog.AllObjects
                .Where(static definition => definition.Sql.Contains(
                    "arcanum_covenant_entry_erasure_authorized",
                    StringComparison.Ordinal)),
        ];

        Assert.All(
            naming,
            static definition => Assert.Equal(GrimoireSchemaTransactionTier.CovenantCanonical, definition.TransactionTier));

        Assert.Equal(
            [
                "covenant_curation_heads_guard_delete",
                "covenant_curation_receipts_guard_delete",
                "covenant_curation_versions_guard_delete",
                "covenant_entries_guard_delete",
                "covenant_key_epochs_guard_delete",
                "covenant_mutation_receipts_guard_delete",
                "covenant_search_outbox_guard_delete",
                "covenant_version_attachment_provenance_guard_delete",
                "covenant_versions_guard_delete",
            ],
            naming.Select(static definition => definition.Name).Order(StringComparer.Ordinal));

    }

    /// <summary>
    /// A fresh dataset starts at canonical sequence zero with a null applied FTS tuple and
    /// <c>FullRebuildRequired</c>, because an accelerator that has never been built is behind by
    /// definition. Reinstalling verifies the singleton and changes nothing: the dataset generation
    /// is the identity every in-flight turn snapshot binds, so reissuing it on a reopen would
    /// invalidate live work.
    /// </summary>
    [Fact]
    public async Task Canonical_initializer_seeds_one_fresh_state_row()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_state;", CancellationToken.None));

        Assert.Equal(
            1L,
            await database.ScalarLongAsync(
                """
                SELECT COUNT(*)
                FROM covenant_state
                WHERE StateKey = 1
                  AND CanonicalSearchSequence = 0
                  AND AppliedDatasetGeneration IS NULL
                  AND AppliedSearchSequence IS NULL
                  AND AcceleratorEpoch = 1
                  AND KeyReclamationEpoch = 1
                  AND EnvelopeKeyEpoch = 1
                  AND NextSearchRowId = 1
                  AND RebuildStateCode = 2
                  AND length(DatasetGeneration) = 16;
                """,
                CancellationToken.None));

        string? generation = await database.ScalarStringAsync(
            "SELECT hex(DatasetGeneration) FROM covenant_state WHERE StateKey = 1;",
            CancellationToken.None);

        Assert.NotNull(generation);

        await database.InstallCanonicalAsync(CancellationToken.None);

        Assert.Equal(
            1L,
            await database.ScalarLongAsync("SELECT COUNT(*) FROM covenant_state;", CancellationToken.None));

        Assert.Equal(
            generation,
            await database.ScalarStringAsync(
                "SELECT hex(DatasetGeneration) FROM covenant_state WHERE StateKey = 1;",
                CancellationToken.None));

    }

    /// <summary>
    /// Global effect validation compares a recorded epoch for a normalized key instead of rescanning
    /// every Campaign head that shares it, so the epoch has to move on every change to what that key
    /// resolves to - appearing, advancing, and disappearing alike. It is only useful because it is
    /// monotonic: an epoch that moved backward would let a stale recording compare equal to a key
    /// that has since changed.
    /// </summary>
    [Fact]
    public async Task Key_epoch_advances_on_head_insert_update_and_delete()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await AllocateSearchRowIdsAsync(database);

        string campaignId = NewId();

        string entryId = NewId();

        string versionId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 2, campaignId: campaignId, normalizedKey: "epoch.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionId, entryId, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 16),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertHead(
                entryId,
                laneCode: 1,
                versionId: versionId,
                laneRevision: 1,
                operationCode: 1,
                scopeCode: 2,
                campaignId: campaignId,
                normalizedKey: "epoch.key",
                compiledByteCost: 16,
                originCode: 1,
                searchRowId: 1),
            CancellationToken.None);

        Assert.Equal(1L, await ReadKeyEpochAsync(database, "epoch.key"));

        // A key row created by a head starts its binding epoch at zero, the value a key with no row
        // reads as, so curation recorded before the key existed stays bound to it.
        Assert.Equal(0L, await ReadIncarnationEpochAsync(database, "epoch.key"));

        await database.ExecuteAsync(
            $"UPDATE covenant_heads SET UpdatedAtUtc = '{LaterTimestamp}' WHERE EntryId = '{entryId}' AND LaneCode = 1;",
            CancellationToken.None);

        Assert.Equal(2L, await ReadKeyEpochAsync(database, "epoch.key"));

        Assert.Equal(0L, await ReadIncarnationEpochAsync(database, "epoch.key"));

        await database.ExecuteAsync(
            $"DELETE FROM covenant_heads WHERE EntryId = '{entryId}' AND LaneCode = 1;",
            CancellationToken.None);

        Assert.Equal(3L, await ReadKeyEpochAsync(database, "epoch.key"));

        Assert.Equal(0L, await ReadIncarnationEpochAsync(database, "epoch.key"));

        SqliteException rewound = await AssertRaisesAsync(
            database,
            "UPDATE covenant_key_epochs SET KeyEpoch = 1 WHERE NormalizedKey = 'epoch.key';");

        Assert.Contains("can only advance", rewound.Message, StringComparison.Ordinal);

        Assert.Equal(3L, await ReadKeyEpochAsync(database, "epoch.key"));

        // The dependency epoch advances in the same statement, so only the binding guard can refuse it.
        SqliteException rebound = await AssertRaisesAsync(
            database,
            "UPDATE covenant_key_epochs SET KeyEpoch = KeyEpoch + 1, IncarnationEpoch = 7 WHERE NormalizedKey = 'epoch.key';");

        Assert.Contains(
            "A covenant key binding epoch is fixed when its epoch row is created.",
            rebound.Message,
            StringComparison.Ordinal);

        Assert.Equal(3L, await ReadKeyEpochAsync(database, "epoch.key"));

        Assert.Equal(0L, await ReadIncarnationEpochAsync(database, "epoch.key"));

    }

    private static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    private static string Quote(string? value) => value is null ? "NULL" : $"'{value}'";

    /// <summary>
    /// A head's projection row ID must already have been allocated, which means
    /// <c>covenant_state.NextSearchRowId</c> has to have moved past it. A freshly seeded state row
    /// sits at one, so no head at all can be written until the counter advances.
    /// </summary>
    private static async Task AllocateSearchRowIdsAsync(CovenantSchemaScratchDatabase database) =>
        await database.ExecuteAsync(
            "UPDATE covenant_state SET NextSearchRowId = 1000 WHERE StateKey = 1;",
            CancellationToken.None);

    private static async Task<long> ReadKeyEpochAsync(CovenantSchemaScratchDatabase database, string normalizedKey) =>
        await database.ScalarLongAsync(
            $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{normalizedKey}';",
            CancellationToken.None);

    private static async Task<long> ReadIncarnationEpochAsync(
        CovenantSchemaScratchDatabase database,
        string normalizedKey) =>
        await database.ScalarLongAsync(
            $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{normalizedKey}';",
            CancellationToken.None);

    /// <summary>
    /// Writes one row into every table of an entry's erasure closure: the entry, its version and
    /// provenance leaf, a mutation receipt, a pinned curation subject, and the key's epoch row.
    /// </summary>
    private static async Task SeedEntryClosureAsync(CovenantSchemaScratchDatabase database)
    {

        string entryId = NewId();

        string versionId = NewId();

        await database.ExecuteAsync(
            InsertEntry(entryId, scopeCode: 2, campaignId: NewId(), normalizedKey: "erased.key"),
            CancellationToken.None);

        await database.ExecuteAsync(
            InsertVersion(versionId, entryId, laneCode: 1, laneRevision: 1, operationCode: 1, compiledByteCost: 32),
            CancellationToken.None);

        await database.ExecuteAsync(InsertProvenance(versionId), CancellationToken.None);

        await database.ExecuteAsync(InsertMutationReceipt(), CancellationToken.None);

        await database.ExecuteAsync(InsertKeyEpoch("erased.key", 2), CancellationToken.None);

        await database.ExecuteAsync(InsertCurationSubject("erased.key", 2), CancellationToken.None);

    }

    /// <summary>
    /// Every authorization kind, and no grant at all, except the ones named. The set is read from the
    /// enum, so a kind added later is refused here until a guard is deliberately widened to admit it.
    /// </summary>
    private static TheoryData<string> AuthorizationsExcept(params CovenantSqliteAuthorizationKind[] admitted)
    {

        TheoryData<string> data = new() { NoAuthorization };

        foreach (CovenantSqliteAuthorizationKind kind in Enum.GetValues<CovenantSqliteAuthorizationKind>())
        {

            if (!admitted.Contains(kind))
            {

                data.Add(kind.ToString());

            }

        }

        return data;

    }

    /// <summary>
    /// Grants the named kind through the core both entry points end at.
    /// </summary>
    /// <remarks>
    /// The general entry point refuses restore-staging sanitization, but the sealed restore-staging
    /// capability grants it through this same core on a staged Grimoire that carries the canonical tier.
    /// Going through the core is what lets the refusal theories cover that kind too.
    /// </remarks>
    private static CovenantSqliteAuthorizationScope? AuthorizeUnlessNone(
        CovenantSchemaScratchDatabase database,
        string kind) =>
        kind == NoAuthorization
            ? null
            : CovenantSqliteConnectionInitializer.Instance.AuthorizeCore(
                database.Connection,
                Enum.Parse<CovenantSqliteAuthorizationKind>(kind));

    private static string InsertKeyEpoch(string normalizedKey, long keyEpoch) =>
        $"""
        INSERT INTO covenant_key_epochs (NormalizedKey, KeyEpoch, UpdatedAtUtc)
        VALUES ({Quote(normalizedKey)}, {keyEpoch}, {Quote(Timestamp)});
        """;

    /// <summary>
    /// One pinned Global Confirmed curation subject at <paramref name="keyEpoch"/>: its first curation
    /// version, the head that points at it, and the Applied receipt that recorded it.
    /// </summary>
    private static string InsertCurationSubject(string normalizedKey, long keyEpoch)
    {

        string versionId = NewId();

        string mutationId = NewId();

        return $"""
        INSERT INTO covenant_curation_versions (
            CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, CurationKindCode,
            Revision, PredecessorVersionId, MutationId, RequestIdempotencyDigest, AuthorizationDigest,
            FinalMutationDigest, CreatedAtUtc)
        VALUES (
            {Quote(versionId)}, 1, NULL, {Quote(normalizedKey)}, 1, {keyEpoch}, 1,
            1, NULL, {Quote(mutationId)}, randomblob(32), randomblob(32),
            randomblob(32), {Quote(Timestamp)});

        INSERT INTO covenant_curation_heads (
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, IsPinned, IsMasked, CurrentVersionId,
            CurrentRevision, UpdatedAtUtc)
        VALUES (1, NULL, {Quote(normalizedKey)}, 1, {keyEpoch}, 1, 0, {Quote(versionId)}, 1, {Quote(Timestamp)});

        INSERT INTO covenant_curation_receipts (
            MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, CurationKindCode,
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, OutcomeCode, ResultingVersionId,
            ResultingRevision, ResponseReceiptDigest, CommittedAtUtc)
        VALUES (
            {Quote(mutationId)}, randomblob(32), randomblob(32), randomblob(32), 1,
            1, NULL, {Quote(normalizedKey)}, 1, {keyEpoch}, 1, {Quote(versionId)},
            1, randomblob(32), {Quote(Timestamp)});
        """;

    }

    private static string InsertOutboxRow() =>
        $"""
        INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
        VALUES (1, 0, 1, {Quote(NewId())}, 1, {Quote(NewId())});
        """;

    private static async Task<SqliteException> AssertRaisesAsync(
        CovenantSchemaScratchDatabase database,
        string sql) =>
        await Assert.ThrowsAsync<SqliteException>(() => database.ExecuteAsync(sql, CancellationToken.None));

    private static async Task<List<string>> ReadColumnAsync(
        CovenantSchemaScratchDatabase database,
        string sql,
        int ordinal)
    {

        List<string> values = [];

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText = sql;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            values.Add(reader.GetString(ordinal));

        }

        return values;

    }

    private static string InsertEntry(string entryId, long scopeCode, string? campaignId, string normalizedKey) =>
        $"""
        INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
        VALUES ({Quote(entryId)}, {scopeCode}, {Quote(campaignId)}, {Quote(normalizedKey)}, {Quote(normalizedKey)}, {Quote(Timestamp)});
        """;

    /// <summary>
    /// An Operator-origin version at the head of its lane: no source turn, no tool call, no Ward
    /// evidence, and therefore no predecessor either.
    /// </summary>
    private static string InsertVersion(
        string versionId,
        string entryId,
        long laneCode,
        long laneRevision,
        long operationCode,
        long compiledByteCost)
    {

        string[] shape = operationCode == 1
            ? ["'authored covenant text'", "'compiled covenant text'", "randomblob(32)", "randomblob(32)"]
            : ["NULL", "NULL", "NULL", "NULL"];

        return InsertShapedVersion(versionId, entryId, laneCode, laneRevision, operationCode, shape, compiledByteCost);

    }

    /// <summary>
    /// The same insert with the four content columns supplied as raw SQL fragments, so a test can
    /// write a shape the table is supposed to refuse.
    /// </summary>
    private static string InsertShapedVersion(
        string versionId,
        string entryId,
        long laneCode,
        long laneRevision,
        long operationCode,
        string[] shape,
        long compiledByteCost = 0,
        long? origin = null,
        bool useDefaultWardFields = true,
        string? wardReceiptDigest = null,
        string? authorizationModeCode = null)
    {

        // The lane decides the origin unless a test is deliberately writing a disagreeing pair, and
        // an agent origin has to name the turn, the tool call, and the plan it was proposed against,
        // so those columns follow the origin rather than the lane.
        long originCode = origin ?? (laneCode == 2 ? 2 : 1);

        bool agentAuthored = originCode != 1;

        string sourceTurnId = agentAuthored ? Quote(NewId()) : "NULL";

        string sourceToolCallId = agentAuthored ? Quote(NewId()) : "NULL";

        string basePlanDigest = agentAuthored ? "randomblob(32)" : "NULL";

        string resolvedWardReceiptDigest = useDefaultWardFields
            ? originCode == 3 ? "randomblob(32)" : "NULL"
            : wardReceiptDigest ?? "NULL";

        string resolvedAuthorizationModeCode = useDefaultWardFields
            ? originCode == 3 ? "2" : "NULL"
            : authorizationModeCode ?? "NULL";

        return $"""
        INSERT INTO covenant_versions (
            VersionId,
            EntryId,
            LaneCode,
            LaneRevision,
            OperationCode,
            AuthoredContent,
            CompiledContent,
            AuthoredHash,
            RenderedHash,
            CompiledByteCost,
            RequiredFenceLength,
            CompilerPolicyVersion,
            RendererPolicyVersion,
            OriginCode,
            SourceTurnId,
            SourceToolCallId,
            BasePlanDigest,
            AdmissionReceiptDigest,
            WardReceiptDigest,
            AuthorizationModeCode,
            MutationId,
            RequestIdempotencyDigest,
            AuthorizationDigest,
            FinalMutationDigest,
            PredecessorVersionId,
            AttachmentProvenanceCount,
            AttachmentProvenanceDigest,
            CreatedAtUtc)
        VALUES (
            {Quote(versionId)},
            {Quote(entryId)},
            {laneCode},
            {laneRevision},
            {operationCode},
            {shape[0]},
            {shape[1]},
            {shape[2]},
            {shape[3]},
            {compiledByteCost},
            3,
            1,
            1,
            {originCode},
            {sourceTurnId},
            {sourceToolCallId},
            {basePlanDigest},
            NULL,
            {resolvedWardReceiptDigest},
            {resolvedAuthorizationModeCode},
            {Quote(NewId())},
            randomblob(32),
            randomblob(32),
            randomblob(32),
            NULL,
            0,
            randomblob(32),
            {Quote(Timestamp)});
        """;

    }

    private static string InsertHead(
        string entryId,
        long laneCode,
        string versionId,
        long laneRevision,
        long operationCode,
        long scopeCode,
        string? campaignId,
        string normalizedKey,
        long compiledByteCost,
        long originCode,
        long searchRowId) =>
        $"""
        INSERT INTO covenant_heads (
            EntryId,
            LaneCode,
            CurrentVersionId,
            CurrentLaneRevision,
            CurrentOperationCode,
            ScopeCode,
            CampaignId,
            NormalizedKey,
            CompiledByteCost,
            OriginCode,
            SearchRowId,
            UpdatedAtUtc)
        VALUES (
            {Quote(entryId)},
            {laneCode},
            {Quote(versionId)},
            {laneRevision},
            {operationCode},
            {scopeCode},
            {Quote(campaignId)},
            {Quote(normalizedKey)},
            {compiledByteCost},
            {originCode},
            {searchRowId},
            {Quote(Timestamp)});
        """;

    private static string InsertProvenance(string versionId) =>
        $"""
        INSERT INTO covenant_version_attachment_provenance (
            VersionId,
            Ordinal,
            AttachmentId,
            AttachmentVersionIdentity,
            LogicalKey,
            ContentHash,
            SourceRangeKindCode,
            SourceStart,
            SourceEnd,
            SourceTurnId,
            MaterializationReference)
        VALUES (
            {Quote(versionId)},
            0,
            {Quote(NewId())},
            {Quote(NewId())},
            'source.md',
            randomblob(32),
            1,
            NULL,
            NULL,
            NULL,
            NULL);
        """;

    /// <summary>
    /// A NoChange receipt, which is the shape that carries neither a resulting version nor a
    /// revision and therefore needs nothing else in the tier to exist first.
    /// </summary>
    private static string InsertMutationReceipt() =>
        $"""
        INSERT INTO covenant_mutation_receipts (
            MutationId,
            RequestIdempotencyDigest,
            AuthorizationDigest,
            FinalMutationDigest,
            MutationKindCode,
            ScopeCode,
            CampaignId,
            TargetIdentityDigest,
            LaneCode,
            OutcomeCode,
            ResultingVersionId,
            ResultingLaneRevision,
            ResponseReceiptDigest,
            SourceTurnId,
            CommittedAtUtc)
        VALUES (
            {Quote(NewId())},
            randomblob(32),
            randomblob(32),
            randomblob(32),
            1,
            2,
            {Quote(NewId())},
            randomblob(32),
            1,
            2,
            NULL,
            NULL,
            randomblob(32),
            NULL,
            {Quote(Timestamp)});
        """;

    private static string InsertTurnReceipt() =>
        $"""
        INSERT INTO covenant_turn_receipts (
            AssistantEntryId,
            SessionId,
            CampaignId,
            DatasetGeneration,
            PlanDigest,
            AttemptedAdmissionCount,
            AttemptChainHead,
            CommittedBranchDigest,
            LineageHeadDigest,
            ExternalDisclosureCount,
            DisclosureChainHead,
            ConfirmedTokenCost,
            ProposedTokenCost,
            MutationCount,
            FinalOutcomeCode,
            CreatedAtUtc)
        VALUES (
            {Quote(NewId())},
            {Quote(NewId())},
            {Quote(NewId())},
            randomblob(16),
            randomblob(32),
            randomblob(8),
            randomblob(32),
            randomblob(32),
            randomblob(32),
            randomblob(8),
            randomblob(32),
            0,
            0,
            0,
            1,
            {Quote(Timestamp)});
        """;

}
