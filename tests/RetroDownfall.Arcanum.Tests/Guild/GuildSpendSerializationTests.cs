using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendSerializationTests
{
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
    public void Refresh_request_and_envelope_round_trip_through_generated_contexts()
    {
        Guid key = Guid.NewGuid();

        string request = JsonSerializer.Serialize(new GuildSpendRefreshRequest(key), GuildJsonContext.Default.GuildSpendRefreshRequest);

        Assert.Equal(key, JsonSerializer.Deserialize(request, ArcanumJsonContext.Default.GuildSpendRefreshRequest)!.IdempotencyKey);

        GuildSpendRefreshResultDto data = new(Guid.NewGuid(), null, false, false, true, "Guild.ReconciliationRequired");

        ApiResponse<GuildSpendRefreshResultDto> envelope = ApiResponse<GuildSpendRefreshResultDto>.FromResult(Result<GuildSpendRefreshResultDto>.Success(data), "test");

        string json = JsonSerializer.Serialize(envelope, ArcanumJsonContext.Default.ApiResponseGuildSpendRefreshResultDto);

        Assert.Equal(data, JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseGuildSpendRefreshResultDto)!.Data);
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
