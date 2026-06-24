using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class OutboundUrlGuardTests : IDisposable
{

    private readonly IDnsResolver _originalResolver;

    public OutboundUrlGuardTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver fake = new();

        fake.Add("localhost", IPAddress.Parse("127.0.0.1"));
        fake.Add("example.com", IPAddress.Parse("93.184.216.34"));
        fake.Add("8.8.8.8", IPAddress.Parse("8.8.8.8"));
        fake.Add("93.184.216.34", IPAddress.Parse("93.184.216.34"));
        fake.Add("127.0.0.1", IPAddress.Parse("127.0.0.1"));
        fake.Add("10.1.2.3", IPAddress.Parse("10.1.2.3"));
        fake.Add("127.0.0.1.nip.io", IPAddress.Parse("127.0.0.1"));

        OutboundUrlGuard.DnsResolver = fake;

    }

    public void Dispose()
    {

        OutboundUrlGuard.DnsResolver = _originalResolver;

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateUntrustedUrlAsync_MissingUrl_Fails(string? url)
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url);

        Assert.True(result.IsFailure);

        Assert.Equal(OutboundUrlGuard.BlockedErrorCode, result.Error.Code);

        Assert.Contains("URL is required", result.Error.Message, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://100.127.255.254/")]
    public async Task ValidateUntrustedUrlAsync_CarrierGradeNat_Fails(string url)
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url);

        Assert.True(result.IsFailure);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_Ipv6Unspecified_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("http://[::]/");

        Assert.True(result.IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsBlockedAddress_CarrierGradeNat_Blocked(bool allowPrivateAndLoopback)
    {

        IPAddress cgnat = IPAddress.Parse("100.64.0.1");

        Assert.True(OutboundUrlGuard.IsBlockedAddress(cgnat, allowPrivateAndLoopback));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsBlockedAddress_Ipv6Unspecified_Blocked(bool allowPrivateAndLoopback)
    {

        Assert.True(OutboundUrlGuard.IsBlockedAddress(IPAddress.IPv6Any, allowPrivateAndLoopback));

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_RelativeUrl_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("not-a-valid-uri");

        Assert.True(result.IsFailure);

        Assert.Contains("absolute http or https", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_RelativePathWithFileScheme_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("file:///tmp/x");

        Assert.True(result.IsFailure);

        Assert.Contains("http or https scheme", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_NonHttpScheme_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("ftp://example.com/file");

        Assert.True(result.IsFailure);

        Assert.Contains("http or https scheme", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("https://api.localhost/path")]
    public async Task ValidateUntrustedUrlAsync_LocalhostHostnames_Fail(string url)
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url);

        Assert.True(result.IsFailure);

        Assert.Contains("loopback, private, or link-local", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://0.0.0.0/")]
    public async Task ValidateUntrustedUrlAsync_PrivateLiteralIpv4_Fails(string url)
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url);

        Assert.True(result.IsFailure);

    }

    [Theory]
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://[fe80::1]/")]
    public async Task ValidateUntrustedUrlAsync_LinkLocalAddresses_FailEvenForProvider(string url)
    {

        Result untrusted = await OutboundUrlGuard.ValidateUntrustedUrlAsync(url);

        Result provider = await OutboundUrlGuard.ValidateProviderEndpointAsync(url);

        Assert.True(untrusted.IsFailure);

        Assert.True(provider.IsFailure);

    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://localhost/")]
    public async Task ValidateProviderEndpointAsync_PrivateAndLoopback_Allowed(string url)
    {

        Result result = await OutboundUrlGuard.ValidateProviderEndpointAsync(url);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_PublicLiteralIpv4_Succeeds()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("http://8.8.8.8/");

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_PublicHostname_Succeeds()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("https://example.com/resource");

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_UnresolvableHost_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync(
            "https://this-host-definitely-does-not-exist-12345.invalid/");

        Assert.True(result.IsFailure);

        Assert.Contains("Could not resolve host", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_Ipv4MappedIpv6Loopback_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("http://[::ffff:127.0.0.1]/");

        Assert.True(result.IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsBlockedAddress_Ipv6Loopback_RespectsAllowPrivateFlag(bool allowPrivateAndLoopback)
    {

        IPAddress loopback = IPAddress.IPv6Loopback;

        bool blocked = OutboundUrlGuard.IsBlockedAddress(loopback, allowPrivateAndLoopback);

        Assert.Equal(!allowPrivateAndLoopback, blocked);

    }

    [Fact]
    public void IsBlockedAddress_Ipv6UniqueLocal_BlockedWhenUntrusted()
    {

        IPAddress uniqueLocal = IPAddress.Parse("fc00::1");

        Assert.True(OutboundUrlGuard.IsBlockedAddress(uniqueLocal, allowPrivateAndLoopback: false));

        Assert.False(OutboundUrlGuard.IsBlockedAddress(uniqueLocal, allowPrivateAndLoopback: true));

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_BlockedWebhook_FailsWithPrefixedMessage()
    {

        ArcanumSettings settings = new()
        {
            CommLink = new CommLinkSettings
            {
                WebhookUrl = "http://127.0.0.1/hook",
            },
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsFailure);

        Assert.Equal(OutboundUrlGuard.BlockedErrorCode, result.Error.Code);

        Assert.Contains("CommLink.WebhookUrl", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_BlockedProviderEndpoint_FailsWithProviderName()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ollama-local",
                    Type = AiProviderKind.Ollama,
                    Endpoint = "http://169.254.0.1:11434",
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsFailure);

        Assert.Contains("Provider 'ollama-local' endpoint", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_LlamaCppServerEndpoint_SkipsProviderEndpointValidation()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "llama",
                    Type = AiProviderKind.LlamaCppServer,
                    Endpoint = "http://127.0.0.1:8080",
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_BlockedModelMapUrl_FailsWithModelKey()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "llama",
                    Type = AiProviderKind.LlamaCppServer,
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["tiny"] = "http://127.0.0.1/models/tiny.gguf",
                        },
                    },
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsFailure);

        Assert.Contains("llamaCpp.modelMap['tiny']", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ValidateUntrustedUrlAsync_HostnameResolvingToPrivateIp_Fails()
    {

        Result result = await OutboundUrlGuard.ValidateUntrustedUrlAsync("http://127.0.0.1.nip.io/");

        Assert.True(result.IsFailure);

        Assert.Contains("loopback, private, or link-local", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void IsBlockedAddress_Ipv6SiteLocal_BlockedWhenUntrusted()
    {

        IPAddress siteLocal = IPAddress.Parse("fec0::1");

        Assert.True(OutboundUrlGuard.IsBlockedAddress(siteLocal, allowPrivateAndLoopback: false));

        Assert.False(OutboundUrlGuard.IsBlockedAddress(siteLocal, allowPrivateAndLoopback: true));

    }

    [Fact]
    public void IsBlockedAddress_Ipv4MappedPublicAddress_NotBlocked()
    {

        IPAddress mapped = IPAddress.Parse("::ffff:8.8.8.8");

        Assert.False(OutboundUrlGuard.IsBlockedAddress(mapped, allowPrivateAndLoopback: false));

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_NullCommLink_Succeeds()
    {

        ArcanumSettings settings = new()
        {
            CommLink = null,
            Providers = [],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_NullProviders_Succeeds()
    {

        ArcanumSettings settings = new()
        {
            Providers = null,
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_EmptyModelMapValue_SkipsEntry()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "llama",
                    Type = AiProviderKind.LlamaCppServer,
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["skip"] = "   ",
                            ["valid"] = "https://93.184.216.34/models/tiny.gguf",
                        },
                    },
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_EmptyModelMap_Succeeds()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "llama",
                    Type = AiProviderKind.LlamaCppServer,
                    LlamaCpp = new ProviderLlamaCppSettings
                    {
                        ModelMap = new Dictionary<string, string>(StringComparer.Ordinal),
                    },
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_EmptyProviderEndpoint_SkipsValidation()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "remote",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "   ",
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ValidateArcanumSettingsAsync_ValidSettings_Succeeds()
    {

        ArcanumSettings settings = new()
        {
            CommLink = new CommLinkSettings
            {
                WebhookUrl = "https://93.184.216.34/webhook",
            },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "remote",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://93.184.216.34/v1",
                },
            ],
        };

        Result result = await OutboundUrlGuard.ValidateArcanumSettingsAsync(settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public async Task ResolveValidatedAddressesAsync_PublicLiteral_ReturnsAddress()
    {

        Result<IReadOnlyList<IPAddress>> result = await OutboundUrlGuard.ResolveValidatedAddressesAsync(
            "8.8.8.8",
            allowPrivateAndLoopback: false);

        Assert.True(result.IsSuccess);

        Assert.Contains(result.Value, static address => address.Equals(IPAddress.Parse("8.8.8.8")));

    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    public async Task ResolveValidatedAddressesAsync_UntrustedBlockedHost_Fails(string host)
    {

        Result<IReadOnlyList<IPAddress>> result = await OutboundUrlGuard.ResolveValidatedAddressesAsync(
            host,
            allowPrivateAndLoopback: false);

        Assert.True(result.IsFailure);

        Assert.Equal(OutboundUrlGuard.BlockedErrorCode, result.Error.Code);

    }

    [Fact]
    public async Task ResolveValidatedAddressesAsync_UnresolvableHost_Fails()
    {

        Result<IReadOnlyList<IPAddress>> result = await OutboundUrlGuard.ResolveValidatedAddressesAsync(
            "this-host-definitely-does-not-exist-12345.invalid",
            allowPrivateAndLoopback: false);

        Assert.True(result.IsFailure);

        Assert.Contains("Could not resolve host", result.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void CreateUntrustedEgressHandler_DisablesRedirectsAndPinsConnect()
    {

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        Assert.False(handler.AllowAutoRedirect);

        Assert.NotNull(handler.ConnectCallback);

    }

    [Fact]
    public void CreateProviderEgressHandler_DisablesRedirectsAndPinsConnect()
    {

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

        Assert.False(handler.AllowAutoRedirect);

        Assert.NotNull(handler.ConnectCallback);

    }

    [Theory]
    [InlineData(HttpStatusCode.Moved)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public void IsRedirectStatusCode_RecognizesRedirectResponses(HttpStatusCode statusCode)
    {

        Assert.True(OutboundUrlGuard.IsRedirectStatusCode(statusCode));

    }

    [Fact]
    public void IsRedirectStatusCode_RejectsSuccess()
    {

        Assert.False(OutboundUrlGuard.IsRedirectStatusCode(HttpStatusCode.OK));

    }

    [Fact]
    public void ResolveRedirectLocation_AbsoluteLocation_ReturnsAbsoluteUri()
    {

        Uri requestUri = new("https://huggingface.co/resolve/main/model.gguf");

        Result<string> result = OutboundUrlGuard.ResolveRedirectLocation(
            requestUri,
            "https://cdn.example.com/model.gguf");

        Assert.True(result.IsSuccess);

        Assert.Equal("https://cdn.example.com/model.gguf", result.Value);

    }

    [Fact]
    public void ResolveRedirectLocation_AbsoluteHttpLocation_ReturnsUri()
    {

        Uri requestUri = new("https://example.com/a");

        Result<string> result = OutboundUrlGuard.ResolveRedirectLocation(
            requestUri,
            "http://cdn.example.com/model.gguf");

        Assert.True(result.IsSuccess);

        Assert.Equal("http://cdn.example.com/model.gguf", result.Value);

    }

    [Fact]
    public void ResolveRedirectLocation_RelativeLocation_ResolvesAgainstRequestUri()
    {

        Uri requestUri = new("https://huggingface.co/foo/bar/model.gguf");

        Result<string> result = OutboundUrlGuard.ResolveRedirectLocation(requestUri, "/cdn/model.gguf");

        Assert.True(result.IsSuccess);

        Assert.Equal("https://huggingface.co/cdn/model.gguf", result.Value);

    }

    [Fact]
    public void ResolveRedirectLocation_MissingLocation_Fails()
    {

        Result<string> result = OutboundUrlGuard.ResolveRedirectLocation(
            new Uri("https://example.com/a"),
            null);

        Assert.True(result.IsFailure);

        Assert.Contains("Location header", result.Error.Message, StringComparison.Ordinal);

    }

}
