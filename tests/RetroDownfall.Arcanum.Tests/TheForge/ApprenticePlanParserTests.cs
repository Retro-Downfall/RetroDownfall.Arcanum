using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

public sealed class ApprenticePlanParserTests
{

    [Fact]
    public void ParsePlan_ValidJsonArray_ReturnsNormalizedSteps()
    {

        const string json = """
            [
              { "description": "First step" },
              { "index": 3, "description": "Third", "status": "running" }
            ]
            """;

        List<PlanStep> steps = ApprenticePlanParser.ParsePlan(json);

        Assert.Equal(2, steps.Count);

        Assert.Equal(1, steps[0].Index);

        Assert.Equal("pending", steps[0].Status);

        Assert.Equal("First step", steps[0].Description);

        Assert.Equal(3, steps[1].Index);

        Assert.Equal("running", steps[1].Status);

    }

    [Fact]
    public void ParsePlan_MarkdownFencedJson_StripsFences()
    {

        const string fenced = """
            ```json
            [{"description":"Fenced step"}]
            ```
            """;

        List<PlanStep> steps = ApprenticePlanParser.ParsePlan(fenced);

        Assert.Single(steps);

        Assert.Equal("Fenced step", steps[0].Description);

        Assert.Equal(1, steps[0].Index);

    }

    [Fact]
    public void ParsePlan_EmptyArray_Throws()
    {

        Assert.Throws<InvalidOperationException>(() => ApprenticePlanParser.ParsePlan("[]"));

    }

    [Fact]
    public void ParsePlan_InvalidJson_ThrowsInvalidOperationNotJsonException()
    {

        // W3.6: malformed plan JSON surfaces as a domain InvalidOperationException (consistent with
        // the empty/oversize cases and TryParseRevisedPlan), not a raw JsonException for callers to catch.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ApprenticePlanParser.ParsePlan("not-json"));

        Assert.Contains("malformed JSON", ex.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void ParsePlan_OversizedInput_ThrowsBeforeParsing()
    {

        string oversized = new('x', ApprenticePlanParser.MaxResponseChars + 1);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ApprenticePlanParser.ParsePlan(oversized));

        Assert.Contains("maximum allowed", ex.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseRevisedPlan_EmptyInput_ReturnsFalse(string? responseText)
    {

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan(responseText!, out List<PlanStep>? steps);

        Assert.False(parsed);

        Assert.Null(steps);

    }

    [Fact]
    public void TryParseRevisedPlan_RejectsNoChange()
    {

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan("NO_CHANGE", out List<PlanStep>? steps);

        Assert.False(parsed);

        Assert.Null(steps);

    }

    [Fact]
    public void TryParseRevisedPlan_ParsesValidArray()
    {

        const string json = """[{"index":1,"description":"Revised step"}]""";

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan(json, out List<PlanStep>? steps);

        Assert.True(parsed);

        Assert.NotNull(steps);

        Assert.Single(steps!);

        Assert.Equal("Revised step", steps![0].Description);

        Assert.Equal(1, steps[0].Index);

        Assert.Equal("pending", steps[0].Status);

    }

    [Fact]
    public void TryParseRevisedPlan_MarkdownFencedJson_ParsesSuccessfully()
    {

        const string fenced = """
            ```json
            [{"description":"Revised fenced"}]
            ```
            """;

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan(fenced, out List<PlanStep>? steps);

        Assert.True(parsed);

        Assert.NotNull(steps);

        Assert.Equal("Revised fenced", steps![0].Description);

    }

    [Fact]
    public void TryParseRevisedPlan_InvalidJson_ReturnsFalse()
    {

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan("{not-valid", out List<PlanStep>? steps);

        Assert.False(parsed);

        Assert.Null(steps);

    }

    [Fact]
    public void TryParseRevisedPlan_EmptyArray_ReturnsFalse()
    {

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan("[]", out List<PlanStep>? steps);

        Assert.False(parsed);

        Assert.Null(steps);

    }

    [Fact]
    public void ParsePlan_ExceedsMaxSteps_Throws()
    {

        string json = """
            [
              { "description": "one", "status": "pending" },
              { "description": "two", "status": "pending" }
            ]
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ApprenticePlanParser.ParsePlan(json, maxSteps: 1));

        Assert.Contains("maximum allowed is 1", ex.Message, StringComparison.Ordinal);

    }

}
