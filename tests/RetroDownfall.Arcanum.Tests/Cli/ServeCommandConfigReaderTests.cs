using Microsoft.Extensions.Configuration;
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
    public void ReadConfiguredMaxRequestBodyBytes_returns_default_when_missing()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());

        long maxBytes = ServeCommand.ReadConfiguredMaxRequestBodyBytes(configuration);

        Assert.Equal(new HostSettings().MaxRequestBodyBytes, maxBytes);

    }

    [Fact]
    public void ReadConfiguredMaxRequestBodyBytes_parses_valid_integer()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:MaxRequestBodyBytes"] = "2097152",
        });

        long maxBytes = ServeCommand.ReadConfiguredMaxRequestBodyBytes(configuration);

        Assert.Equal(2_097_152L, maxBytes);

    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    }

}
