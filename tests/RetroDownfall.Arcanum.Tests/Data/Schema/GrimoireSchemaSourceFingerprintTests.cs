using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// What a tier's published source fingerprint answers for, and what it deliberately does not.
/// </summary>
/// <remarks>
/// The fingerprint is a tier's durable identity in <c>grimoire_feature_schemas</c>, and Core is the
/// tier whose refusal aborts startup. A fingerprint that moved for a corrected comment or a reindented
/// column would refuse every installed Grimoire over an edit that changed no schema, so from Core
/// version 6 the value is taken over the normalized statement rather than over the file's bytes.
///
/// <para>Normalizing is not the same as ignoring. <c>GrimoireSqlNormalizer</c> preserves quoted
/// strings byte for byte and strips only comments, insignificant whitespace, <c>IF NOT EXISTS</c> and
/// the terminating semicolon, so a changed column, a changed abort message, or an added index still
/// moves the value - which the second case here is for.</para>
/// </remarks>
public sealed class GrimoireSchemaSourceFingerprintTests
{

    [Fact]
    public void A_reindented_and_recommented_head_tree_publishes_the_same_core_fingerprint()
    {

        IReadOnlyList<GrimoireSchemaObject> reformatted = Reformatted(GrimoireSchemaCatalog.CoreObjects);

        Assert.Equal(
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CoreObjects),
            GrimoireSchemaCatalog.ComputeSourceFingerprint(reformatted));

    }

    [Fact]
    public void A_changed_statement_still_moves_the_core_fingerprint()
    {

        IReadOnlyList<GrimoireSchemaObject> changed =
        [
            .. GrimoireSchemaCatalog.CoreObjects
                .Select(static (definition, position) => position == 0
                    ? definition with
                    {
                        Sql = definition.Sql
                            + "\nCREATE INDEX IF NOT EXISTS ix_not_declared_anywhere ON \"Entries\" (\"Role\");\n",
                    }
                    : definition),
        ];

        Assert.NotEqual(
            GrimoireSchemaCatalog.ComputeSourceFingerprint(GrimoireSchemaCatalog.CoreObjects),
            GrimoireSchemaCatalog.ComputeSourceFingerprint(changed));

    }

    /// <summary>
    /// The pinned computation is still reachable and still reads the file's bytes.
    /// </summary>
    /// <remarks>
    /// Every pin a shipped step carries - Core 2 through 6, Covenant canonical 2 and 3 - was taken
    /// before normalization existed, and a fixture reconstructing one of those trees has to reproduce
    /// the value the same way. That makes the raw computation a permanent part of the contract rather
    /// than dead code, so it is asserted to still disagree with the normalized one for exactly the
    /// input normalization exists to forgive.
    /// </remarks>
    [Fact]
    public void The_pinned_raw_computation_still_reads_the_bytes()
    {

        IReadOnlyList<GrimoireSchemaObject> reformatted = Reformatted(GrimoireSchemaCatalog.CoreObjects);

        Assert.NotEqual(
            GrimoireSchemaCatalog.ComputeRawSourceFingerprint(GrimoireSchemaCatalog.CoreObjects),
            GrimoireSchemaCatalog.ComputeRawSourceFingerprint(reformatted));

    }

    /// <summary>
    /// The pin is a literal captured before the version-5 tree was edited, and nothing can recompute it
    /// from a tree that no longer exists. Reconstructing that tree and hashing it the way version 5
    /// published it is the only check that the pinned value is the one a version-5 installation actually
    /// recorded - and a wrong pin means every version-5 installation refuses the upgrade with
    /// <c>SourceDefinitionMismatch</c>.
    /// </summary>
    [Fact]
    public void Version_five_reconstruction_matches_the_pinned_fingerprint()
    {

        Assert.Equal(
            "EFD0E3F2981B3462337E83BAAD2BE696AD3279452E85A11903CA6B636AC1B6F9",
            CoreSchemaVersionFiveFixture.Fingerprint);

        GrimoireSchemaVersionChain core =
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        Assert.Equal(CoreSchemaVersionFiveFixture.Fingerprint, core.SourceDefinitionFingerprintFor(5));

    }

    /// <summary>
    /// Reindents every line, wraps the statement in both comment forms, and moves the trailing
    /// newline - the variations the finding names, and none that touch a quoted literal, since no
    /// shipped statement carries one that spans a line.
    /// </summary>
    private static IReadOnlyList<GrimoireSchemaObject> Reformatted(
        IReadOnlyList<GrimoireSchemaObject> definitions) =>
    [
        .. definitions.Select(static definition => definition with { Sql = Reformat(definition.Sql) }),
    ];

    private static string Reformat(string sql) =>
        "-- a comment the fingerprint must not read\n"
        + "/* and a block comment beside it */\n"
        + string.Join("\n", sql.Split('\n').Select(static line => "    " + line))
        + "\n\n-- and one after the statement\n";

}
