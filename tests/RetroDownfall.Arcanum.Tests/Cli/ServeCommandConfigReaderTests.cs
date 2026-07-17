using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ServeCommandConfigReaderTests
{

    [Fact]
    public void ReadConfiguredHostPort_returns_default_when_missing()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());

        int port = ServeCommand.ReadConfiguredHostPort(configuration);

        Assert.Equal(new HostSettings().Port, port);

    }

    [Fact]
    public void ReadConfiguredHostPort_parses_valid_integer()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:Port"] = "8080",
        });

        int port = ServeCommand.ReadConfiguredHostPort(configuration);

        Assert.Equal(8080, port);

    }

    [Fact]
    public void ReadConfiguredHostPort_falls_back_when_value_is_invalid()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:Port"] = "not-a-port",
        });

        int port = ServeCommand.ReadConfiguredHostPort(configuration);

        Assert.Equal(new HostSettings().Port, port);

    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    public void ReadConfiguredListenAny_parses_boolean(string? raw, bool expected)
    {

        Dictionary<string, string?> values = new();

        if (raw is not null)
        {
            values["Arcanum:Host:ListenAny"] = raw;
        }

        IConfiguration configuration = BuildConfiguration(values);

        bool listenAny = ServeCommand.ReadConfiguredListenAny(configuration);

        Assert.Equal(expected, listenAny);

    }

    [Fact]
    public void Configure_sets_default_max_request_body_when_missing()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());

        KestrelServerOptions options = new();

        ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false);

        Assert.Equal(new HostSettings().MaxRequestBodyBytes, options.Limits.MaxRequestBodySize);

    }

    [Fact]
    public void Configure_applies_configured_max_request_body()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:MaxRequestBodyBytes"] = "2097152",
        });

        KestrelServerOptions options = new();

        ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false);

        Assert.Equal(2_097_152L, options.Limits.MaxRequestBodySize);

    }

    [Fact]
    public void Configure_ListenAnyWithoutHttps_ThrowsBeforeBinding()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:Https:Enabled"] = "false",
        });

        KestrelServerOptions options = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: true));

        Assert.Equal(ArcanumKestrelConfigurator.ListenAnyRequiresHttpsMessage, ex.Message);

    }

    [Fact]
    public void ReadConfiguredHttpsPort_returns_default_when_missing()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());

        int port = ServeCommand.ReadConfiguredHttpsPort(configuration);

        Assert.Equal(new HttpsSettings().Port, port);

    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    }

}
