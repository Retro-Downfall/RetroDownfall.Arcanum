using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The exact output <see cref="GrimoireSqlNormalizer"/> produces, pinned character for character.
/// </summary>
/// <remarks>
/// This is not a unit test of a formatting helper. From Core schema version 6 the tier's published
/// source-definition fingerprint is taken over this function's output, so what it returns <i>is</i>
/// Core's durable identity in <c>grimoire_feature_schemas</c>, and Core is the tier whose refusal
/// aborts startup. Nothing else in the tree can catch a change to it: head fingerprints are never
/// pinned by design, every version fixture hashes the raw bytes instead, and the published-fingerprint
/// test compares the function to itself and agrees with any behaviour. A normaliser that grew slightly
/// more forgiving - collapsing the space around a comma, say - would move Core's head value, and the
/// first thing to notice would be every version-6 installation refusing to open.
///
/// <para>So the golden below is a literal on both sides. It is written to be read as a specification
/// of the four decisions that matter rather than as a captured string: whitespace runs collapse to one
/// space and never to zero, both comment forms are removed rather than collapsed, a quoted literal is
/// preserved byte for byte including its own doubled spaces, and <c>IF NOT EXISTS</c> and the
/// terminating semicolon are dropped because SQLite does not store them.</para>
/// </remarks>
public sealed class GrimoireSqlNormalizerTests
{

    /// <summary>
    /// Reindented, commented in both forms, and padded everywhere padding is legal.
    /// </summary>
    private const string GoldenInput =
        """
        -- a leading line comment, removed entirely
          /* and a block comment */  CREATE   TABLE IF NOT EXISTS  "golden" (
            "Id"    TEXT   NOT NULL ,   -- a trailing line comment
            "Note"  TEXT   DEFAULT 'two  spaces  kept'   /* inline */ ,
            "Flag"  INTEGER DEFAULT 0
        ) ;
        """;

    /// <summary>
    /// What <see cref="GrimoireSqlNormalizer.Normalize"/> must return for <see cref="GoldenInput"/>.
    /// </summary>
    /// <remarks>
    /// Every space here is load-bearing. The one before each comma is the case a more forgiving
    /// normaliser would remove; the one between <c>"Id"</c> and <c>TEXT</c> is the case a more
    /// aggressive one would remove, which would also fuse <c>NOT NULL</c> into a different token
    /// entirely. The doubled spaces inside the string literal are the case neither may touch, because
    /// a collapsed <c>RAISE(ABORT, …)</c> message is a changed abort message that would then compare
    /// equal to the old one.
    /// </remarks>
    private const string GoldenOutput =
        """CREATE TABLE "golden" ( "Id" TEXT NOT NULL , "Note" TEXT DEFAULT 'two  spaces  kept' , "Flag" INTEGER DEFAULT 0 )""";

    [Fact]
    public void The_normalizer_output_is_pinned_because_Core_records_it()
    {

        string actual = GrimoireSqlNormalizer.Normalize(GoldenInput.ReplaceLineEndings("\n"));

        if (string.Equals(GoldenOutput, actual, StringComparison.Ordinal))
        {

            return;

        }

        // Assert.Fail rather than Assert.Equal: the diff alone would send a reader to fix the pin,
        // which is the one repair that is always wrong here unless a version step is being authored.
        Assert.Fail(
            "GrimoireSqlNormalizer.Normalize no longer returns the pinned form.\n\n"
            + "From Core schema version 6 this function's output is what CoreSchemaFingerprint is "
            + "computed over, so its return value IS Core's durable identity in "
            + "grimoire_feature_schemas. Changing it moves that identity for every object at once, and "
            + "Core is the tier whose refusal aborts startup - so the first symptom of an unreviewed "
            + "change here is every installation at the current version refusing to open with "
            + "SourceDefinitionMismatch, with no test having gone red first.\n\n"
            + "CHANGING THIS FUNCTION IS A CORE SCHEMA VERSION STEP. Raise CoreSchemaVersion, pin the "
            + "currently published CoreSchemaFingerprint as the source pin for the version the step "
            + "leaves before editing anything, add the fixture that reconstructs that version, and "
            + "update this golden in the same change. If you are not authoring a version step, revert "
            + "the normaliser rather than the pin.\n\n"
            + $"expected: {GoldenOutput}\n"
            + $"actual:   {actual}");

    }

    /// <summary>
    /// The same pin stated as the four rules it encodes, so a failure above says which one moved.
    /// </summary>
    /// <remarks>
    /// A single golden string tells a reader that something changed and not what. These name the
    /// decisions individually, and the identifier-adjacent pair is the one worth stating twice: a run
    /// of whitespace between two identifiers must become exactly one space, because zero would fuse
    /// them into a token that is not the same SQL and two would make a reindentation visible again.
    /// </remarks>
    [Theory]
    [InlineData("CREATE  TABLE", "CREATE TABLE")]
    [InlineData("\"Id\"    TEXT", "\"Id\" TEXT")]
    [InlineData("NOT\n\n   NULL", "NOT NULL")]
    [InlineData("a , b", "a , b")]
    [InlineData("a ,\n  b", "a , b")]
    [InlineData("( a )", "( a )")]
    [InlineData("x -- comment\ny", "x y")]
    [InlineData("x /* comment */ y", "x y")]
    [InlineData("CREATE TABLE IF NOT EXISTS t", "CREATE TABLE t")]
    [InlineData("SELECT 1 ;", "SELECT 1")]
    [InlineData("RAISE(ABORT, 'two  spaces')", "RAISE(ABORT, 'two  spaces')")]
    public void The_normalizer_rules_are_pinned_one_at_a_time(string input, string expected) =>
        Assert.Equal(expected, GrimoireSqlNormalizer.Normalize(input));

}
