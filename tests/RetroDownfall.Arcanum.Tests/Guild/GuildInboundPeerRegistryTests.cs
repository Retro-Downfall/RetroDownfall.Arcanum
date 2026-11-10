using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildInboundPeerRegistryTests
{
    private static readonly string First = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly string Second = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Canonical_credential_authenticates_only_a_current_host_issued_peer()
    {
        using Fixture fixture = new();

        GuildInboundPeer peer = Assert.IsType<GuildInboundPeer>(fixture.Registry.Resolve(First));

        Assert.Equal("alpha", peer.PeerId);
        Assert.True(fixture.Registry.Authenticates(peer));
        Assert.True(fixture.Registry.IsCurrent(peer.PeerId, peer.GrantRevision, peer.AuthorityVersion));
        Assert.Null(fixture.Registry.Resolve(Second));
        Assert.Null(fixture.Registry.Resolve(First + "="));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-credential")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Missing_malformed_or_noncanonical_environment_values_never_authenticate(string credential)
    {
        using Fixture fixture = new(credential);

        Assert.Empty(fixture.Registry.Peers);
        Assert.Null(fixture.Registry.Resolve(credential));
    }

    [Fact]
    public void Duplicate_credentials_and_master_collisions_invalidate_every_affected_binding()
    {
        using Fixture fixture = new();

        fixture.Environment["PEER_B"] = First;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A"), Peer("beta", "PEER_B")));
        Assert.Empty(fixture.Registry.Peers);
        Assert.Null(fixture.Registry.Resolve(First));

        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        fixture.Cache.StoreDigest(SHA256.HashData(Encoding.UTF8.GetBytes(First)), 300);
        Assert.Null(fixture.Registry.Resolve(First));
        Assert.Empty(fixture.Registry.Peers);
    }

    [Fact]
    public void Credential_rotation_preserves_ownership_but_invalidates_old_presentation_and_request_object()
    {
        using Fixture fixture = new();

        GuildInboundPeer old = fixture.Registry.Resolve(First)!;
        fixture.Environment["PEER_A"] = Second;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        GuildInboundPeer current = Assert.IsType<GuildInboundPeer>(fixture.Registry.Resolve(Second));

        Assert.Equal(old.GrantRevision, current.GrantRevision);
        Assert.Equal(old.AuthorityVersion, current.AuthorityVersion);
        Assert.False(fixture.Registry.Authenticates(old));
        Assert.Null(fixture.Registry.Resolve(First));
        Assert.True(fixture.Registry.Authenticates(current));
    }

    [Fact]
    public void Disable_reenable_and_remove_readd_never_reuse_authority_version()
    {
        using Fixture fixture = new();

        GuildInboundPeer original = fixture.Registry.Resolve(First)!;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A", enabled: false)));
        Assert.Null(fixture.Registry.Resolve(First));
        Assert.False(fixture.Registry.IsCurrent(original.PeerId, original.GrantRevision, original.AuthorityVersion));
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        GuildInboundPeer enabled = fixture.Registry.Resolve(First)!;
        Assert.True(enabled.AuthorityVersion > original.AuthorityVersion);
        Assert.Equal(original.GrantRevision, enabled.GrantRevision);
        fixture.Options.Set(Settings());
        long removed = fixture.Registry.GetAuthorityVersion("alpha");
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        Assert.True(fixture.Registry.Resolve(First)!.AuthorityVersion > removed);
    }

    [Fact]
    public void Grant_edit_and_revert_are_visible_even_without_intervening_request()
    {
        using Fixture fixture = new();

        GuildInboundPeer original = fixture.Registry.Resolve(First)!;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A", tokens: 2)));
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        GuildInboundPeer reverted = fixture.Registry.Resolve(First)!;

        Assert.Equal(original.GrantRevision, reverted.GrantRevision);
        Assert.True(reverted.AuthorityVersion > original.AuthorityVersion);
        Assert.False(fixture.Registry.Authenticates(original));
    }

    [Fact]
    public void Unchanged_reload_and_environment_unavailability_do_not_revoke_authority()
    {
        using Fixture fixture = new();

        GuildInboundPeer original = fixture.Registry.Resolve(First)!;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        Assert.Equal(original.AuthorityVersion, fixture.Registry.Resolve(First)!.AuthorityVersion);
        Assert.True(fixture.Registry.Authenticates(original));
        fixture.Environment.Remove("PEER_A");
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        Assert.Empty(fixture.Registry.Peers);
        fixture.Environment["PEER_A"] = First;
        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));
        Assert.Equal(original.AuthorityVersion, fixture.Registry.Resolve(First)!.AuthorityVersion);
    }

    [Fact]
    public void A_persisted_peer_absent_at_startup_cannot_reenter_with_initial_authority_version()
    {
        using Fixture fixture = new();

        fixture.Registry.RememberPeerId("previous-host-peer");

        fixture.Options.Set(Settings(Peer("previous-host-peer", "PEER_A")));

        Assert.True(fixture.Registry.Resolve(First)!.AuthorityVersion > 0);
    }

    [Fact]
    public void Credential_unavailability_is_distinct_from_a_changed_standing_authority()
    {
        using Fixture fixture = new();

        GuildInboundPeer original = fixture.Registry.Resolve(First)!;

        fixture.Environment.Remove("PEER_A");

        fixture.Options.Set(Settings(Peer("alpha", "PEER_A")));

        Assert.True(fixture.Registry.MatchesAuthority(original.PeerId, original.GrantRevision));

        fixture.Options.Set(Settings(Peer("alpha", "PEER_A", enabled: false)));

        Assert.False(fixture.Registry.MatchesAuthority(original.PeerId, original.GrantRevision));
    }

    [Fact]
    public void Lazy_snapshot_refresh_does_not_reenter_a_persistence_listener_from_a_root_gate()
    {
        using Fixture fixture = new();

        int notifications = 0;

        fixture.Registry.Changed += () => notifications++;

        fixture.Options.SetWithoutNotification(Settings(Peer("alpha", "PEER_A", tokens: 2)));

        Assert.NotNull(fixture.Registry.Resolve(First));

        Assert.Equal(0, notifications);
    }

    private static string Peer(string id, string environment, bool enabled = true, long tokens = 0) => $$$"""
        {"PeerId":"{{{id}}}","Enabled":{{{enabled.ToString().ToLowerInvariant()}}},"CredentialEnvironmentVariable":"{{{environment}}}","CampaignId":null,
         "Grant":{"Workspaces":[],"Tools":[],"Memory":[],"Budget":{"Usd":{"Mode":"finite","Value":0},"Tokens":{"Mode":"finite","Value":{{{tokens}}}}},"Delegation":[],"ExpiresAtUtc":null}}
        """;

    private static ArcanumSettings Settings(params string[] peers) => JsonSerializer.Deserialize(
        "{\"Features\":{\"Guild\":true,\"A2AServer\":true},\"Integrations\":{\"A2A\":{\"InboundPeers\":[" + string.Join(',', peers) + "]}}}",
        ConfigurationJsonContext.Default.ArcanumSettings)!;

    private sealed class Fixture : IDisposable
    {
        internal readonly Dictionary<string, string> Environment = new();
        internal readonly Monitor Options = new(Settings(Peer("alpha", "PEER_A")));
        internal readonly ApiKeyDigestCache Cache = new(TimeProvider.System);
        internal readonly GuildInboundPeerRegistry Registry;

        internal Fixture(string? credential = null)
        {
            Environment["PEER_A"] = credential ?? First;
            Cache.StoreDigest(SHA256.HashData(Encoding.UTF8.GetBytes("master-key")), 300);
            Registry = new(Options, Cache, name => Environment.GetValueOrDefault(name));
        }

        public void Dispose() => Registry.Dispose();
    }

    private sealed class Monitor(ArcanumSettings initial) : IOptionsMonitor<ArcanumSettings>
    {
        private event Action<ArcanumSettings, string?>? Changed;
        public ArcanumSettings CurrentValue { get; private set; } = initial;
        public ArcanumSettings Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<ArcanumSettings, string?> listener) { Changed += listener; return new Subscription(() => Changed -= listener); }
        internal void Set(ArcanumSettings value) { CurrentValue = value; Changed?.Invoke(value, null); }
        internal void SetWithoutNotification(ArcanumSettings value) => CurrentValue = value;
        private sealed class Subscription(Action remove) : IDisposable { public void Dispose() => remove(); }
    }
}
