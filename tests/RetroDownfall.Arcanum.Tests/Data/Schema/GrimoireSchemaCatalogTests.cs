using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Authoring conventions of the declarative <c>Data/Schema/</c> tree (DESIGN §5.4.5), plus the
/// family and transaction-tier dimensions issue #81 adds. These are the rules a reviewer would
/// otherwise have to enforce by eye on every schema change.
/// </summary>
public sealed class GrimoireSchemaCatalogTests
{

    [Fact]
    public void Catalog_is_populated_from_the_embedded_glob()
    {

        Assert.NotEmpty(GrimoireSchemaCatalog.AllObjects);

        Assert.NotEmpty(GrimoireSchemaCatalog.CoreObjects);

    }

    [Fact]
    public void Catalog_exposes_core_covenant_canonical_and_covenant_accelerator_in_order()
    {

        Assert.All(
            GrimoireSchemaCatalog.CoreObjects,
            static item => Assert.Equal(GrimoireSchemaTransactionTier.Core, item.TransactionTier));

        Assert.All(
            GrimoireSchemaCatalog.CovenantCanonicalObjects,
            static item => Assert.Equal(
                GrimoireSchemaTransactionTier.CovenantCanonical,
                item.TransactionTier));

        Assert.All(
            GrimoireSchemaCatalog.CovenantAcceleratorObjects,
            static item => Assert.Equal(
                GrimoireSchemaTransactionTier.CovenantAccelerator,
                item.TransactionTier));

        // The three catalogs partition AllObjects: an object belongs to exactly one install
        // transaction, so a resource that reached none of them would silently never install.
        Assert.Equal(
            GrimoireSchemaCatalog.AllObjects.Count,
            GrimoireSchemaCatalog.CoreObjects.Count
                + GrimoireSchemaCatalog.CovenantCanonicalObjects.Count
                + GrimoireSchemaCatalog.CovenantAcceleratorObjects.Count);

        List<int> tiers =
        [
            .. GrimoireSchemaCatalog.AllObjects.Select(static item => (int)item.TransactionTier),
        ];

        Assert.Equal<IEnumerable<int>>([.. tiers.Order()], tiers);

    }

    [Fact]
    public void Covenant_resources_are_never_in_CoreObjects()
    {

        Assert.DoesNotContain(
            GrimoireSchemaCatalog.CoreObjects,
            static item => item.Family == GrimoireSchemaFamily.Covenant);

        Assert.All(
            GrimoireSchemaCatalog.CovenantCanonicalObjects,
            static item => Assert.Equal(GrimoireSchemaFamily.Covenant, item.Family));

        Assert.All(
            GrimoireSchemaCatalog.CovenantAcceleratorObjects,
            static item => Assert.Equal(GrimoireSchemaFamily.Covenant, item.Family));

    }

    // Not a [Theory]: the dimensions are internal to Infrastructure, and an InlineData parameter
    // would have to be public on the test method.
    [Fact]
    public void Nested_capability_path_encodes_family_tier_and_category()
    {

        AssertParses(
            "Tables.Campaigns",
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            "Campaigns");

        AssertParses(
            "Triggers.lexicon_entries_ai",
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Triggers,
            "lexicon_entries_ai");

        AssertParses(
            "Capabilities.Covenant.Canonical.Tables.covenant_entries",
            GrimoireSchemaFamily.Covenant,
            GrimoireSchemaTransactionTier.CovenantCanonical,
            GrimoireSchemaCategory.Tables,
            "covenant_entries");

        AssertParses(
            "Capabilities.Covenant.Accelerator.FullTextSearch.covenant_fts",
            GrimoireSchemaFamily.Covenant,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            GrimoireSchemaCategory.FullTextSearch,
            "covenant_fts");

        AssertParses(
            "Capabilities.Covenant.Accelerator.Triggers.covenant_search_documents_ad",
            GrimoireSchemaFamily.Covenant,
            GrimoireSchemaTransactionTier.CovenantAccelerator,
            GrimoireSchemaCategory.Triggers,
            "covenant_search_documents_ad");

    }

