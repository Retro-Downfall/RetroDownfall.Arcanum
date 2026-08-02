using System.Diagnostics.CodeAnalysis;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Commands.Wards;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

[ExcludeFromCodeCoverage] // System.CommandLine wiring factory; covered via CliApplicationFactoryTests.
internal static class CliApplicationFactory
{

    public static void ConfigureCliServices(IServiceCollection services, IConfiguration configuration)
    {

        ArcanumSettings settingsSnapshot =
            ConfigurationBootstrapper.LoadArcanumSettings(
                () => configuration.GetSection("Arcanum").Get<ArcanumSettings>()
                    ?? new ArcanumSettings());
        services.Configure<ArcanumSettings>(settings =>
            ConfigurationBootstrapper.CopySettings(settingsSnapshot, settings));

        services.AddArcanumThemeDetection();

        services.AddSingleton<ICliEnvironment>(sp =>
        {
            bool showManaBar = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Cli.ShowManaBar;

            return new CliEnvironment(
                showManaBar,
                sp.GetRequiredService<ICliInvocationContext>());
        });

        services.AddSingleton<IThemePalette>(sp =>
        {
            ArcanumSettings arc = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            CliSettings cli = arc.Cli;

            bool useDark = cli.Theme switch
            {
                ArcanumTheme.Light => false,
                ArcanumTheme.Dark => true,
                _ => sp.GetRequiredService<IThemeDetector>().SystemPrefersDark,
            };

            ThemeColors builtin = new();

            ThemeSemanticColors sem = useDark ? builtin.Dark : builtin.Light;

            return new ConfiguredThemePalette(sem, sem);
        });

        services.AddSingleton<CliSessionManager>();

        services.AddSingleton<ICliContextStore, CliContextStore>();

        services.AddSingleton<CliContextService>();

        services.AddSingleton<ICliContextService>(serviceProvider =>
            serviceProvider.GetRequiredService<CliContextService>());

        services.AddSingleton<ICliInferenceContextResolver, CliInferenceContextResolver>();

        services.AddSingleton<ICliInvocationContext, CliInvocationContext>();

        services.AddSingleton<IConsoleDispatcher, ConsoleDispatcher>();

        services.AddSingleton(new CliStandardInput(Console.In));

        services.AddSingleton<IConfirmationPrompt, ConfirmationPrompt>();

        services.AddSingleton<IResourcePicker, SpectreResourcePicker>();

        services.AddSingleton<IRecentResourceStore, RecentResourceStore>();

        services.AddSingleton<IAttachmentRevealLauncher, AttachmentRevealLauncher>();

        services.AddSingleton<TelemetryService>();

        services.AddSingleton<MarkdigSpectreRenderer>();

        // W6.4: shared secret/grimoire stack (Data Protection + digest cache + secret store +
        // CLI Grimoire), owned by Infrastructure so it cannot drift from the host wiring.
        services.AddArcanumCliClientStack();

        services.AddHttpClient(
            ArcanumApiClient.StreamingHttpClientName,
            (serviceProvider, client) =>
            {
                HostSettings host = serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Host;

                client.BaseAddress = new Uri(ArcanumLocalApiAddress.ResolveBaseUrl(host));

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddHttpClient(
            ArcanumApiClient.RequestHttpClientName,
            (serviceProvider, client) =>
            {
                ArcanumSettings settings = serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

                int timeoutSeconds = ArcanumSettingClamps.ApiRequestTimeoutSeconds(
                    ArcanumRuntimeDefaults.CliApiRequestTimeoutSeconds);

                client.BaseAddress = new Uri(ArcanumLocalApiAddress.ResolveBaseUrl(settings.Host));

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });

        services.AddSingleton<ArcanumApiClient>();

        services.AddSingleton<FileBatchApiClient>();

        services.AddSingleton<ConfigurationValidator>();

        services.AddSingleton<ConfigurationWriter>();

        services.AddSingleton<IConfigurationCommandService, ConfigurationCommandService>();

        services.AddSingleton<CompendiumLauncher>();

        services.AddSingleton<ICliResourceCatalog, CliResourceCatalog>();

        services.AddSingleton<IServeProcessLauncher, ServeProcessLauncher>();

        services.AddSingleton<IArcanumServeLauncher, ArcanumServeLauncher>();

        services.AddSingleton<ILastSessionStore, CliLastSessionStore>();

        services.AddTransient<SessionWorkspaceService>();

        services.AddSingleton<ShellCommandParser>();

        services.AddTransient<ShellCommandDispatcher>();

        services.AddSingleton<CommandCenterHardModalArbiter>();

        services.AddSingleton<CommandCenterWardCoordinator>();

        services.AddSingleton<CommandCenterHumanPromptCoordinator>();

        services.AddTransient<CommandCenterChatRunner>();

        services.AddTransient<CommandCenterAttachmentDriftMonitor>();

        services.AddTransient<CommandCenterApp>();

        services.AddTransient<ICommandCenterHost, CommandCenterHost>();

        services.AddArcanumEyeOfTheWorld();

        services.AddArcanumDaemonManagement();

        services.AddTransient<ServeCommand>();

        services.AddTransient<AskCommand>();

        services.AddTransient<ChatCommand>();

        services.AddTransient<LookCommand>();

        services.AddTransient<DoctorCommand>();

        services.AddTransient<KeyCommands>();

        services.AddTransient<LoreCommands>();

        services.AddTransient<DaemonCommands>();

        services.AddTransient<CampaignCommands>();

        services.AddTransient<CampaignCodexCommands>();

        services.AddTransient<SessionCommands>();

        services.AddTransient<SagaCommands>();

        services.AddTransient<MemoryCommands>();

        services.AddTransient<SpellCommands>();

        services.AddTransient<SpellVersionCommands>();

        services.AddTransient<PromptCommands>();

        services.AddTransient<WardCommands>();

        services.AddTransient<TrialCommands>();

        services.AddTransient<ApprenticeCommands>();

        services.AddTransient<ModelCommands>();

        services.AddTransient<ProviderCommands>();

        services.AddTransient<WorkspaceCommands>();

        services.AddTransient<McpCommands>();

        services.AddTransient<ToolCommands>();

        services.AddTransient<WebWorkflowCommands>();

        services.AddTransient<FileBatchCommands>();

        services.AddTransient<AttachmentCommands>();

        services.AddTransient<OperationCommands>();
        services.AddTransient<DataEncryptionCommands>();
        services.AddTransient<ContextCommands>();

        services.AddTransient<ConfigCommands>();

    }

    /// <summary>
    /// Runs the CLI end-to-end with System.CommandLine 2.0.
    /// Keeps the empty-args Command Center branch intact; non-empty args invoke CliCommandTree.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider serviceProvider)
    {

        CliInvocationOptions activeOptions = default;

        try
        {

            if (args.Length == 0)
            {
                using IDisposable emptyInvocationScope =
                    CliInvocationContext.Push(default);

                ICliEnvironment env = serviceProvider.GetRequiredService<ICliEnvironment>();

                if (env.IsInteractive && !CommandCenterHost.IsCommandCenterDisabled())
                {
                    ICommandCenterHost host = serviceProvider.GetRequiredService<ICommandCenterHost>();

                    int hostExitCode = await host
                        .RunAsync(CancellationToken.None)
                        .ConfigureAwait(false);

                    return NormalizeExitCode(hostExitCode);
                }

                WriteBareUsage();

                return (int)CliExitCode.Success;
            }

            RootCommand root = CliCommandTree.Build(
                serviceProvider,
                out CliGlobalOptions globalOptions);
            ParseResult parseResult = root.Parse(
                args,
                new ParserConfiguration
                {

                    ResponseFileTokenReplacer = null,

                });
            CliInvocationOptions options = new(
                parseResult.GetValue(globalOptions.Json),
                parseResult.GetValue(globalOptions.Plain),
                parseResult.GetValue(globalOptions.Yes),
                parseResult.GetValue(globalOptions.NoContext));
            activeOptions = options;

            using IDisposable invocationScope =
                CliInvocationContext.Push(options);

            if (parseResult.Errors.Count > 0 && options.Json)
            {
                IConsoleDispatcher dispatcher =
                    serviceProvider.GetRequiredService<IConsoleDispatcher>();
                dispatcher.WriteDiagnostic("The command line is invalid.");
                dispatcher.WriteJson(
                    new CliErrorPayload(
                        "The command line is invalid.",
                        (int)CliExitCode.ConfigurationError),
                    CliJsonContext.Default.CliErrorPayload);

                return (int)CliExitCode.ConfigurationError;
            }

            IAnsiConsole originalAnsiConsole = AnsiConsole.Console;
            TextWriter originalOutput = Console.Out;
            StringWriter? capturedOutput = options.Json ? new StringWriter() : null;

            try
            {
                if (capturedOutput is not null)
                {
                    Console.SetOut(capturedOutput);
                }

                ConfigureAnsiConsoleForInvocation(options);

                System.CommandLine.InvocationConfiguration config = new()
                {
                    EnableDefaultExceptionHandler = false,
                    ProcessTerminationTimeout = Timeout.InfiniteTimeSpan,
                    Output = Console.Out,
                    Error = Console.Error,
                };

                int exitCode = await parseResult
                    .InvokeAsync(config)
                    .ConfigureAwait(false);

                int normalizedExitCode = parseResult.Errors.Count > 0
                    ? (int)CliExitCode.ConfigurationError
                    : NormalizeExitCode(exitCode);

                if (capturedOutput is not null)
                {
                    FlushJsonOutput(
                        originalOutput,
                        capturedOutput.ToString(),
                        normalizedExitCode);
                }

                return normalizedExitCode;
            }
            finally
            {
                if (capturedOutput is not null)
                {
                    Console.SetOut(originalOutput);
                    capturedOutput.Dispose();
                }

                AnsiConsole.Console = originalAnsiConsole;
            }
        }
        catch (Exception exception)
        {
            CliFailure failure = CliFailureMapper.Map(exception);
            IConsoleDispatcher dispatcher =
                serviceProvider.GetRequiredService<IConsoleDispatcher>();

            if (activeOptions.Json)
            {
                dispatcher.WriteJson(
                    new CliErrorPayload(
                        failure.SafeMessage,
                        (int)failure.ExitCode),
                    CliJsonContext.Default.CliErrorPayload);
            }

            dispatcher.WriteDiagnostic(failure.SafeMessage);

            return (int)failure.ExitCode;
        }

    }

    private static void FlushJsonOutput(
        TextWriter output,
        string capturedOutput,
        int exitCode)
    {

        if (CliInvocationContext.StructuredPayloadWritten)
        {
            output.Write(capturedOutput);

            return;
        }

        string normalizedOutput =
            ConsoleDispatcher.StripAnsi(capturedOutput).TrimEnd('\r', '\n');
        string json = System.Text.Json.JsonSerializer.Serialize(
            new CliTextPayload(normalizedOutput, exitCode),
            CliJsonContext.Default.CliTextPayload);

        output.WriteLine(json);

    }

    private static int NormalizeExitCode(int exitCode) =>
        Enum.IsDefined((CliExitCode)exitCode)
            ? exitCode
            : (int)CliExitCode.GenericError;

    internal static void WriteBareUsage()
    {
        Console.WriteLine(
            """
            Arcanum CLI

              arcanum                 Open the Command Center (interactive TTY)
              arcanum <command> …     Run a direct command (script-safe, no TUI)

            Examples:
              arcanum chat
              arcanum ask "hello"
              arcanum doctor
              arcanum campaign list

            Escape hatches:
              ARCANUM_NO_COMMAND_CENTER=1   Print this usage instead of the TUI
              ARCANUM_NO_AUTO_SERVE=1       Disable auto-start of `arcanum serve`

            Run `arcanum --help` for the full command list.
            """);
    }

    public static void ConfigureAnsiConsoleForEnvironment(IConfiguration configuration)
    {

        bool noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR"));

        bool stdoutRedirected = Console.IsOutputRedirected;

        if (!noColor && !stdoutRedirected)
        {
            // Leave Spectre's auto-detected capabilities alone for normal interactive use.
            return;
        }

        AnsiSupport ansi = noColor || stdoutRedirected ? AnsiSupport.No : AnsiSupport.Detect;

        ColorSystemSupport colorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect;

        InteractionSupport interaction = stdoutRedirected ? InteractionSupport.No : InteractionSupport.Detect;

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi,
            ColorSystem = colorSystem,
            Interactive = interaction,
            Out = new AnsiConsoleOutput(Console.Out),
        });

        _ = configuration;

    }

    private static void ConfigureAnsiConsoleForInvocation(
        CliInvocationOptions options)
    {

        if (!options.Plain && !options.Json)
        {

            return;

        }

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(Console.Out),
        });

    }

}
