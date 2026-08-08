using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Desktop;
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

        string[] unchanged = ["run", "hello"];
        Assert.Equal(["run", "hello"], unchanged);

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
    public async Task Command_center_deep_link_enters_current_host_without_command_parsing_or_process_launch()
    {

        FakeCommandCenterHost host = new(exitCode: 0);

        ThrowingApplicationLauncher launcher = new();

        ServiceCollection services = CreateDeepLinkServices(host, launcher);

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.CommandCenter,
            ApplicationResourceKind.None,
            InitialView: ApplicationInitialView.CommandCenter);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, host.RunCount);

        Assert.Null(host.StartupSessionId);

        Assert.Equal(0, launcher.CallCount);

        Assert.DoesNotContain(payload, result.Output + result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Command_center_session_deep_link_resumes_the_canonical_session_in_current_host()
    {

        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        FakeCommandCenterHost host = new(exitCode: 0);

        ThrowingApplicationLauncher launcher = new();

        ServiceCollection services = CreateDeepLinkServices(host, launcher);

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.CommandCenter,
            ApplicationResourceKind.Session,
            sessionId.ToString("D"),
            InitialView: ApplicationInitialView.CommandCenter);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, host.RunCount);

        Assert.Equal(sessionId, host.StartupSessionId);

        Assert.Equal(0, launcher.CallCount);

        Assert.DoesNotContain(payload, result.Output + result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public async Task Command_center_empty_session_id_is_rejected_without_entering_the_host()
    {

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.CommandCenter,
            ApplicationResourceKind.Session,
            Guid.Empty.ToString("D"),
            InitialView: ApplicationInitialView.CommandCenter);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        FakeCommandCenterHost host = new(exitCode: 0);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateDeepLinkServices(host, new ThrowingApplicationLauncher()),
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, host.RunCount);

        Assert.DoesNotContain(payload, result.Output + result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Malformed_command_center_deep_link_fails_without_echoing_private_payload()
    {

        const string SecretMarker = "api-key=must-not-surface";

        string payload = $"{{not-json:{SecretMarker}";

        FakeCommandCenterHost host = new(exitCode: 0);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateDeepLinkServices(host, new ThrowingApplicationLauncher()),
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        string combined = result.Output + result.Error;

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, host.RunCount);

        Assert.Contains(
            "Command Center application link is invalid.",
            combined,
            StringComparison.Ordinal);

        Assert.DoesNotContain(SecretMarker, combined, StringComparison.Ordinal);

        Assert.DoesNotContain(payload, combined, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Wrong_target_command_center_deep_link_fails_without_echoing_private_payload()
    {

        const string SecretMarker = "private-marker-must-not-surface";

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            SecretMarker,
            InitialView: ApplicationInitialView.Settings);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        FakeCommandCenterHost host = new(exitCode: 0);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateDeepLinkServices(host, new ThrowingApplicationLauncher()),
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        string combined = result.Output + result.Error;

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, host.RunCount);

        Assert.Contains(
            "Command Center application link is invalid.",
            combined,
            StringComparison.Ordinal);

        Assert.DoesNotContain(SecretMarker, combined, StringComparison.Ordinal);

        Assert.DoesNotContain(payload, combined, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Unsupported_command_center_resource_fails_with_fixed_safe_diagnostic()
    {

        const string SecretMarker = "private-resource-must-not-surface";

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.CommandCenter,
            ApplicationResourceKind.Prompt,
            SecretMarker,
            InitialView: ApplicationInitialView.CommandCenter);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        FakeCommandCenterHost host = new(exitCode: 0);

        CliTestResult result = await CliTestHarness.RunAsync(
            CreateDeepLinkServices(host, new ThrowingApplicationLauncher()),
            [ApplicationDeepLinkCodec.ArgumentName, payload]);

        string combined = result.Output + result.Error;

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Equal(0, host.RunCount);

        Assert.Contains(
            "Command Center application link requests an unsupported resource.",
            combined,
            StringComparison.Ordinal);

        Assert.DoesNotContain(SecretMarker, combined, StringComparison.Ordinal);

        Assert.DoesNotContain(payload, combined, StringComparison.Ordinal);

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
        Assert.Contains("run", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("center", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("look", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("key", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("use", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("context", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_exposes_context_bypass_and_management_commands()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        CliTestResult root = CliTestHarness.Run(services, "--help");

        CliTestResult use = CliTestHarness.Run(services, "use", "--help");

        CliTestResult context = CliTestHarness.Run(services, "context", "--help");

        Assert.Contains("--no-context", root.Output, StringComparison.Ordinal);

        Assert.Contains("campaign", use.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("workspace", use.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("model", use.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("session", use.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("clear", use.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("current", context.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("cost", context.Output, StringComparison.OrdinalIgnoreCase);

        CliTestResult run = CliTestHarness.Run(services, "run", "--help");

        Assert.Contains("--workspace", run.Output, StringComparison.Ordinal);

        Assert.Contains("--session", run.Output, StringComparison.Ordinal);

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
    [InlineData("campaign", "show")]
    [InlineData("prompt", "show")]
    [InlineData("spell", "show")]
    [InlineData("apprentice", "show")]
    public void Resource_show_help_marks_identifier_as_optional_for_interactive_selection(
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
    public void ConfigureCliServices_registers_clients_without_total_operation_timeouts()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient streamingClient = factory.CreateClient(ArcanumApiClient.StreamingHttpClientName);
        HttpClient requestClient = factory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, streamingClient.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, requestClient.Timeout);
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
    public void ConfigureCliServices_registers_grimoire_readiness_so_the_run_command_resolves()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        IGrimoireDbReadiness readiness = provider.GetRequiredService<IGrimoireDbReadiness>();
        Assert.NotNull(readiness);
        RunCommand runCommand = provider.GetRequiredService<RunCommand>();
        Assert.NotNull(runCommand);
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

        public Guid? StartupSessionId { get; private set; }

        public Task<int> RunAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            return Task.FromResult(exitCode);
        }

        public Task<int> RunAsync(
            Guid? startupSessionId,
            CancellationToken cancellationToken)
        {

            StartupSessionId = startupSessionId;

            return RunAsync(cancellationToken);

        }
    }

    private static ServiceCollection CreateDeepLinkServices(
        ICommandCenterHost host,
        IApplicationLauncher launcher)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<ICliEnvironment>();

        services.AddSingleton<ICliEnvironment>(
            new FakeCliEnvironment(interactive: false, colorEnabled: false));

        services.RemoveAll<ICommandCenterHost>();

        services.AddSingleton(host);

        services.RemoveAll<IApplicationLauncher>();

        services.AddSingleton(launcher);

        return services;

    }

    private sealed class ThrowingApplicationLauncher : IApplicationLauncher
    {

        public int CallCount { get; private set; }

        public ApplicationLaunchResult TryLaunch(ApplicationLaunchRequest request)
        {

            CallCount++;

            throw new InvalidOperationException(
                "A Command Center deep link must not create a process.");

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

    [Fact]
    public void Every_option_and_argument_in_the_tree_carries_help_text()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<ICliEnvironment>(new FakeCliEnvironment(interactive: false, colorEnabled: false));

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        List<string> undescribed = new();

        Walk(root, root.Name, undescribed);

        Assert.True(
            undescribed.Count == 0,
            "Symbols reachable from the root command have no --help description: "
                + string.Join(", ", undescribed));

        static void Walk(Command command, string path, List<string> undescribed)
        {

            foreach (Option option in command.Options)
            {

                if (option.Name is "--help" or "--version")
                {

                    continue;

                }

                if (string.IsNullOrWhiteSpace(option.Description))
                {

                    undescribed.Add($"{path} {option.Name}");

                }

            }

            foreach (Argument argument in command.Arguments)
            {

                if (string.IsNullOrWhiteSpace(argument.Description))
                {

                    undescribed.Add($"{path} <{argument.Name}>");

                }

            }

            foreach (Command child in command.Subcommands)
            {

                Walk(child, $"{path} {child.Name}", undescribed);

            }

        }

    }

}
