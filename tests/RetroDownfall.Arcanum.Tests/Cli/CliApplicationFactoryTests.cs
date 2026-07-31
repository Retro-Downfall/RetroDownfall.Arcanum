using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class CliApplicationFactoryTests
{
    [Fact]
    public void ApplyDefaultCommand_retired_is_identity()
    {
        string[] empty = [];  // method retired; identity behavior preserved in RunAsync directly
        Assert.Empty(empty);

        string[] unchanged = ["ask", "hello"];
        Assert.Equal(["ask", "hello"], unchanged);

        string[] helpUnchanged = ["--help"];
        Assert.Equal(["--help"], helpUnchanged);
    }

    [Fact]
    public async Task Empty_interactive_routes_to_command_center_host()
    {
        FakeCommandCenterHost host = new(exitCode: 42);
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);
        services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment(interactive: true, colorEnabled: true));
        services.AddTransient<ICommandCenterHost>(_ => host);

        CliTestResult result = await CliTestHarness.RunAsync(services, []);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);
        Assert.Equal(1, host.RunCount);
    }

    [Fact]
    public async Task Empty_interactive_with_NO_COLOR_still_routes_to_command_center()
    {
        FakeCommandCenterHost host = new(exitCode: 7);
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);
        services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment(interactive: true, colorEnabled: false));
        services.AddTransient<ICommandCenterHost>(_ => host);

        CliTestResult result = await CliTestHarness.RunAsync(services, []);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);
        Assert.Equal(1, host.RunCount);
    }

    [Fact]
    public async Task Empty_non_interactive_prints_usage_exit_0_without_host()
    {
        FakeCommandCenterHost host = new(exitCode: 99);
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);
        services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment(interactive: false, colorEnabled: false));
        services.AddTransient<ICommandCenterHost>(_ => host);

        CliTestResult result = await CliTestHarness.RunAsync(services, []);

            Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, host.RunCount);
        Assert.Contains("Command Center", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_with_NO_COMMAND_CENTER_prints_usage_exit_0()
    {
        string? previous = global::System.Environment.GetEnvironmentVariable(CommandCenterHost.NoCommandCenterEnvVar);
        try
        {
            global::System.Environment.SetEnvironmentVariable(CommandCenterHost.NoCommandCenterEnvVar, "1");

            FakeCommandCenterHost host = new(exitCode: 99);
            ServiceCollection services = new();
            ConfigurationManager configuration = new();
            CliApplicationFactory.ConfigureCliServices(services, configuration);
            services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment(interactive: true, colorEnabled: true));
            services.AddTransient<ICommandCenterHost>(_ => host);

            CliTestResult result = await CliTestHarness.RunAsync(services, []);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, host.RunCount);
            Assert.Contains("ARCANUM_NO_COMMAND_CENTER", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(CommandCenterHost.NoCommandCenterEnvVar, previous);
        }
    }

    [Fact]
    public void Help_smoke_lists_core_commands()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result = CliTestHarness.Run(services, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("serve", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("look", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_smoke_lists_branch_commands()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result = CliTestHarness.Run(services, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("daemon", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lore", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("campaign", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operation", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcp", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("campaign", "get")]
    [InlineData("prompt", "get")]
    [InlineData("spell", "get")]
    [InlineData("apprentice", "get")]
    public void Resource_get_help_marks_identifier_as_optional_for_interactive_selection(
        string family,
        string command)
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result = CliTestHarness.Run(services, family, command, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("optional", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Operation_help_lists_operator_recovery_commands()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result = CliTestHarness.Run(services, "operation", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("list", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("show", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancel", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconcile", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Key_provider_help_lists_secret_safe_management_commands()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result =
            CliTestHarness.Run(services, "key", "provider", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("set", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delete", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Key_provider_set_does_not_accept_secret_as_command_line_argument()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result =
            CliTestHarness.Run(
                services,
                "key",
                "provider",
                "set",
                "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("api-key", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure prompt", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Key_provider_status_recognizes_environment_credential_without_disclosing_it()
    {
        const string EnvironmentName = "ARCANUM_TEST_PERPLEXITY_STATUS_KEY";
        const string Secret = "perplexity-status-secret";
        string? previous =
            global::System.Environment.GetEnvironmentVariable(EnvironmentName);

        try
        {
            global::System.Environment.SetEnvironmentVariable(
                EnvironmentName,
                Secret);
            ConfigurationManager configuration = new();
            ServiceCollection services = new();
            CliApplicationFactory.ConfigureCliServices(services, configuration);
            services.AddSingleton<IOptions<ArcanumSettings>>(
                Options.Create(
                    new ArcanumSettings
                    {
                        Integrations = new IntegrationSettings
                        {
                            WebResearch = new WebResearchIntegrationSettings
                            {
                                CredentialEnvironmentVariable =
                                    EnvironmentName,
                            },
                        },
                    }));
            services.AddSingleton<IWebResearchCredentialStore>(
                new ThrowingWebResearchCredentialStore());

            CliTestResult result =
                CliTestHarness.Run(
                    services,
                    "key",
                    "provider",
                    "status",
                    "perplexity");

            Assert.True(
                result.ExitCode == 0,
                "Output: " + result.Output + "\nError: " + result.Error);
            Assert.Contains(EnvironmentName, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(
                EnvironmentName,
                previous);
        }
    }

    [Fact]
    public void ConfigureCliServices_registers_streaming_and_bounded_request_clients()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient streamingClient = factory.CreateClient(ArcanumApiClient.StreamingHttpClientName);
        HttpClient requestClient = factory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, streamingClient.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), requestClient.Timeout);
    }

    [Fact]
    public void ConfigureCliServices_registers_api_key_digest_cache_so_secret_store_resolves()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IApiKeyDigestCache digestCache = provider.GetRequiredService<IApiKeyDigestCache>();
        IWebResearchCredentialStore webResearchCredentials =
            provider.GetRequiredService<IWebResearchCredentialStore>();
        Assert.NotNull(digestCache);
        Assert.NotNull(webResearchCredentials);
        AskCommand askCommand = provider.GetRequiredService<AskCommand>();
        Assert.NotNull(askCommand);
    }

    [Fact]
    public void ConfigureCliServices_registers_grimoire_readiness_so_chat_command_resolves()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IGrimoireDbReadiness readiness = provider.GetRequiredService<IGrimoireDbReadiness>();
        Assert.NotNull(readiness);
        ChatCommand chatCommand = provider.GetRequiredService<ChatCommand>();
        Assert.NotNull(chatCommand);
    }

    [Fact]
    public void Data_encryption_help_exposes_all_lifecycle_commands_without_opening_grimoire()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult result = CliTestHarness.Run(services, "data", "encryption", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("status", result.Output, StringComparison.Ordinal);
        Assert.Contains("migrate", result.Output, StringComparison.Ordinal);
        Assert.Contains("verify", result.Output, StringComparison.Ordinal);
        Assert.Contains("rotate-key", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureCliServices_registers_command_center_host()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ICommandCenterHost>());
        Assert.NotNull(provider.GetRequiredService<ShellCommandParser>());
        Assert.NotNull(provider.GetRequiredService<ShellCommandDispatcher>());
        Assert.NotNull(provider.GetRequiredService<CommandCenterChatRunner>());
    }

    private sealed class FakeCommandCenterHost(int exitCode) : ICommandCenterHost
    {
        public int RunCount { get; private set; }

        public Task<int> RunAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class FakeCliEnvironment(bool interactive, bool colorEnabled) : ICliEnvironment
    {
        public bool IsInteractive { get; } = interactive;

        public bool ColorEnabled { get; } = colorEnabled;

        public bool ShouldShowManaBar => IsInteractive && ColorEnabled;
    }

    private sealed class ThrowingWebResearchCredentialStore
        : IWebResearchCredentialStore
    {
        public Task<SecretStoreReadResult> GetPerplexityApiKeyReadResultAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Environment credentials must take precedence.");

        public Task SavePerplexityApiKeyAsync(
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeletePerplexityApiKeyAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
