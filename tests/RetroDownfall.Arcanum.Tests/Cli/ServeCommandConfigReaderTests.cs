using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
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

        Assert.Equal(
            ArcanumSettingClamps.MaxRequestBodyBytes(
                ArcanumRuntimeDefaults.HostMaxRequestBodyBytes),
            options.Limits.MaxRequestBodySize);

    }

    [Fact]
    public void Configure_ignores_removed_max_request_body_key()
    {

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Arcanum:Host:MaxRequestBodyBytes"] = "2097152",
        });

        KestrelServerOptions options = new();

        ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false);

        Assert.Equal(
            ArcanumSettingClamps.MaxRequestBodyBytes(
                ArcanumRuntimeDefaults.HostMaxRequestBodyBytes),
            options.Limits.MaxRequestBodySize);

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

    /// <summary>
    /// An acknowledgement that cannot be obtained because the console is not interactive is the
    /// documented exit-2 case, not a generic runtime failure — automation keys on the difference.
    /// </summary>
    [Fact]
    public void EnforceListenAnyPolicy_refuses_non_interactively_with_the_configuration_exit_code()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {

            ServeCommand command = new(
                new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors()),
                apiClient: null!);

            int? refusal = command.EnforceListenAnyPolicy(requiresInteractiveConfirmation: true);

            Assert.Equal((int)CliExitCode.ConfigurationError, refusal);

            Assert.Contains("ARCANUM_LISTEN_ANY_ACK", console.Output, StringComparison.Ordinal);

        }
        finally
        {

            AnsiConsole.Console = prior;

        }

    }

    /// <summary>
    /// An already-acknowledged binding still prints the banner but must let the host start.
    /// </summary>
    [Fact]
    public void EnforceListenAnyPolicy_allows_startup_when_no_confirmation_is_required()
    {

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {

            ServeCommand command = new(
                new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors()),
                apiClient: null!);

            Assert.Null(command.EnforceListenAnyPolicy(requiresInteractiveConfirmation: false));

        }
        finally
        {

            AnsiConsole.Console = prior;

        }

    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    }

}
