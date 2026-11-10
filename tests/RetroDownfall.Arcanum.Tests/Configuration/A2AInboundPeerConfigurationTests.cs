using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class A2AInboundPeerConfigurationTests
{
    private const string PeerJson = """
        {"peerId":"trusted-peer","credentialEnvironmentVariable":"MY_PEER_SECRET","campaignId":null,
         "grant":{"workspaces":[],"tools":[],"memory":[],
          "budget":{"usd":{"mode":"finite","value":0},"tokens":{"mode":"unlimited"}},
          "delegation":[],"expiresAtUtc":null}}
        """;

    [Fact]
    public void Complete_peer_binds_through_generated_options_and_keeps_reference_visible()
    {
        string json = Wrap(PeerJson);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        ConfigurationValidator validator = new();

        Assert.True(validator.RejectObsoleteKeys(configuration).IsSuccess);

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings bound = provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.True(validator.Validate(bound).IsSuccess);

        using JsonDocument visible = JsonDocument.Parse(JsonSerializer.Serialize(
            ConfigurationRedactor.Redact(bound), ConfigurationJsonContext.Default.ArcanumSettings));

        JsonElement peer = visible.RootElement.GetProperty("integrations").GetProperty("a2A")
            .GetProperty("inboundPeers")[0];

        Assert.Equal("trusted-peer", peer.GetProperty("peerId").GetString());

        Assert.False(peer.GetProperty("enabled").GetBoolean());

        Assert.Equal(JsonValueKind.Null, peer.GetProperty("campaignId").ValueKind);

        Assert.Equal("MY_PEER_SECRET", peer.GetProperty("credentialEnvironmentVariable").GetString());

        Assert.Equal("unlimited", peer.GetProperty("grant").GetProperty("budget").GetProperty("tokens").GetProperty("mode").GetString());

        Assert.False(ConfigurationPathAccessor.IsSensitive("integrations.a2A.inboundPeers.0.credentialEnvironmentVariable"));
    }

    [Fact]
    public void Disabled_peer_credential_reference_is_denied_to_child_processes()
    {
        ArcanumSettings settings = JsonSerializer.Deserialize(Wrap(PeerJson), ConfigurationJsonContext.Default.ArcanumConfigurationFile)!.Arcanum;

        Assert.Contains("MY_PEER_SECRET", FamiliarSecretEnvironmentNames.Collect(settings));
    }

    [Fact]
    public void Generated_binder_does_not_invent_an_omitted_workspace_root()
    {
        JsonObject peer = JsonNode.Parse(PeerJson)!.AsObject();

        peer["workspaceId"] = "11111111-1111-1111-1111-111111111111";

        peer["grant"]!["workspaces"] = JsonNode.Parse("""
            [{"workspaceId":"11111111-1111-1111-1111-111111111111","access":"read"}]
            """);

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Wrap(peer.ToJsonString())));

        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings bound = provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.True(new ConfigurationValidator().Validate(bound).IsFailure);
    }

    [Fact]
    public void Generated_binder_preserves_complete_nonempty_grant_and_exact_budget_values()
    {
        const string peerJson = """
            {"peerId":"full-peer","enabled":true,"credentialEnvironmentVariable":"FULL_PEER_KEY",
             "campaignId":"22222222-2222-2222-2222-222222222222",
             "sessionId":"33333333-3333-3333-3333-333333333333",
             "workspaceId":"11111111-1111-1111-1111-111111111111",
             "grant":{
              "workspaces":[{"workspaceId":"11111111-1111-1111-1111-111111111111","relativeRoot":"src","access":"readWrite"}],
              "tools":[{"sourceKind":"builtIn","sourceRegistrationId":"arcanum","name":"read_file"}],
              "memory":[{"store":"tapestry","operation":"read","scope":{"kind":"session","id":"33333333-3333-3333-3333-333333333333"}}],
              "budget":{"usd":{"mode":"finite","value":1.234567890123456789},"tokens":{"mode":"finite","value":9223372036854775807}},
              "delegation":["local","remote","cohort"],"expiresAtUtc":"2030-01-02T03:04:05Z"}}
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(Wrap(peerJson)));

        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider provider = services.BuildServiceProvider();

        ArcanumSettings bound = provider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.True(new ConfigurationValidator().RejectObsoleteKeys(configuration).IsSuccess);

        Assert.True(new ConfigurationValidator().Validate(bound).IsSuccess);

        A2AInboundPeerSettings peer = Assert.Single(bound.ResolveA2A().InboundPeers);

        Assert.True(peer.Enabled);

        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), peer.CampaignId);

        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), peer.SessionId);

        GuildGrant grant = peer.Grant!.ToGrant().Value;

        GuildWorkspaceGrant workspace = Assert.Single(grant.Workspaces);

        Assert.Equal("src", workspace.RelativeRoot);

        Assert.Equal(GuildWorkspaceAccess.ReadWrite, workspace.Access);

        Assert.Equal(new GuildToolGrant(GuildToolSourceKind.BuiltIn, "arcanum", "read_file"), Assert.Single(grant.Tools));

        Assert.Equal(new GuildMemoryGrant(GuildMemoryStore.Tapestry, GuildMemoryOperation.Read,
            new GuildMemoryScope(GuildMemoryScopeKind.Session, peer.SessionId)), Assert.Single(grant.Memory));

        Assert.Equal(1.234567890123456789m, grant.Budget.Usd.Value);

        Assert.Equal(long.MaxValue, grant.Budget.Tokens.Value);

        Assert.Equal([GuildDelegationKind.Local, GuildDelegationKind.Remote, GuildDelegationKind.Cohort], grant.Delegation.ToArray());

        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), grant.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("peerId")]
    [InlineData("credentialEnvironmentVariable")]
    [InlineData("campaignId")]
    [InlineData("grant")]
    [InlineData("grant.workspaces")]
    [InlineData("grant.tools")]
    [InlineData("grant.memory")]
    [InlineData("grant.budget")]
    [InlineData("grant.budget.usd")]
    [InlineData("grant.budget.usd.mode")]
    [InlineData("grant.budget.tokens")]
    [InlineData("grant.budget.tokens.mode")]
    [InlineData("grant.delegation")]
    [InlineData("grant.expiresAtUtc")]
    public void Missing_required_peer_axis_is_rejected(string path)
    {
        JsonObject peer = JsonNode.Parse(PeerJson)!.AsObject();

        string[] segments = path.Split('.');

        JsonObject owner = peer;

        foreach (string segment in segments[..^1])
        {
            owner = owner[segment]!.AsObject();
        }

        owner.Remove(segments[^1]);

        AssertInvalid(peer.ToJsonString());
    }

    [Theory]
    [InlineData("{\"mode\":\"finite\"}")]
    [InlineData("{\"mode\":\"finite\",\"value\":-1}")]
    [InlineData("{\"mode\":\"unlimited\",\"value\":1}")]
    [InlineData("{\"mode\":\"mystery\"}")]
    [InlineData("{\"mode\":99}")]
    public void Invalid_budget_dimension_is_rejected(string usd)
    {
        JsonObject peer = JsonNode.Parse(PeerJson)!.AsObject();

        peer["grant"]!["budget"]!["usd"] = JsonNode.Parse(usd);

        AssertInvalid(peer.ToJsonString());
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("grantRevision")]
    [InlineData("mystery")]
    public void Undeclared_peer_key_is_rejected_before_binding(string key)
    {
        JsonObject peer = JsonNode.Parse(PeerJson)!.AsObject();

        peer[key] = "must-not-be-accepted";

        using JsonDocument document = JsonDocument.Parse(Wrap(peer.ToJsonString()));

        Assert.True(new ConfigurationValidator().ValidateConfigurationFileJson(document.RootElement).IsFailure);
    }

    [Theory]
    [InlineData("ARCANUM_EDITION")]
    [InlineData("ARCANUM_Arcanum__Features__Guild")]
    [InlineData("bad-name")]
    [InlineData("")]
    public void Unsafe_credential_reference_is_rejected(string name)
    {
        JsonObject peer = JsonNode.Parse(PeerJson)!.AsObject();

        peer["credentialEnvironmentVariable"] = name;

        AssertInvalid(peer.ToJsonString());
    }

    [Fact]
    public void Peer_ids_are_ordinal_and_duplicates_are_rejected()
    {
        string distinct = Wrap(PeerJson + "," + PeerJson.Replace("trusted-peer", "Trusted-peer", StringComparison.Ordinal)
            .Replace("MY_PEER_SECRET", "MY_OTHER_SECRET", StringComparison.Ordinal));

        ArcanumSettings settings = JsonSerializer.Deserialize(distinct, ConfigurationJsonContext.Default.ArcanumConfigurationFile)!.Arcanum;

        Assert.True(new ConfigurationValidator().Validate(settings).IsSuccess);

        settings = JsonSerializer.Deserialize(Wrap(PeerJson + "," + PeerJson), ConfigurationJsonContext.Default.ArcanumConfigurationFile)!.Arcanum;

        Assert.True(new ConfigurationValidator().Validate(settings).IsFailure);
    }

    private static void AssertInvalid(string peer)
    {
        try
        {
            ArcanumSettings settings = JsonSerializer.Deserialize(Wrap(peer), ConfigurationJsonContext.Default.ArcanumConfigurationFile)!.Arcanum;

            Assert.True(new ConfigurationValidator().Validate(settings).IsFailure);
        }
        catch (JsonException)
        {
            // Source-generated required fields and closed enums also reject malformed input.
        }
    }

    private static string Wrap(string peers) => "{\"Arcanum\":{\"integrations\":{\"a2A\":{\"inboundPeers\":[" + peers + "]}}}}";
}
