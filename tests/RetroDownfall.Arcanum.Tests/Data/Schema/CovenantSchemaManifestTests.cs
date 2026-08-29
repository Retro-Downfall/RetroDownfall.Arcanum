using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The closed per-tier manifests and the inspection that compares one against a live catalog.
/// </summary>
/// <remarks>
/// Every inspection here runs against a real installed database rather than a stubbed catalog. The
/// property under test is not "the manifest lists the right names" but "an installed tier either
/// matches its declaration or fails closed with a content-free reason", and only a real
/// <c>sqlite_master</c> and real <c>PRAGMA</c> output can distinguish the two.
///
/// <para>The three tiers share one database, so each test that installs more than one tier also
/// proves the tiers stay out of each other's way: an object another tier legitimately installed must
/// never read as drift.</para>
/// </remarks>
public sealed class CovenantSchemaManifestTests
{

    /// <summary>
    /// The provider has to be installed before the first SQLCipher connection is constructed. The
    /// fixture does this too, but repeating it here keeps a run filtered to this class from
    /// depending on which type happened to be touched first.
    /// </summary>
    static CovenantSchemaManifestTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// The four shadow tables FTS5 materializes behind <c>covenant_fts</c>, in the ordinal order the
    /// synthetic manifest pins them in.
    /// </summary>
    private static readonly string[] ShadowTableNames =
    [
        "covenant_fts_config",
        "covenant_fts_data",
        "covenant_fts_docsize",
        "covenant_fts_idx",
    ];

    /// <summary>
    /// Fragments whose presence in a diagnostic code would mean it had absorbed SQL text, a quoted
    /// value, or free prose rather than staying a stable identifier.
    /// </summary>
    private static readonly string[] ContentMarkers =
    [
        " ",
        "'",
        "\"",
        "`",
        "(",
        ")",
        ";",
        "SELECT",
        "CREATE",
        "PRAGMA",
    ];

    /// <summary>
    /// Implicit indexes are never named in a manifest. Their names encode constraint declaration
    /// order, so pinning them would report drift for a harmless reordering; their shape is pinned
    /// instead, through the owning table. The fingerprint still has to move when that shape changes,
    /// which is why the same installed catalog must fingerprint identically twice and only twice.
    /// </summary>
    [Fact]
    public async Task Manifest_excludes_sqlite_autoindexes_but_validates_primary_and_unique_shapes()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        GrimoireSchemaInspectionResult first = await InspectCanonicalAsync(database);

        Assert.True(first.IsValid, Describe(first));

        Assert.Null(first.Failure);

        Assert.NotNull(first.InstalledCatalogFingerprint);

        GrimoireSchemaInspectionResult second = await InspectCanonicalAsync(database);

        Assert.True(second.IsValid, Describe(second));

        Assert.Equal(first.InstalledCatalogFingerprint, second.InstalledCatalogFingerprint);

        // The exclusion below only means something if the installed tier actually has autoindexes to
        // exclude. covenant_entries has both a primary key and two unique indexes, so it does.
        List<string> installedIndexNames = await ReadColumnAsync(
            database,
            "PRAGMA index_list('covenant_entries');",
            ordinal: 1);

