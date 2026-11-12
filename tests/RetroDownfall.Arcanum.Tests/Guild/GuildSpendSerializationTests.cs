using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
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

    private static GuildSpendAmountsRequest? ParseAmounts(string json) =>
        JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildSpendAmountsRequest);
}
