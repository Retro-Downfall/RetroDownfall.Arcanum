using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using System.Diagnostics;

using RetroDownfall.Arcanum.Cli.Commands.Configuration;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class ConfigCommandTests
{

    [Fact]

    public void Editor_command_preserves_configured_arguments_without_using_a_shell()
    {

        ProcessStartInfo startInfo = ConfigEditor.CreateStartInfo(
            "code --wait",
            "/tmp/arcanum.json");

        Assert.Equal("code", startInfo.FileName);

        Assert.False(startInfo.UseShellExecute);

        Assert.Equal(["--wait", "/tmp/arcanum.json"], startInfo.ArgumentList);

    }

    [Fact]

    public void Help_exposes_complete_config_command_family()
    {

        ServiceCollection services = Services();

        CliTestResult result = CliTestHarness.Run(services, "config", "--help");

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("path", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("show", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("get", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("set", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("validate", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("edit", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("open", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Path_prints_exact_configuration_path()
    {

        ServiceCollection services = Services();

        CliTestResult result = CliTestHarness.Run(services, "config", "path", "--plain");

        Assert.Equal(0, result.ExitCode);

        Assert.EndsWith("arcanum.json", result.Output.Trim(), StringComparison.Ordinal);

    }

    [Fact]

    public void Show_redacts_sensitive_values_and_identifies_local_bootstrap()
    {

        FakeConfigurationCommandService fake = new(
            new ArcanumSettings
            {

                Providers =
                [
                    new ProviderSettings
                    {

                        Name = "openai",

                        Endpoint = "https://secret-endpoint.example/v1",

                    },
                ],

            },
            ConfigurationAccessMode.LocalBootstrap);

        ServiceCollection services = Services(fake);

        CliTestResult result = CliTestHarness.Run(services, "config", "show", "--plain");

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("***", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("secret-endpoint", result.Output, StringComparison.Ordinal);

        Assert.Contains("local configuration bootstrap", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Set_parses_validates_writes_and_displays_effective_value()
    {

        FakeConfigurationCommandService fake = new(
            new ArcanumSettings(),
            ConfigurationAccessMode.HostApi);

        ServiceCollection services = Services(fake);

        CliTestResult result = CliTestHarness.Run(
            services,
            "config",
            "set",
            "host.port",
            "6123",
            "--plain");

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(6123, fake.Written!.Host.Port);

        Assert.Equal(1, fake.ValidateCount);

        Assert.Contains("host.port = 6123", result.Output, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("security.ward.enabled", "true")]

    [InlineData("security.ward.autoDenyInUnattendedMode", "true")]

    [InlineData("security.ward.autoApprove", "true")]

    [InlineData("security.ward.autoApprove.tools", "apply_patch")]

    public void Set_rejects_removed_ward_approval_paths_with_actionable_diagnostics(
        string key,
        string value)
    {

        FakeConfigurationCommandService fake = new(
            new ArcanumSettings(),
            ConfigurationAccessMode.HostApi);

        ServiceCollection services = Services(fake);

        CliTestResult result = CliTestHarness.Run(
            services,
            "config",
            "set",
            key,
            value,
            "--plain");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Null(fake.Written);

        Assert.Equal(0, fake.ValidateCount);

        Assert.Contains(key, result.Error, StringComparison.Ordinal);

        Assert.Contains("was removed", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Remove this Ward approval setting", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Set_rejects_sensitive_argv_without_echoing_value()
    {

        FakeConfigurationCommandService fake = new(
            new ArcanumSettings
            {

                Providers =
                [
                    new ProviderSettings
                    {

                        Name = "openai",

                        Endpoint = "https://old.example/v1",

                    },
                ],

            },
            ConfigurationAccessMode.HostApi);

        ServiceCollection services = Services(fake);

        const string secret = "https://do-not-echo.example/v1";

        CliTestResult result = CliTestHarness.Run(
            services,
            "config",
            "set",
            "providers.0.endpoint",
            secret,
            "--plain");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Null(fake.Written);

        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);

        Assert.Contains("must not be passed", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Set_reads_sensitive_value_from_stdin_and_only_displays_mask()
    {

        FakeConfigurationCommandService fake = new(
            new ArcanumSettings
            {

                Providers =
                [
                    new ProviderSettings
                    {

                        Name = "openai",

                        Endpoint = "https://old.example/v1",

                    },
                ],

            },
            ConfigurationAccessMode.HostApi);

        ServiceCollection services = Services(fake);

        const string secret = "https://new.example/v1";

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["config", "set", "providers.0.endpoint", "--plain"],
            input: secret + System.Environment.NewLine);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(secret, fake.Written!.Providers[0].Endpoint);

        Assert.Contains("providers.0.endpoint = ***", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Edit_preserves_masks_for_host_to_merge_against_authoritative_secrets()
    {

        ArcanumSettings masked = SettingsWithEndpoint("***");

        ConfigurationCommandSnapshot snapshot = new(
            masked,
            ConfigurationAccessMode.HostApi,
            []);

        ConfigurationPathUpdate result = ConfigCommands.PrepareEditedSettings(
            snapshot,
            masked);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal("***", result.Settings!.Providers[0].Endpoint);

    }

    [Fact]

    public void Edit_restores_local_masks_before_validation_and_write()
    {

        ConfigurationCommandSnapshot snapshot = new(
            SettingsWithEndpoint("https://local.example/v1"),
            ConfigurationAccessMode.LocalBootstrap,
            []);

        ConfigurationPathUpdate result = ConfigCommands.PrepareEditedSettings(
            snapshot,
            SettingsWithEndpoint("***"));

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal("https://local.example/v1", result.Settings!.Providers[0].Endpoint);

    }

    private static ArcanumSettings SettingsWithEndpoint(string endpoint) =>
        new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "openai",

                    Endpoint = endpoint,

                },
            ],

        };

    private static ServiceCollection Services(
        IConfigurationCommandService? configurationService = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        if (configurationService is not null)
        {

            services.AddSingleton(configurationService);

        }

        return services;

    }

    private sealed class FakeConfigurationCommandService(
        ArcanumSettings settings,
        ConfigurationAccessMode accessMode) : IConfigurationCommandService
    {

        public string ConfigurationPath => "/tmp/arcanum.json";

        public ArcanumSettings? Written { get; private set; }

        public int ValidateCount { get; private set; }

        public Task<Result<ConfigurationCommandSnapshot>> ReadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<ConfigurationCommandSnapshot>.Success(
                    new ConfigurationCommandSnapshot(
                        settings,
                        accessMode,
                        [])));

        public Task<Result> ValidateAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings candidate,
            CancellationToken cancellationToken)
        {

            ValidateCount++;

            return Task.FromResult(Result.Success());

        }

        public async Task<Result> WriteAsync(
            ConfigurationCommandSnapshot snapshot,
            ArcanumSettings candidate,
            CancellationToken cancellationToken)
        {

            Result validation = await ValidateAsync(
                snapshot,
                candidate,
                cancellationToken);

            if (validation.IsSuccess)
            {

                Written = candidate;

            }

            return validation;

        }

    }

}
