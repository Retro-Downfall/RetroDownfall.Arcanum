using System.Text.Json;
using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildGrantSerializationTests
{
    private const string RootJson = """
        {"workspaces":[],"tools":[],"memory":[],"budget":{"usd":{"mode":"unlimited"},"tokens":{"mode":"finite","value":0}},"delegation":[],"expiresAtUtc":null}
        """;

    [Theory]
    [InlineData("workspaces")]
    [InlineData("tools")]
    [InlineData("memory")]
    [InlineData("budget")]
    [InlineData("delegation")]
    [InlineData("expiresAtUtc")]
    public void Every_root_field_must_be_present_even_nullable_expiry(string name)
    {
        JsonObject root = JsonNode.Parse(RootJson)!.AsObject();

        Assert.True(root.Remove(name));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(root.ToJsonString(), GuildJsonContext.Default.GuildGrant));
    }

    [Fact]
    public void Root_requires_explicit_budget_dimensions_and_mode()
    {
        string[] invalid =
        [
            RootJson.Replace("\"usd\":{\"mode\":\"unlimited\"},", "", StringComparison.Ordinal),
            RootJson.Replace("\"mode\":\"unlimited\"", "", StringComparison.Ordinal),
        ];

        Assert.All(invalid, json => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildGrant)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expiresAtUtc\":null}")]
    [InlineData("{\"workspaces\":null}")]
    [InlineData("{\"tools\":null}")]
    [InlineData("{\"memory\":null}")]
    [InlineData("{\"budget\":null}")]
    [InlineData("{\"delegation\":null}")]
    [InlineData("{\"tools\":[]}")]
    public void Child_roundtrip_preserves_absence_explicit_null_and_empty(string json)
    {
        GuildRequestedGrant value = JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildRequestedGrant)!;

        Assert.Equal(json, JsonSerializer.Serialize(value, GuildJsonContext.Default.GuildRequestedGrant));
    }

    [Fact]
    public void Child_expiry_null_is_distinct_from_omission_without_a_wire_wrapper()
    {
        GuildRequestedGrant omitted = JsonSerializer.Deserialize("{}", GuildJsonContext.Default.GuildRequestedGrant)!;

        GuildRequestedGrant noExpiry = JsonSerializer.Deserialize("{\"expiresAtUtc\":null}", GuildJsonContext.Default.GuildRequestedGrant)!;

        Assert.False(omitted.ExpiresAtUtc.IsSpecified);

        Assert.True(noExpiry.ExpiresAtUtc.IsSpecified);

        Assert.Null(noExpiry.ExpiresAtUtc.Value);

        Assert.NotEqual(omitted, noExpiry);
    }

    [Theory]
    [InlineData("{\"expiresAtUtc\":null,\"expiresAtUtc\":null}")]
    [InlineData("{\"extraAuthority\":true}")]
    [InlineData("{\"delegation\":[0]}")]
    [InlineData("{\"delegation\":[\"unrecognized\"]}")]
    [InlineData("{\"tools\":{}}")]
    [InlineData("[]")]
    public void Child_rejects_ambiguous_or_unknown_wire_shapes(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildRequestedGrant));
    }

    [Fact]
    public void Token_amount_cannot_bind_a_fractional_decimal()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            RootJson.Replace("\"value\":0", "\"value\":0.1", StringComparison.Ordinal), GuildJsonContext.Default.GuildGrant));
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950335.1")]
    public void Nonrepresentable_usd_authority_is_rejected_instead_of_rounded(string number)
    {
        string json = RootJson.Replace("\"usd\":{\"mode\":\"unlimited\"}",
            "\"usd\":{\"mode\":\"finite\",\"value\":" + number + "}", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildGrant));
    }
}
