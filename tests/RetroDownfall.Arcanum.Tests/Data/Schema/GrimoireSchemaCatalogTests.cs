using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Authoring conventions of the declarative <c>Data/Schema/</c> tree (DESIGN §5.4.5). These are the
/// rules a reviewer would otherwise have to enforce by eye on every schema change.
/// </summary>
public sealed class GrimoireSchemaCatalogTests
{

    [Fact]
    public void Catalog_is_populated_from_the_embedded_glob()
    {

        Assert.NotEmpty(GrimoireSchemaCatalog.AllObjects);

        Assert.NotEmpty(GrimoireSchemaCatalog.CoreObjects);

        Assert.NotEmpty(GrimoireSchemaCatalog.AcceleratorObjects);

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
                $"'{definition.Category}/{definition.Name}.sql' declares {declarations} objects; the convention is exactly one (indexes co-locate with their owning table).");

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
                    $"'{definition.Category}/{definition.Name}.sql' has a non-idempotent statement: {trimmed}");

            }

        }

    }

    [Fact]
    public void Only_accelerators_are_dimension_templated()
    {

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CoreObjects)
        {

            Assert.DoesNotContain("{{", definition.Sql, StringComparison.Ordinal);

        }

        Assert.All(
            GrimoireSchemaCatalog.AcceleratorObjects,
            static definition => Assert.Contains(
                GrimoireSchemaCatalog.EmbeddingDimensionsToken,
                definition.Sql,
                StringComparison.Ordinal));

    }

    [Fact]
    public void Core_objects_install_tables_then_full_text_search_then_triggers()
    {

        List<int> tiers = [.. GrimoireSchemaCatalog.CoreObjects.Select(static definition => (int)definition.Category)];

        Assert.Equal<IEnumerable<int>>([.. tiers.Order()], tiers);

        Assert.DoesNotContain(
            GrimoireSchemaCatalog.CoreObjects,
            static definition => definition.Category == GrimoireSchemaCategory.Accelerators);

    }

    [Fact]
    public void Resolve_substitutes_the_configured_embedding_dimensions()
    {

        GrimoireSchemaObject accelerator = GrimoireSchemaCatalog.AcceleratorObjects[0];

        string resolved = GrimoireSchemaCatalog.Resolve(accelerator, 3072);

        Assert.Contains("FLOAT[3072]", resolved, StringComparison.Ordinal);

        Assert.DoesNotContain("{{", resolved, StringComparison.Ordinal);

    }

    [Fact]
    public void Resolve_rejects_an_unresolved_placeholder()
    {

        GrimoireSchemaObject broken = new(
            GrimoireSchemaCategory.Tables,
            "Broken",
            "CREATE TABLE IF NOT EXISTS broken (Value FLOAT[{{NotAToken}}]);");

        _ = Assert.Throws<InvalidOperationException>(() => GrimoireSchemaCatalog.Resolve(broken, 1536));

    }

    [Fact]
    public void Canonical_fingerprint_is_stable_across_reads()
    {

        Assert.Equal(
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint,
            GrimoireSchemaCatalog.CanonicalSchemaFingerprint);

        Assert.Equal(64, GrimoireSchemaCatalog.CanonicalSchemaFingerprint.Length);

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
