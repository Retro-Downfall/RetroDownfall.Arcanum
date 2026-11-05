using System.Collections.Immutable;
using System.Net;
using RetroDownfall.Arcanum.Tests.Support;
using System.Text;
using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRemoteCardReaderTests
{
    private static GuildConfiguredRemoteSource Source => new("https://peer.example/card", new GuildRemoteTransportDescriptor(
        "configured-peer", "https://peer.example/rpc", "1.0", ["JSONRPC"], [], null, null, null));

    [Fact]
    public void Configured_endpoint_and_real_skill_fields_are_preserved()
    {
        var result = GuildRemoteCardReader.Parse(Source, Encoding.UTF8.GetBytes(Card("https://peer.example/rpc")));

        Assert.True(result.IsSuccess);

        Assert.Equal("configured-peer", result.Value.Card.Source.PeerId);

        Assert.Equal("Review skill", Assert.Single(result.Value.Card.Skills).Name);

        Assert.Equal("Reviews a supplied patch", result.Value.Card.Skills[0].Description);

        Assert.Equal(["code"], result.Value.Card.Skills[0].Tags.ToArray());

        Assert.True(result.Value.Card.Capabilities.Streaming);

        Assert.False(result.Value.Card.Capabilities.StateTransitionHistory);
    }

    [Theory]
    [InlineData("https://other.example/rpc")]
    [InlineData("https://peer.example/other")]
    [InlineData("http://127.0.0.1/rpc")]
    public void Card_cannot_replace_host_configured_interface(string endpoint)
    {
        var result = GuildRemoteCardReader.Parse(Source, Encoding.UTF8.GetBytes(Card(endpoint)));

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.DestinationChanged", result.Error.Code);
    }

    [Fact]
    public void Unknown_or_duplicate_remote_json_is_not_promoted_to_capabilities()
    {
        var result = GuildRemoteCardReader.Parse(Source, Encoding.UTF8.GetBytes(Card("https://peer.example/rpc")
            .Replace("\"streaming\": true", "\"streaming\": true, \"streaming\": false", StringComparison.Ordinal)));

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Private_destination_is_refused_by_real_outbound_policy_before_http()
    {
        FakeDnsResolver dns = new();

        dns.Add("peer.example", IPAddress.Loopback);

        var result = await new GuildRemoteCardReader(dns).ReadAsync(Source, CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.Forbidden", result.Error.Code);
    }

    [Fact]
    public void Configured_peer_and_advertised_agent_keys_cannot_alias_through_delimiters()
    {
        var first = Source with { Transport = Source.Transport with { PeerId = "a:b" }, AdvertisedAgentKey = "c" };

        var second = Source with { Transport = Source.Transport with { PeerId = "a" }, AdvertisedAgentKey = "b:c" };

        Assert.NotEqual(GuildRemoteCardReader.SourceIdentity(first), GuildRemoteCardReader.SourceIdentity(second));
    }

    [Fact]
    public async Task Credential_reference_without_qualified_host_adapter_refuses_before_http()
    {
        FakeDnsResolver dns = new();

        dns.Add("peer.example", IPAddress.Parse("203.0.113.1"));

        var source = Source with
        {
            DiscoveryEndpoint = "http://peer.example:0/card",
            Transport = Source.Transport with { Endpoint = "http://peer.example:0/rpc", CredentialReference = "peer-secret" },
        };

        var result = await new GuildRemoteCardReader(dns).ReadAsync(source, CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.AuthorityUnavailable", result.Error.Code);
    }

    [Theory]
    [InlineData("supportedInterfaces")]
    [InlineData("skills")]
    public void Null_items_in_remote_arrays_are_typed_invalid_cards(string field)
    {
        JsonObject card = JsonNode.Parse(Card("https://peer.example/rpc"))!.AsObject();

        card[field] = JsonNode.Parse("[null]");

        var result = GuildRemoteCardReader.Parse(Source, Encoding.UTF8.GetBytes(card.ToJsonString()));

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.InvalidRequest", result.Error.Code);
    }

    private static string Card(string endpoint) => $$"""
        {
          "name": "Remote reviewer",
          "description": "Review agent",
          "version": "2",
          "supportedInterfaces": [{"url": "{{endpoint}}", "protocolBinding": "JSONRPC", "protocolVersion": "1.0"}],
          "defaultInputModes": ["text/plain"],
          "defaultOutputModes": ["text/plain"],
          "capabilities": {"streaming": true, "pushNotifications": false, "unknownPower": true},
          "skills": [{"id":"review", "name":"Review skill", "description":"Reviews a supplied patch", "tags":["code"]}]
        }
        """;
}
