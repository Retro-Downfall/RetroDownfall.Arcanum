using System.Collections.Immutable;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendSerializationTests
{
    [Theory]
    [InlineData(typeof(GuildSpendRefreshRequest[]))]
    [InlineData(typeof(ImmutableArray<GuildSpendRefreshRequest>))]
    [InlineData(typeof(GuildSpendRefreshResultDto[]))]
    [InlineData(typeof(ImmutableArray<GuildSpendRefreshResultDto>))]
    public void Refresh_collections_have_explicit_generated_metadata(Type type)
    {
        Assert.NotNull(GuildJsonContext.Default.GetTypeInfo(type));
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("0.12345678901234567890123456789")]
    public void Nonrepresentable_usd_is_refused(string number)
    {
        Assert.Throws<JsonException>(() => ParseAmounts($"{{\"usd\":{number}}}"));
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Exact_decimal_extremes_round_trip(string number, string expected)
    {
        var amounts = ParseAmounts($"{{\"usd\":{number}}}");

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), amounts!.Usd);
    }

    [Fact]
    public void Omitted_numbers_remain_missing_and_integer_origins_are_rejected()
    {
        var amounts = ParseAmounts("{}");

        Assert.True(GuildSpendCorrectionPolicy.Validate(new(0, 0, 0, 0, 0, false, false, true), amounts).IsFailure);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"origin\":2}", GuildJsonContext.Default.GuildSpendCorrectionRequest));
    }

    [Theory]
    [InlineData(GuildSpendOrigin.LocalResponse, "localResponse")]
    [InlineData(GuildSpendOrigin.RemoteReport, "remoteReport")]
    [InlineData(GuildSpendOrigin.OperatorAttested, "operatorAttested")]
    [InlineData(GuildSpendOrigin.ProviderReported, "providerReported")]
    public void Origins_round_trip_as_closed_wire_strings(GuildSpendOrigin origin, string wire)
    {
        var json = JsonSerializer.Serialize(origin, GuildJsonContext.Default.GuildSpendOrigin);

        Assert.Equal($"\"{wire}\"", json);

        Assert.Equal(origin, JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildSpendOrigin));
    }

    [Fact]
    public void Archived_refresh_request_and_result_round_trip_through_the_historical_context()
    {
        Guid key = Guid.NewGuid();

        string request = JsonSerializer.Serialize(new GuildSpendRefreshRequest(key), GuildJsonContext.Default.GuildSpendRefreshRequest);

        Assert.Equal(key, JsonSerializer.Deserialize(request, GuildJsonContext.Default.GuildSpendRefreshRequest)!.IdempotencyKey);

        GuildSpendRefreshResultDto data = new(Guid.NewGuid(), null, false, false, true, "Guild.ReconciliationRequired", false);

        string json = JsonSerializer.Serialize(data, GuildJsonContext.Default.GuildSpendRefreshResultDto);

        Assert.Equal(data, JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildSpendRefreshResultDto));
    }

    [Theory]
    [InlineData("Guild.SpendSourceMissing")]
    [InlineData("Guild.SpendSourceInvalid")]
    [InlineData("Guild.SpendGenerationMissing")]
    [InlineData("Guild.SpendGenerationInvalid")]
    [InlineData("Guild.SpendConfigurationChanged")]
    [InlineData("Guild.SpendCredentialChanged")]
    [InlineData("Guild.SpendAuthorityRetired")]
    public void Refresh_source_refusals_are_conflicts(string code) =>
        Assert.Equal(409, ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(code));

    private static GuildSpendAmountsRequest? ParseAmounts(string json) =>
        JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildSpendAmountsRequest);
}
