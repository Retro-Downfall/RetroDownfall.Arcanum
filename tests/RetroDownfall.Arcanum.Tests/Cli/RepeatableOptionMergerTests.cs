using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class RepeatableOptionMergerTests
{

    [Fact]
    public void Merge_wraps_single_occurrence_as_a_one_element_json_array()
    {
        // A single occurrence is also wrapped (not left as a bare value): CAF's array
        // binding falls back to comma-splitting non-bracketed values, which would corrupt
        // a single-occurrence value that itself contains a comma (e.g. inline JSON).
        string[] args = ["spell", "create", "--name", "x", "--tag", "a"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(["spell", "create", "--name", "x", "--tag", "[\"a\"]"], result);
    }

    [Fact]
    public void Merge_wraps_single_occurrence_containing_a_comma_safely()
    {
        string[] args = ["trial", "run", "--inquisitor", "{\"kind\":\"regex\",\"pattern\":\"Hello\"}"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(
            ["trial", "run", "--inquisitor", "[\"{\\\"kind\\\":\\\"regex\\\",\\\"pattern\\\":\\\"Hello\\\"}\"]"],
            result);
    }

    [Fact]
    public void Merge_combines_repeated_occurrences_into_json_array()
    {
        string[] args = ["spell", "create", "--name", "x", "--tag", "a", "--tag", "b", "--tag", "c"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(["spell", "create", "--name", "x", "--tag", "[\"a\",\"b\",\"c\"]"], result);
    }

    [Fact]
    public void Merge_preserves_values_containing_commas_and_quotes()
    {
        string[] args =
        [
            "trial", "run",
            "--inquisitor", "{\"kind\":\"regex\",\"pattern\":\"a,b\"}",
            "--inquisitor", "second, with \"quotes\"",
        ];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(
            [
                "trial", "run",
                "--inquisitor",
                "[\"{\\\"kind\\\":\\\"regex\\\",\\\"pattern\\\":\\\"a,b\\\"}\",\"second, with \\\"quotes\\\"\"]",
            ],
            result);
    }

    [Fact]
    public void Merge_does_not_touch_arguments_after_double_dash_escape()
    {
        string[] args = ["spell", "execute", "greet", "--", "--tag", "a", "--tag", "b"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(args, result);
    }

    [Fact]
    public void Merge_handles_multiple_distinct_repeatable_flags_independently()
    {
        string[] args =
        [
            "spell", "create",
            "--tag", "a", "--tag", "b",
            "--declared-tool", "read_file",
        ];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(
            ["spell", "create", "--tag", "[\"a\",\"b\"]", "--declared-tool", "[\"read_file\"]"],
            result);
    }

    [Fact]
    public void Merge_does_not_wrap_tag_for_commands_where_it_is_a_scalar_filter()
    {
        // spell search's --tag is a singular string filter, not an array — unlike spell/prompt
        // create/update where --tag is repeatable. Wrapping it here would break the scalar bind.
        string[] args = ["spell", "search", "--query", "greet", "--tag", "demo"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(args, result);
    }

    [Fact]
    public void Merge_does_not_wrap_tag_for_campaign_scoped_scalar_filters()
    {
        string[] args = ["campaign", "spells", "some-id", "--tag", "demo"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(args, result);
    }

    [Fact]
    public void Merge_wraps_tag_for_prompt_update_array_context()
    {
        string[] args = ["prompt", "update", "some-id", "--tag", "a", "--tag", "b"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Equal(["prompt", "update", "some-id", "--tag", "[\"a\",\"b\"]"], result);
    }

    [Fact]
    public void Merge_returns_original_array_when_no_repeatable_flags_present()
    {
        string[] args = ["ask", "hello", "world"];

        string[] result = RepeatableOptionMerger.Merge(args);

        Assert.Same(args, result);
    }

}