    [Theory]
    // An unknown category folder at the core root.
    [InlineData("Accelerators.entry_embeddings_vec")]
    // The retired vec0 tier: its folder name must not resolve to anything.
    [InlineData("Views.Tables.Something")]
    // A capability family Arcanum does not ship.
    [InlineData("Capabilities.Lexicon.Canonical.Tables.lexicon_entries")]
    // A transaction tier the Covenant family does not declare.
    [InlineData("Capabilities.Covenant.Projection.Tables.covenant_entries")]
    // A capability path missing its category segment.
    [InlineData("Capabilities.Covenant.Canonical.covenant_entries")]
    // A capability path carrying one segment too many.
    [InlineData("Capabilities.Covenant.Canonical.Tables.Extra.covenant_entries")]
    // A bare name with no category at all.
    [InlineData("covenant_entries")]
    public void Unknown_family_tier_or_category_fails_closed(string relativePath)
    {

        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaCatalog.ParseResourcePath(relativePath));

    }

    [Fact]
    public void Combined_and_covenant_canonical_source_fingerprints_are_stable_and_distinct()
    {

        Assert.Equal(
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint);

        // Uppercase 64-character SHA-256 without a prefix, which is the format GrimoireFixture
        // already writes beside its cached template database. Installed-catalog fingerprints use
        // the separate lowercase "sha256-" form and must never be compared with these.
        Assert.Equal(64, GrimoireSchemaCatalog.CanonicalSchemaFingerprint.Length);

        Assert.Equal(64, GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint.Length);

        // The combined fingerprint covers every resource and the Covenant one covers a strict
        // subset, so a core-only schema edit must not silently reuse a capability identity.
        Assert.NotEqual(
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint);

    }

    /// <summary>
    /// Every tier's recorded source identity covers that tier's resources and nothing else. Core is
    /// the one that has to hold: its identity is compared on every open and a Core
    /// <c>SourceDefinitionMismatch</c> is fatal rather than degrading, so deriving it from the whole
    /// tree would turn a comment-only edit under <c>Capabilities/Covenant/</c> into a host and a CLI
    /// that cannot open the Grimoire at all.
    /// </summary>
    [Fact]
    public void Core_tier_source_identity_is_scoped_to_core_resources()
    {

        // The whole-tree value is the test fixture's cached-template key, not any tier's identity. It
        // moves when any resource in any tier changes, which is exactly what a tier identity must
        // never do.
        Assert.NotEqual(
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint,
            GrimoireSchemaManifests.Core.SourceDefinitionFingerprint);

        Assert.Equal(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            GrimoireSchemaManifests.Core.SourceDefinitionFingerprint);

        Assert.Equal(64, GrimoireSchemaCatalog.CoreSchemaFingerprint.Length);

        // Three tiers, three identities. A shared value would let one tier's recorded row satisfy
        // another's gate.
        Assert.NotEqual(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint);

        Assert.NotEqual(
            GrimoireSchemaCatalog.CoreSchemaFingerprint,
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
            GrimoireSchemaManifests.CovenantCanonical.SourceDefinitionFingerprint);

        Assert.Equal(
            GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
            GrimoireSchemaManifests.CovenantAccelerator.SourceDefinitionFingerprint);

    }

    [Fact]
    public void Every_object_declares_exactly_one_object()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            int declarations = CountOccurrences(definition.Sql, "CREATE TABLE")
                + CountOccurrences(definition.Sql, "CREATE VIRTUAL TABLE")
                + CountOccurrences(definition.Sql, "CREATE TRIGGER")
                + CountOccurrences(definition.Sql, "CREATE VIEW");

            // "CREATE VIRTUAL TABLE" does not contain "CREATE TABLE" as a substring, so a virtual
            // table counts once. Co-located CREATE INDEX statements are deliberately not counted.
            Assert.True(
                declarations == 1,
                $"'{definition.ResourcePath}' declares {declarations} objects; the convention is exactly one (indexes co-locate with their owning table).");

        }

    }

    [Fact]
    public void Every_object_name_matches_its_declaration()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            Assert.Equal(definition.Name, GrimoireSchemaCatalog.ReadDeclaredObjectName(definition.Sql));

        }

    }

    [Fact]
    public void Every_statement_is_idempotent_so_a_reopen_is_a_no_op()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            foreach (string line in definition.Sql.Split('\n'))
            {

                string trimmed = line.Trim();

                if (!trimmed.StartsWith("CREATE", StringComparison.Ordinal))
                {

                    continue;

                }

                Assert.True(
                    trimmed.Contains("IF NOT EXISTS", StringComparison.Ordinal),
                    $"'{definition.ResourcePath}' has a non-idempotent statement: {trimmed}");

            }

        }

    }

    [Fact]
    public void No_shipped_object_is_dimension_templated()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.AllObjects)
        {

            Assert.DoesNotContain("{{", definition.Sql, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void Objects_install_tables_then_full_text_search_then_triggers_within_each_tier()
    {

        foreach (IReadOnlyList<GrimoireSchemaObject> tier in
            (IReadOnlyList<GrimoireSchemaObject>[])
            [
                GrimoireSchemaCatalog.CoreObjects,
                GrimoireSchemaCatalog.CovenantCanonicalObjects,
                GrimoireSchemaCatalog.CovenantAcceleratorObjects,
            ])
        {

            List<int> categories = [.. tier.Select(static definition => (int)definition.Category)];

            Assert.Equal<IEnumerable<int>>([.. categories.Order()], categories);

        }

    }

    [Fact]
    public void Resolve_rejects_an_unresolved_placeholder()
    {

        GrimoireSchemaObject broken = new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            "broken",
            "Tables.broken",
            "CREATE TABLE IF NOT EXISTS broken (Value FLOAT[{{NotAToken}}]);");

        _ = Assert.Throws<InvalidOperationException>(() => GrimoireSchemaCatalog.Resolve(broken, 1536));

    }

    [Fact]
    public void Resolve_substitutes_the_configured_embedding_dimensions()
    {

        // Built here rather than taken from the catalog: no shipped object uses the token, but the
        // substitution mechanism has to keep working for a statically linked accelerator.
        GrimoireSchemaObject templated = new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            "templated_embeddings",
            "Tables.templated_embeddings",
            $"CREATE TABLE IF NOT EXISTS templated_embeddings (Dim INTEGER NOT NULL DEFAULT {GrimoireSchemaCatalog.EmbeddingDimensionsToken});");

        string resolved = GrimoireSchemaCatalog.Resolve(templated, 3072);

        Assert.Contains("DEFAULT 3072", resolved, StringComparison.Ordinal);

        Assert.DoesNotContain("{{", resolved, StringComparison.Ordinal);

    }

    [Fact]
    public void Resolve_without_dimensions_refuses_a_templated_object()
    {

        GrimoireSchemaObject templated = new(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            GrimoireSchemaCategory.Tables,
            "templated_embeddings",
            "Tables.templated_embeddings",
            $"CREATE TABLE IF NOT EXISTS templated_embeddings (Dim INTEGER NOT NULL DEFAULT {GrimoireSchemaCatalog.EmbeddingDimensionsToken});");

        // InstallCoreOnlyAsync has no configured dimension to supply, so a templated object must
        // fail closed there rather than install with a guessed width.
        _ = Assert.Throws<InvalidOperationException>(
            () => GrimoireSchemaCatalog.Resolve(templated, embeddingDimensions: null));

    }

    private static void AssertParses(
        string relativePath,
        GrimoireSchemaFamily expectedFamily,
        GrimoireSchemaTransactionTier expectedTier,
        GrimoireSchemaCategory expectedCategory,
        string expectedName)
    {

        GrimoireSchemaResourcePath parsed = GrimoireSchemaCatalog.ParseResourcePath(relativePath);

        Assert.Equal(expectedFamily, parsed.Family);

        Assert.Equal(expectedTier, parsed.TransactionTier);

        Assert.Equal(expectedCategory, parsed.Category);

        Assert.Equal(expectedName, parsed.Name);

    }

    private static int CountOccurrences(string haystack, string needle)
    {

        int count = 0;

        int index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {

            count++;

            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);

        }

        return count;

    }

}
