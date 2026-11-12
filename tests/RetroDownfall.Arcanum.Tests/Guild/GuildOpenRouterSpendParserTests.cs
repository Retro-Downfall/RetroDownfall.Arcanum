using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildOpenRouterSpendParserTests
{
    // Controlled complete example based on the OpenRouter generation endpoint's documented shape.
    private const string Record = """
        {"data":{"api_type":"completions","app_id":12345,"cache_discount":null,"cancelled":false,
        "created_at":"2024-07-15T23:33:19.433273+00:00","data_region":"global","external_user":"user-123",
        "finish_reason":"stop","generation_time":1200,"http_referer":"https://openrouter.ai/",
        "id":"gen-example","is_byok":false,"latency":1250,"model":"example/model","moderation_latency":50,
        "native_finish_reason":"stop","native_tokens_cached":40,"native_tokens_completion":20,
        "native_tokens_completion_images":0,"native_tokens_prompt":100,"native_tokens_reasoning":5,
        "num_fetches":0,"num_input_audio_prompt":0,"num_media_completion":0,"num_media_prompt":0,
        "num_search_results":5,"origin":"https://openrouter.ai/","preset_id":null,"provider_name":"Example",
        "provider_responses":null,"router":null,"service_tier":"priority","streamed":true,
        "tokens_completion":999,"tokens_prompt":888,"total_cost":0.25,"upstream_id":"other",
        "upstream_inference_cost":12,"usage":14,"user_agent":"example","web_search_engine":null,
        "request_id":"req-example","session_id":null}}
        """;

    [Fact]
    public void Complete_record_uses_native_facts_and_account_cost()
    {
        var result = Parse(Record);

        Assert.True(result.IsSuccess);

        Assert.Equal(new GuildSpendAmountsRequest(100, 20, 40, 5, .25m), result.Value);
    }

    [Fact]
    public void Zero_counts_are_measurements()
    {
        string record = Record.Replace("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":0")
            .Replace("\"native_tokens_completion\":20", "\"native_tokens_completion\":0")
            .Replace("\"native_tokens_cached\":40", "\"native_tokens_cached\":0")
            .Replace("\"native_tokens_reasoning\":5", "\"native_tokens_reasoning\":0")
            .Replace("\"total_cost\":0.25", "\"total_cost\":0");

        Assert.Equal(new GuildSpendAmountsRequest(0, 0, 0, 0, 0), Parse(record).Value);
    }

    [Theory]
    [InlineData("\"id\":\"gen-example\"", "\"id\":\"wrong\"")]
    [InlineData("\"model\":\"example/model\"", "\"model\":\"wrong\"")]
    [InlineData("\"is_byok\":false", "\"is_byok\":true")]
    [InlineData("\"api_type\":\"completions\"", "\"api_type\":\"responses\"")]
    [InlineData("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":null")]
    [InlineData("\"native_tokens_prompt\":100,", "")]
    [InlineData("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":-1")]
    [InlineData("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":1.0")]
    [InlineData("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":9223372036854775808")]
    [InlineData("\"native_tokens_prompt\":100", "\"native_tokens_prompt\":9223372036854775807")]
    [InlineData("\"native_tokens_cached\":40", "\"native_tokens_cached\":101")]
    [InlineData("\"native_tokens_reasoning\":5", "\"native_tokens_reasoning\":21")]
    [InlineData("\"total_cost\":0.25", "\"total_cost\":1e-29")]
    [InlineData("\"total_cost\":0.25", "\"total_cost\":0.12345678901234567890123456789")]
    [InlineData("\"total_cost\":0.25", "\"total_cost\":79228162514264337593543950336")]
    [InlineData("\"total_cost\":0.25", "\"total_cost\":-1")]
    [InlineData("\"num_media_prompt\":0", "\"num_media_prompt\":1")]
    [InlineData("\"num_media_completion\":0", "\"num_media_completion\":null")]
    [InlineData("\"num_input_audio_prompt\":0,", "")]
    [InlineData("\"native_tokens_completion_images\":0", "\"native_tokens_completion_images\":1")]
    [InlineData("\"native_tokens_cached\":40", "\"native_tokens_cached\":40,\"native_tokens_cached\":40")]
    [InlineData("\"id\":\"gen-example\"", "\"id\":\"gen-example\",\"\\u0069d\":\"gen-example\"")]
    public void Unsupported_or_invalid_facts_are_refused(string from, string to)
    {
        var result = Parse(Record.Replace(from, to));

        Assert.True(result.IsFailure);

        Assert.DoesNotContain("gen-example", result.Error.Message);
    }

    [Theory]
    [InlineData("id", "\"gen-example\"")]
    [InlineData("model", "\"example/model\"")]
    [InlineData("api_type", "\"completions\"")]
    [InlineData("is_byok", "false")]
    [InlineData("native_tokens_prompt", "100")]
    [InlineData("native_tokens_completion", "20")]
    [InlineData("native_tokens_cached", "40")]
    [InlineData("native_tokens_reasoning", "5")]
    [InlineData("total_cost", "0.25")]
    [InlineData("native_tokens_completion_images", "0")]
    [InlineData("num_input_audio_prompt", "0")]
    [InlineData("num_media_completion", "0")]
    [InlineData("num_media_prompt", "0")]
    public void Every_required_field_must_be_present_unique_and_non_null(string field, string value)
    {
        string pair = $"\"{field}\":{value}";

        Assert.True(Parse(Record.Replace(pair + ",", "")).IsFailure);

        Assert.True(Parse(Record.Replace(pair, $"\"{field}\":null")).IsFailure);

        Assert.True(Parse(Record.Replace(pair, pair + "," + pair)).IsFailure);
    }

    [Fact]
    public void Duplicate_data_malformed_json_and_invalid_utf8_are_refused()
    {
        Assert.True(Parse(Record.Replace("{\"data\":", "{\"data\":{},\"data\":")).IsFailure);

        Assert.True(Parse(Record[..^1]).IsFailure);

        Assert.True(GuildOpenRouterSpendParser.Parse(new byte[] { 0xff }, "gen-example", "example/model").IsFailure);

        byte[] invalidString = Encoding.UTF8.GetBytes(Record.Replace("Example", "~"));

        invalidString[Array.IndexOf(invalidString, (byte)'~')] = 0xff;

        Assert.True(GuildOpenRouterSpendParser.Parse(invalidString, "gen-example", "example/model").IsFailure);

        Assert.True(GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(Record), "", "example/model").IsFailure);

        Assert.True(GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(Record), "gen-example", " ").IsFailure);
    }

    [Fact]
    public void Generation_id_must_match_provider_syntax_and_length()
    {
        string[] invalid = ["bad", "gen-", "gen-a_b", "gen-a/b", "Gen-a", "gen-" + new string('a', 125)];

        foreach (string id in invalid)
        {
            string json = Record.Replace("gen-example", id);

            Assert.True(GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(json), id, "example/model").IsFailure);
        }

        string boundary = "gen-" + new string('a', 124);

        Assert.True(GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(Record.Replace("gen-example", boundary)), boundary, "example/model").IsSuccess);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    [InlineData("\\uD800\\u0041")]
    public void Unpaired_surrogates_in_required_values_are_refused(string escape)
    {
        Assert.True(Parse(Record.Replace("gen-example", "gen-" + escape)).IsFailure);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void Unpaired_surrogates_in_property_names_are_refused(string escape)
    {
        Assert.True(Parse(Record.Replace("\"api_type\"", "\"" + escape + "\"")).IsFailure);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void Unpaired_surrogates_in_ignored_nested_values_are_refused(string escape)
    {
        var result = Parse(Record.Replace("\"provider_responses\":null", "\"provider_responses\":{\"nested\":[\"" + escape + "\"]}"));

        Assert.True(result.IsFailure);

        Assert.DoesNotContain("gen-example", result.Error.Message);
    }

    [Fact]
    public void Valid_surrogate_pairs_and_escaped_backslashes_are_accepted()
    {
        string json = Record.Replace("\"provider_responses\":null", "\"provider_responses\":{\"nested\":[\"\\uD83D\\uDE00\",\"\\\\uD800\"]}");

        Assert.True(Parse(json).IsSuccess);
    }

    [Fact]
    public void Response_size_is_bounded_inclusive()
    {
        byte[] original = Encoding.UTF8.GetBytes(Record);

        byte[] boundary = Encoding.UTF8.GetBytes(Record + new string(' ', 65536 - original.Length));

        Assert.True(GuildOpenRouterSpendParser.Parse(boundary, "gen-example", "example/model").IsSuccess);

        Assert.True(GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(Record + new string(' ', 65537 - original.Length)), "gen-example", "example/model").IsFailure);
    }

    private static RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendAmountsRequest> Parse(string json) =>
        GuildOpenRouterSpendParser.Parse(Encoding.UTF8.GetBytes(json), "gen-example", "example/model");
}