        Assert.Contains(
            installedIndexNames,
            static name => name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal));

        foreach (GrimoireSchemaManifestEntry entry in GrimoireSchemaManifests.CovenantCanonical.Entries)
        {

            Assert.False(
                entry.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal),
                $"manifest entry {entry.Name} names a generated autoindex");

            foreach (GrimoireExpectedIndex index in entry.Indexes)
            {

                Assert.False(
                    index.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal),
                    $"manifest index {index.Name} on {entry.Name} names a generated autoindex");

            }

        }

    }

    /// <summary>
    /// An index nobody declared is drift even on a table the tier owns. An unowned index changes the
    /// plans the tier's own statements get without changing any object the manifest compares, so it
    /// is the one kind of addition that could otherwise pass unnoticed.
    /// </summary>
    [Fact]
    public async Task Manifest_rejects_unexpected_user_Covenant_index()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        GrimoireSchemaInspectionResult installed = await InspectCanonicalAsync(database);

        Assert.True(installed.IsValid, Describe(installed));

        await database.ExecuteAsync(
            "CREATE INDEX idx_covenant_entries_rogue ON covenant_entries(AuthoredKey);",
            CancellationToken.None);

        GrimoireSchemaInspectionResult drifted = await InspectCanonicalAsync(database);

        Assert.False(drifted.IsValid);

        Assert.Equal(GrimoireSchemaInspectionFailure.UnexpectedObject, drifted.Failure);

        Assert.Equal("idx_covenant_entries_rogue", drifted.ObjectName);

        // A failed inspection carries no fingerprint at all, so a caller cannot persist one for a
        // catalog that did not validate.
        Assert.Null(drifted.InstalledCatalogFingerprint);

    }

    /// <summary>
    /// A Covenant-prefixed object nobody declared is the dangerous case: it looks like ours and would
    /// be trusted by name alone by anything that recognized objects by prefix.
    /// </summary>
    [Fact]
    public async Task Manifest_rejects_unknown_Covenant_object()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync("CREATE TABLE covenant_rogue(Id TEXT);", CancellationToken.None);

        GrimoireSchemaInspectionResult drifted = await InspectCanonicalAsync(database);

        Assert.False(drifted.IsValid);

        Assert.Equal(GrimoireSchemaInspectionFailure.UnexpectedObject, drifted.Failure);

        Assert.Equal("covenant_rogue", drifted.ObjectName);

    }

    /// <summary>
    /// The property that lets three tiers share one database: each is inspected on its own, and
    /// neither reports the other's legitimately installed objects as drift.
    /// </summary>
    /// <remarks>
    /// Without the ownership registry an inspection would have to choose between reporting every
    /// other tier's objects as unexpected and ignoring anything it did not recognize. The first makes
    /// an optional tier impossible; the second makes drift detection meaningless.
    /// </remarks>
    [Fact]
    public async Task Manifest_accepts_objects_owned_by_other_known_tiers()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.InstallAcceleratorAsync(CancellationToken.None);

        GrimoireSchemaInspectionResult canonical = await InspectCanonicalAsync(database);

        Assert.True(canonical.IsValid, Describe(canonical));

        GrimoireSchemaInspectionResult accelerator = await InspectAcceleratorAsync(database);

        Assert.True(accelerator.IsValid, Describe(accelerator));

        // Two tiers over one catalog must still be two identities, or the metadata row for one could
        // be satisfied by the other's installation.
        Assert.NotEqual(canonical.InstalledCatalogFingerprint, accelerator.InstalledCatalogFingerprint);

    }

    /// <summary>
    /// The two ways an owned object stops being the one this binary declares: it is gone, or its
    /// definition changed. Both fail closed and name the object, and neither yields a fingerprint.
    /// </summary>
    [Fact]
    public async Task Manifest_rejects_missing_or_changed_object()
    {

        await using (CovenantSchemaScratchDatabase missing =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None))
        {

            await missing.InstallCanonicalAsync(CancellationToken.None);

            await missing.ExecuteAsync("DROP TABLE covenant_key_epochs;", CancellationToken.None);

            GrimoireSchemaInspectionResult dropped = await InspectCanonicalAsync(missing);

            Assert.False(dropped.IsValid);

            Assert.Equal(GrimoireSchemaInspectionFailure.MissingObject, dropped.Failure);

            Assert.Equal("covenant_key_epochs", dropped.ObjectName);

            Assert.Null(dropped.InstalledCatalogFingerprint);

        }

        await using CovenantSchemaScratchDatabase changed =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await changed.InstallCanonicalAsync(CancellationToken.None);

        string drifted = ReadCanonicalSql("covenant_turn_receipt_aggregate")
            .Replace(
                "SessionId TEXT NOT NULL PRIMARY KEY,",
                "SessionId TEXT NOT NULL PRIMARY KEY,\n    DriftColumn TEXT NULL,",
                StringComparison.Ordinal);

        // Guards against the recreation below silently installing the unmodified definition, which
        // would leave the assertion passing for no reason at all.
        Assert.Contains("DriftColumn", drifted, StringComparison.Ordinal);

        await changed.ExecuteAsync("DROP TABLE covenant_turn_receipt_aggregate;", CancellationToken.None);

        await changed.ExecuteAsync(drifted, CancellationToken.None);

        // Dropping the table dropped the trigger that guards it. Reinstalling the trigger keeps the
        // only difference the extra column, so DefinitionDrift cannot be a missing trigger reported
        // under another name.
        await changed.ExecuteAsync(
            ReadCanonicalSql("covenant_turn_receipt_aggregate_validate_update"),
            CancellationToken.None);

        GrimoireSchemaInspectionResult widened = await InspectCanonicalAsync(changed);

        Assert.False(widened.IsValid);

        Assert.Equal(GrimoireSchemaInspectionFailure.DefinitionDrift, widened.Failure);

        Assert.Equal("covenant_turn_receipt_aggregate", widened.ObjectName);

        Assert.Null(widened.InstalledCatalogFingerprint);

    }

    /// <summary>
    /// An explicit index is matched by name and then compared by shape, so an index recreated under
    /// its own name over different columns is drift rather than an unexpected object. That is the
    /// case a name-only check would accept: the tier's statements would silently get plans the
    /// declared index was never chosen to produce.
    /// </summary>
    [Fact]
    public async Task Manifest_rejects_changed_index_columns_order_uniqueness_or_predicate()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        // Each shape SQLite reports separately, recreated under the declared name so it is an
        // expected index whose shape changed rather than an unexpected object.
        string[] driftedShapes =
        [
            "CREATE INDEX idx_covenant_entries_campaign ON covenant_entries(AuthoredKey);",
            "CREATE INDEX idx_covenant_entries_campaign ON covenant_entries(NormalizedKey, CampaignId);",
            "CREATE UNIQUE INDEX idx_covenant_entries_campaign ON covenant_entries(CampaignId, NormalizedKey);",
            """
            CREATE INDEX idx_covenant_entries_campaign
                ON covenant_entries(CampaignId, NormalizedKey) WHERE CampaignId IS NOT NULL;
            """,
        ];

        foreach (string shape in driftedShapes)
        {

            await ReplaceEntriesCampaignIndexAsync(database, shape);

            GrimoireSchemaInspectionResult drifted = await InspectCanonicalAsync(database);

            Assert.False(drifted.IsValid, shape);

            Assert.Equal(GrimoireSchemaInspectionFailure.IndexShapeDrift, drifted.Failure);

            Assert.Equal("idx_covenant_entries_campaign", drifted.ObjectName);

        }

        // Restoring the declared shape clears the signal, so the rejections above were the shapes
        // rather than a check that had latched on.
        await ReplaceEntriesCampaignIndexAsync(
            database,
            "CREATE INDEX idx_covenant_entries_campaign ON covenant_entries(CampaignId, NormalizedKey);");

        GrimoireSchemaInspectionResult restored = await InspectCanonicalAsync(database);

        Assert.True(restored.IsValid, Describe(restored));

    }

    /// <summary>
    /// A partial index that stays partial while its predicate changes is drift. This is the case the
    /// shape checks structurally cannot see: <c>PRAGMA index_list</c> reports <c>partial</c> as a bare
    /// 0/1 flag and <c>PRAGMA index_xinfo</c> has no predicate column, so name, uniqueness, origin,
    /// partial-ness, and key columns all still agree while the index enforces a different rule.
    /// </summary>
    /// <remarks>
    /// <c>ux_covenant_entries_global_key</c> is half of what keeps Global and Campaign keys in
    /// separate namespaces. Narrowing its predicate the way a hand-edited repair would leaves the
    /// uniqueness rule quietly unenforced for rows the declared index covers.
    /// </remarks>
    [Fact]
    public async Task Manifest_rejects_a_partial_index_whose_predicate_changed_while_staying_partial()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.ExecuteAsync("DROP INDEX ux_covenant_entries_global_key;", CancellationToken.None);

        await database.ExecuteAsync(
            """
            CREATE UNIQUE INDEX ux_covenant_entries_global_key
                ON covenant_entries(NormalizedKey) WHERE CampaignId IS NULL AND ScopeCode = 1;
            """,
            CancellationToken.None);

        GrimoireSchemaInspectionResult drifted = await InspectCanonicalAsync(database);

        Assert.False(drifted.IsValid, Describe(drifted));

        Assert.Equal(GrimoireSchemaInspectionFailure.IndexShapeDrift, drifted.Failure);

        Assert.Equal("ux_covenant_entries_global_key", drifted.ObjectName);

        Assert.Null(drifted.InstalledCatalogFingerprint);

        await database.ExecuteAsync("DROP INDEX ux_covenant_entries_global_key;", CancellationToken.None);

        // Restoring the declared predicate clears the signal, so the rejection above was the
        // predicate rather than a check that had latched on.
        await database.ExecuteAsync(
            """
            CREATE UNIQUE INDEX ux_covenant_entries_global_key
                ON covenant_entries(NormalizedKey) WHERE CampaignId IS NULL;
            """,
            CancellationToken.None);

        GrimoireSchemaInspectionResult restored = await InspectCanonicalAsync(database);

        Assert.True(restored.IsValid, Describe(restored));

    }

    /// <summary>
    /// The shadow tables have no source resource file, so nothing but this manifest claims them. All
    /// four are owned, because a missing or altered shadow means search would answer from a structure
    /// this build did not create.
    /// </summary>
    [Fact]
    public void Accelerator_manifest_owns_all_four_FTS_shadow_tables()
    {

        Assert.Equal(
            ShadowTableNames,
            CovenantAcceleratorSyntheticManifest.Entries.Select(static entry => entry.Name).Order(StringComparer.Ordinal));

        foreach (GrimoireSchemaManifestEntry entry in CovenantAcceleratorSyntheticManifest.Entries)
        {

            Assert.True(entry.IsSynthetic, $"{entry.Name} is not marked synthetic");

        }

        // The accelerator tier's own manifest has to carry them, or an inspection of that tier would
        // report four unexpected objects on a perfectly healthy installation.
        foreach (string name in ShadowTableNames)
        {

            Assert.Contains(
                GrimoireSchemaManifests.CovenantAccelerator.Entries,
                entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

        }

    }

    /// <summary>
    /// The pinned shadow definitions are literals rather than a runtime capture on purpose: reading
    /// them from the database under inspection would make the check tautological. That only works
    /// while the literals describe the accepted runtime, so this test compares them against what the
    /// hermetic SQLCipher build actually creates. A failure here is a native dependency change to
    /// review, not a test to relax.
    /// </summary>
    [Fact]
    public async Task Synthetic_shadow_definitions_match_the_pinned_runtime()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        await database.InstallAcceleratorAsync(CancellationToken.None);

        int compared = 0;

        foreach (GrimoireSchemaManifestEntry entry in CovenantAcceleratorSyntheticManifest.Entries)
        {

            // The name comes from the pinned manifest, never from a row read out of the database.
            string? installed = await database.ScalarStringAsync(
                $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{entry.Name}';",
                CancellationToken.None);

            Assert.NotNull(installed);

            Assert.Equal(entry.NormalizedSql, GrimoireSqlNormalizer.Normalize(installed));

            compared++;

        }

        Assert.Equal(ShadowTableNames.Length, compared);

    }

    /// <summary>
    /// Drift detection compares meaning, not formatting: SQLite stores what was typed, so a
    /// reindented source file would otherwise read as a changed schema. What the normalizer must not
    /// do is reach inside a quoted string, because <c>RAISE(ABORT, 'two  spaces')</c> is a different
    /// message from the one-space version and a normalizer that collapsed it would call a changed
    /// abort message identical.
    /// </summary>
    [Fact]
    public void Installed_fingerprint_is_stable_across_whitespace_only_sql_normalization()
    {

        const string Expected = "CREATE TABLE sample ( Id TEXT NOT NULL )";

        string spread = GrimoireSqlNormalizer.Normalize(
            "CREATE   TABLE   IF NOT EXISTS   sample (\r\n    Id   TEXT   NOT   NULL\r\n);\r\n");

        string tight = GrimoireSqlNormalizer.Normalize("CREATE TABLE sample (\n Id TEXT NOT NULL\n)");

        Assert.Equal(Expected, spread);

        Assert.Equal(Expected, tight);

        Assert.Equal(spread, tight);

        Assert.DoesNotContain("IF NOT EXISTS", spread, StringComparison.Ordinal);

        Assert.DoesNotContain("\r", spread, StringComparison.Ordinal);

        // One terminal semicolon: a source file that omitted it must not read as different from one
        // that included it, because SQLite stores neither.
        Assert.Equal("SELECT 1", GrimoireSqlNormalizer.Normalize("SELECT 1;"));

        Assert.Equal("SELECT 1", GrimoireSqlNormalizer.Normalize("SELECT 1"));

        // A comment is removed rather than collapsed. Collapsing the newline that ends a line comment
        // would swallow the rest of the statement into it.
        Assert.Equal("SELECT 1", GrimoireSqlNormalizer.Normalize("SELECT 1 -- a note about why\n"));

        Assert.Equal(
            Expected,
            GrimoireSqlNormalizer.Normalize("CREATE TABLE sample (\n -- why this column exists\n Id TEXT NOT NULL\n)"));

        string doubled = GrimoireSqlNormalizer.Normalize("SELECT RAISE(ABORT, 'two  spaces')");

        string single = GrimoireSqlNormalizer.Normalize("SELECT RAISE(ABORT, 'two spaces')");

        Assert.Contains("'two  spaces'", doubled, StringComparison.Ordinal);

        Assert.NotEqual(single, doubled);

    }

    /// <summary>
    /// <c>GrimoireSchemaIdentity</c> answers a different question from the manifest inspector: it
    /// hashes the whole physical catalog for backup verification, so a snapshot can be proved to be
    /// the one that was captured. Narrowing it to the manifest would make it blind to every object no
    /// tier owns, and a restored archive missing them would verify as intact.
    /// </summary>
    [Fact]
    public async Task Whole_database_backup_identity_still_includes_every_physical_object()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await database.InstallCanonicalAsync(CancellationToken.None);

        string before = await GrimoireSchemaIdentity.ComputeAsync(database.Connection, CancellationToken.None);

        GrimoireSchemaInspectionResult tierBefore = await InspectCanonicalAsync(database);

        Assert.True(tierBefore.IsValid, Describe(tierBefore));

        await database.ExecuteAsync(
            "CREATE TABLE unrelated_backup_witness (Id TEXT NOT NULL PRIMARY KEY);",
            CancellationToken.None);

        string after = await GrimoireSchemaIdentity.ComputeAsync(database.Connection, CancellationToken.None);

        Assert.NotEqual(before, after);

        // The same object no tier manifest owns leaves the tier identity alone, which is what makes
        // the two values answer different questions rather than duplicate one.
        GrimoireSchemaInspectionResult tierAfter = await InspectCanonicalAsync(database);

        Assert.True(tierAfter.IsValid, Describe(tierAfter));

        Assert.Equal(tierBefore.InstalledCatalogFingerprint, tierAfter.InstalledCatalogFingerprint);

    }

    /// <summary>
    /// A name claimed by two tiers would make ownership depend on which tier happened to be inspected
    /// first, which is exactly the ambiguity the registry exists to remove.
    /// </summary>
    [Fact]
    public void Ownership_registry_rejects_an_object_claimed_by_two_tiers()
    {

        GrimoireSchemaManifest core = BuildSingleObjectManifest(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            "contested_object");

        GrimoireSchemaManifest canonical = BuildSingleObjectManifest(
            GrimoireSchemaFamily.Covenant,
            GrimoireSchemaTransactionTier.CovenantCanonical,
            "contested_object");

        InvalidOperationException rejected = Assert.Throws<InvalidOperationException>(
            () => ConstructRegistry([core, canonical]));

        Assert.Contains("contested_object", rejected.Message, StringComparison.Ordinal);

        // Naming both claimants is what makes the failure actionable; naming one would leave an
        // operator hunting for the other.
        Assert.Contains("Core", rejected.Message, StringComparison.Ordinal);

        Assert.Contains("CovenantCanonical", rejected.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// Diagnostic codes reach logs and API responses, so they are a closed vocabulary that carries no
    /// content. A code that embedded SQL, a path, or a value would leak Covenant text through the
    /// very channel that exists to report that something is wrong with it.
    /// </summary>
    [Fact]
    public void Diagnostic_codes_are_closed_and_content_free()
    {

        string[] expectedCodes =
        [
            "Grimoire.Schema.CatalogReadFailed",
            "Grimoire.Schema.DefinitionDrift",
            "Grimoire.Schema.IndexShapeDrift",
            "Grimoire.Schema.MissingObject",
            "Grimoire.Schema.ShadowObjectDrift",
            "Grimoire.Schema.UnexpectedObject",
        ];

        // Deliberately an object name that would be conspicuous if it ever reached the code.
        const string OffendingObject = "covenant_entries";

        HashSet<string> produced = new(StringComparer.Ordinal);

        foreach (GrimoireSchemaInspectionFailure failure in Enum.GetValues<GrimoireSchemaInspectionFailure>())
        {

            GrimoireSchemaInspectionResult result =
                GrimoireSchemaInspectionResult.Invalid(failure, OffendingObject);

            string? code = result.DiagnosticCode;

            Assert.NotNull(code);

            Assert.StartsWith("Grimoire.Schema.", code, StringComparison.Ordinal);

            Assert.DoesNotContain(OffendingObject, code, StringComparison.Ordinal);

            foreach (string forbidden in ContentMarkers)
            {

                Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);

            }

            Assert.True(produced.Add(code), $"{code} was produced by more than one failure");

        }

        Assert.Equal(expectedCodes, produced.Order(StringComparer.Ordinal));

        // A valid result has nothing to report, so it carries no code to log either.
        Assert.Null(GrimoireSchemaInspectionResult.Valid("sha256-abc").DiagnosticCode);

    }

    private static GrimoireSchemaManifestInspector CreateInspector() =>
        new(GrimoireSchemaTierOwnershipRegistry.CreateDefault());

    private static void ConstructRegistry(IReadOnlyList<GrimoireSchemaManifest> manifests) =>
        _ = new GrimoireSchemaTierOwnershipRegistry(manifests);

    /// <summary>
    /// A one-object manifest over a hand-made definition, so ownership conflicts can be constructed
    /// without depending on two shipped tiers ever colliding.
    /// </summary>
    private static GrimoireSchemaManifest BuildSingleObjectManifest(
        GrimoireSchemaFamily family,
        GrimoireSchemaTransactionTier tier,
        string name) =>
        GrimoireSchemaManifestBuilder.Build(
            family,
            tier,
            GrimoireSchemaVersionChains.CovenantCanonicalSchemaVersion,
            $"source-fingerprint-{tier}",
            [
                new GrimoireSchemaObject(
                    family,
                    tier,
                    GrimoireSchemaCategory.Tables,
                    name,
                    $"Fixture.{name}",
                    $"CREATE TABLE IF NOT EXISTS {name} (Id TEXT NOT NULL PRIMARY KEY);"),
            ]);

    private static async Task ReplaceEntriesCampaignIndexAsync(
        CovenantSchemaScratchDatabase database,
        string definition)
    {

        await database.ExecuteAsync("DROP INDEX idx_covenant_entries_campaign;", CancellationToken.None);

        await database.ExecuteAsync(definition, CancellationToken.None);

    }

    private static string ReadCanonicalSql(string name)
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantCanonicalObjects)
        {

            if (string.Equals(definition.Name, name, StringComparison.Ordinal))
            {

                return GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null);

            }

        }

        throw new InvalidOperationException(
            $"The Covenant canonical schema catalog declares no object named '{name}'.");

    }

    private static async Task<GrimoireSchemaInspectionResult> InspectCanonicalAsync(
        CovenantSchemaScratchDatabase database) =>
        await CreateInspector().InspectAsync(
            database.Connection,
            transaction: null,
            GrimoireSchemaManifests.CovenantCanonical,
            CancellationToken.None);

    private static async Task<GrimoireSchemaInspectionResult> InspectAcceleratorAsync(
        CovenantSchemaScratchDatabase database) =>
        await CreateInspector().InspectAsync(
            database.Connection,
            transaction: null,
            GrimoireSchemaManifests.CovenantAccelerator,
            CancellationToken.None);

    /// <summary>
    /// The closed reason and the offending object, which is everything a failed inspection carries
    /// and therefore everything a failing assertion can usefully print.
    /// </summary>
    private static string Describe(GrimoireSchemaInspectionResult result) =>
        $"failure={result.Failure} object={result.ObjectName}";

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

}
