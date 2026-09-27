using System.Collections.Immutable;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

public sealed class LexiconValueNormalizerTests
{
    [Fact]
    public void Canonical_values_have_no_public_construction_or_mutation_bypass()
    {
        Type canonicalType = typeof(LexiconCanonicalValue);

        Assert.Empty(canonicalType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));

        PropertyInfo[] projections = canonicalType.GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(
            ["Facts", "FactsJson", "FactsText", "Name", "NameNormalized", "Type"],
            projections.Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(projections, static property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(ImmutableArray<string>), canonicalType.GetProperty("Facts")!.PropertyType);

        MethodInfo[] digestEntries = typeof(LexiconSnapshotDigest)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(digestEntries);
        Assert.All(
            digestEntries,
            static method => Assert.Equal(
                [typeof(LexiconCanonicalValue)],
                method.GetParameters().Select(static parameter => parameter.ParameterType)));
    }

    [Fact]
    public void Correction_returns_every_canonical_projection_from_one_normalization()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "  Project Phoenix  ",
            "  Project  ",
            ["  ships Friday  ", "", "uses PostgreSQL", "ships Friday"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("Project Phoenix", result.Value.Name);
        Assert.Equal("PROJECT PHOENIX", result.Value.NameNormalized);
        Assert.Equal("Project", result.Value.Type);
        Assert.Equal<string>(["ships Friday", "uses PostgreSQL"], result.Value.Facts);
        Assert.Equal("[\"ships Friday\",\"uses PostgreSQL\"]", result.Value.FactsJson);
        Assert.Equal("ships Friday\nuses PostgreSQL", result.Value.FactsText);
    }

    [Fact]
    public void Correction_rejects_a_blank_type()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "Phoenix",
            "  ",
            ["fact"]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Lexicon.InvalidReplacement, result.Error.Code);
    }

    [Fact]
    public void Scribe_preserves_the_current_type_when_the_incoming_type_is_blank()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeScribe(
            "Phoenix",
            null,
            ["new fact"],
            currentType: "Project",
            currentFacts: ["existing fact"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("Project", result.Value.Type);
        Assert.Equal<string>(["existing fact", "new fact"], result.Value.Facts);
    }

    [Fact]
    public void Scribe_defaults_a_new_entry_type_to_General()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeScribe(
            "Phoenix",
            "  ",
            ["fact"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(LexiconLimits.DefaultType, result.Value.Type);
    }

    [Fact]
    public void Fact_normalization_trims_discards_blanks_and_deduplicates_exact_ordinal_values()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "Phoenix",
            "Project",
            ["  alpha  ", "", "Alpha", "alpha", " \t ", "beta", "Alpha"]);

        Assert.True(result.IsSuccess);
        Assert.Equal<string>(["alpha", "Alpha", "beta"], result.Value.Facts);
    }

    [Fact]
    public void Scribe_deduplicates_repeated_facts_across_current_and_incoming_values()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeScribe(
            "Phoenix",
            null,
            ["alpha", "gamma", "gamma"],
            currentType: "Project",
            currentFacts: ["alpha", "beta", "alpha"]);

        Assert.True(result.IsSuccess);
        Assert.Equal<string>(["alpha", "beta", "gamma"], result.Value.Facts);
    }

    [Fact]
    public void Normalization_requires_at_least_one_nonblank_fact()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "Phoenix",
            "Project",
            ["", "  ", "\t"]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Lexicon.InvalidReplacement, result.Error.Code);
    }

    [Fact]
    public void Existing_name_and_type_length_bounds_are_enforced()
    {
        Result<LexiconCanonicalValue> maximum = LexiconValueNormalizer.NormalizeCorrection(
            new string('n', LexiconLimits.MaxNameLength),
            new string('t', LexiconLimits.MaxTypeLength),
            ["fact"]);

        Result<LexiconCanonicalValue> longName = LexiconValueNormalizer.NormalizeCorrection(
            new string('n', LexiconLimits.MaxNameLength + 1),
            "type",
            ["fact"]);

        Result<LexiconCanonicalValue> longType = LexiconValueNormalizer.NormalizeCorrection(
            "name",
            new string('t', LexiconLimits.MaxTypeLength + 1),
            ["fact"]);

        Assert.True(maximum.IsSuccess);
        Assert.True(longName.IsFailure);
        Assert.Equal(ErrorCodes.Lexicon.InvalidName, longName.Error.Code);
        Assert.True(longType.IsFailure);
        Assert.Equal(ErrorCodes.Lexicon.InvalidReplacement, longType.Error.Code);
    }

    [Fact]
    public void Embedded_controls_newlines_and_non_ascii_text_are_preserved()
    {
        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            "  魔法\0  ",
            "  A\0B\u001F型  ",
            ["  line1\nline2  ", "  café 🧙  ", "unit\u001Fseparator"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("魔法\0", result.Value.Name);
        Assert.Equal("魔法\0", result.Value.NameNormalized);
        Assert.Equal("A\0B\u001F型", result.Value.Type);
        Assert.Equal<string>(["line1\nline2", "café 🧙", "unit\u001Fseparator"], result.Value.Facts);
        Assert.Equal("line1\nline2\ncafé 🧙\nunit\u001Fseparator", result.Value.FactsText);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("fact")]
    public void Unpaired_surrogates_are_refused(string field)
    {
        string name = field == "name" ? "bad\ud800" : "name";
        string type = field == "type" ? "bad\ud800" : "type";
        string fact = field == "fact" ? "bad\ud800" : "fact";

        Result<LexiconCanonicalValue> result = LexiconValueNormalizer.NormalizeCorrection(
            name,
            type,
            [fact]);

        Assert.True(result.IsFailure);
        Assert.Equal(
            field == "name" ? ErrorCodes.Lexicon.InvalidName : ErrorCodes.Lexicon.InvalidReplacement,
            result.Error.Code);
    }
}
