using System.Diagnostics.CodeAnalysis;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.TheForge;
using RetroDownfall.Arcanum.Cli.Commands.Wards;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Services.Setup;
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

    /// <summary>
    /// The persisted file is the authority, parsed through <c>ConfigurationJsonContext</c> exactly as
    /// the host does (<c>AddArcanumInfrastructure</c>). The configuration binder is a missing-file
    /// fallback only: it cannot honour <c>ModelEntryJsonConverter</c> (bare-string model entries are
    /// silently dropped) and it appends to array defaults instead of replacing them. A file that
    /// cannot be parsed at all degrades to defaults rather than taking down DI composition —
    /// <c>Program.Main</c> has already reported it, and only configuration repair and diagnosis verbs
    /// get this far; they need a usable service graph to name the offending pointer.
    /// </summary>
    private static ArcanumSettings LoadSettingsSnapshot(IConfiguration configuration)
    {

        try
        {

            return ConfigurationBootstrapper.LoadArcanumSettings(
                () => configuration.GetSection("Arcanum").Get<ArcanumSettings>() ?? new ArcanumSettings());

        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {

            return configuration.GetSection("Arcanum").Get<ArcanumSettings>() ?? new ArcanumSettings();

        }

    }

    public static void ConfigureCliServices(IServiceCollection services, IConfiguration configuration)
    {

        ArcanumSettings settingsSnapshot = LoadSettingsSnapshot(configuration);

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

        services.AddSingleton<IBackupPassphraseReader, BackupPassphraseReader>();

        services.AddSingleton<IResourcePicker, SpectreResourcePicker>();

        services.AddSingleton<IRecentResourceStore, RecentResourceStore>();

        services.AddSingleton<IAttachmentRevealLauncher, AttachmentRevealLauncher>();

        services.AddSingleton<TelemetryService>();

        services.AddSingleton<MarkdigSpectreRenderer>();

        // W6.4: shared secret/grimoire stack (Data Protection + digest cache + secret store +
        // CLI Grimoire), owned by Infrastructure so it cannot drift from the host wiring.
        services.AddArcanumCliClientStack();

        services.AddArcanumConfigurationPresets();

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

                client.BaseAddress = new Uri(ArcanumLocalApiAddress.ResolveBaseUrl(settings.Host));

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<ArcanumApiClient>();

        services.AddSingleton<FileBatchApiClient>();

        services.AddSingleton<IConfigurationCommandService, ConfigurationCommandService>();

        services.AddSingleton<CompendiumLauncher>();

        services.AddSingleton<IApplicationDiscoveryService>(
            static _ => ApplicationDiscoveryServiceFactory.CreateDefault());

        services.AddSingleton<IApplicationProcessStarter, ApplicationProcessStarter>();

        services.AddSingleton<IApplicationLauncher, ApplicationLauncher>();

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

        services.AddSingleton<ISetupProbeHandlerFactory, SetupProbeHandlerFactory>();

        services.AddSingleton<ISetupProviderProbe, SetupProviderProbe>();

        services.AddSingleton<ISetupPrompt, ConsoleSetupPrompt>();

        services.AddTransient<ISetupPlanner, SetupPlanner>();

        services.AddTransient<ISetupCommitter, SetupCommitter>();

        services.AddTransient<SetupCommand>();

        services.AddTransient<ServeCommand>();

        services.AddTransient<AskCommand>();

        services.AddTransient<RunCommand>();

        services.AddTransient<IRunInputReader, RunInputReader>();

        services.AddTransient<IRunAttachmentStager, RunAttachmentStager>();

        services.AddTransient<IRunExecutionDispatcher, RunExecutionDispatcher>();

        services.AddTransient<ChatCommand>();

        services.AddTransient<LookCommand>();

        services.AddArcanumDoctorDiagnostics();

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
        services.AddTransient<ConclaveCommands>();

        services.AddTransient<BackupCommands>();

        services.AddTransient<DataEncryptionCommands>();

        services.AddTransient<DataRetentionCommands>();

        services.AddTransient<ContextCommands>();

        services.AddTransient<WatchCommands>();

        services.AddTransient<ConfigCommands>();

        services.AddTransient<PresetCommands>();

        services.AddTransient<OpenCommands>();

    }

    /// <summary>
    /// Runs the CLI end-to-end with System.CommandLine 2.0.
    /// Keeps the empty-args Command Center branch intact; non-empty args invoke CliCommandTree.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider serviceProvider)
    {

        CliInvocationOptions activeOptions = default;

        DeferredJsonTextWriter? activeJsonOutput = null;

        try
        {

            CommandCenterDeepLinkIntake deepLinkIntake =
                ParseCommandCenterDeepLink(args);

            args = deepLinkIntake.RemainingArguments;

            if (deepLinkIntake.Status != CommandCenterDeepLinkStatus.Absent)
            {

                using IDisposable deepLinkInvocationScope =
                    CliInvocationContext.Push(default);

                if (deepLinkIntake.Status != CommandCenterDeepLinkStatus.Valid)
                {

                    IConsoleDispatcher dispatcher =
                        serviceProvider.GetRequiredService<IConsoleDispatcher>();

                    dispatcher.WriteDiagnostic(
                        deepLinkIntake.Status == CommandCenterDeepLinkStatus.Unsupported
                            ? "Command Center application link requests an unsupported resource."
                            : "Command Center application link is invalid.");

                    return (int)CliExitCode.ConfigurationError;

                }

                ICommandCenterHost host =
                    serviceProvider.GetRequiredService<ICommandCenterHost>();

                int hostExitCode = await host
                    .RunAsync(
                        deepLinkIntake.StartupSessionId,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                return NormalizeExitCode(hostExitCode);

            }

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

                if (!IsJsonStreamInvocation(parseResult))
                {

                    dispatcher.WriteJson(
                        new CliErrorPayload(
                            "The command line is invalid.",
                            (int)CliExitCode.ConfigurationError),
                        CliJsonContext.Default.CliErrorPayload);

                }

                return (int)CliExitCode.ConfigurationError;
            }

            IAnsiConsole originalAnsiConsole = AnsiConsole.Console;
            TextWriter originalOutput = Console.Out;
            DeferredJsonTextWriter? capturedOutput = options.Json
                ? new DeferredJsonTextWriter(originalOutput)
                : null;

            activeJsonOutput = capturedOutput;

            if (capturedOutput is not null)
            {

                CliInvocationContext.AttachJsonOutput(capturedOutput);

            }

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
                    ProcessTerminationTimeout = ResolveProcessTerminationTimeout(parseResult),
                    Output = Console.Out,
                    Error = Console.Error,
                };

                int exitCode = await parseResult
                    .InvokeAsync(config)
                    .ConfigureAwait(false);

                int normalizedExitCode = parseResult.Errors.Count > 0
                    ? (int)CliExitCode.ConfigurationError
                    : NormalizeExitCode(exitCode);

                if (capturedOutput is { IsStreaming: false })
                {
                    FlushJsonOutput(
                        originalOutput,
                        capturedOutput.BufferedOutput,
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

            if (activeOptions.Json
                && activeJsonOutput is not { IsStreaming: true })
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

    /// <summary>
    /// Grace window System.CommandLine allows a command to observe SIGINT and unwind before the
    /// process is terminated. This is not a duration cap on the command itself (DESIGN 2.1 — Arcanum
    /// owns no total duration); it only bounds how long a Ctrl+C waits for cooperative shutdown, so
    /// a command that never observes the token can still be stopped from the keyboard.
    /// </summary>
    internal static readonly TimeSpan ProcessTerminationGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Commands that install their own <see cref="Console.CancelKeyPress"/> contract and must never
    /// be torn down by System.CommandLine's process-termination handler. <c>chat</c> maps Ctrl+C to
    /// "cancel this turn / clear this line", so a termination handler would kill the REPL instead.
    /// </summary>
    private static readonly string[] SelfManagedTerminationCommands = ["chat"];

    internal static TimeSpan? ResolveProcessTerminationTimeout(ParseResult parseResult) =>
        OwnsCancelKeyPress(parseResult) ? null : ProcessTerminationGrace;

    private static bool OwnsCancelKeyPress(ParseResult parseResult)
    {

        for (SymbolResult? current = parseResult.CommandResult;
            current is not null;
            current = current.Parent)
        {

            if (current is CommandResult commandResult
                && SelfManagedTerminationCommands.Contains(commandResult.Command.Name, StringComparer.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

    private static bool IsJsonStreamInvocation(ParseResult parseResult)
    {

        for (SymbolResult? current = parseResult.CommandResult;
            current is not null;
            current = current.Parent)
        {

            if (current is not CommandResult commandResult)
            {

                continue;

            }

            if (string.Equals(
                    commandResult.Command.Name,
                    "watch",
                    StringComparison.Ordinal)
                && commandResult.Parent is CommandResult watchParent
                && (watchParent.Parent is null
                    || string.Equals(
                        watchParent.Command.Name,
                        "session",
                        StringComparison.Ordinal)))
            {

                return true;

            }

            if (string.Equals(
                    commandResult.Command.Name,
                    "chronicle",
                    StringComparison.Ordinal)
                && commandResult.Parent is CommandResult parent
                && string.Equals(
                    parent.Command.Name,
                    "apprentice",
                    StringComparison.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

    private static int NormalizeExitCode(int exitCode) =>
        Enum.IsDefined((CliExitCode)exitCode)
            ? exitCode
            : (int)CliExitCode.GenericError;

    private static CommandCenterDeepLinkIntake ParseCommandCenterDeepLink(
        IReadOnlyList<string> arguments)
    {

        if (arguments.Count == 0
            || !string.Equals(
                arguments[0],
                ApplicationDeepLinkCodec.ArgumentName,
                StringComparison.Ordinal))
        {

            return new CommandCenterDeepLinkIntake(
                CommandCenterDeepLinkStatus.Absent,
                [.. arguments]);

        }

        if (arguments.Count == 1)
        {

            return new CommandCenterDeepLinkIntake(
                CommandCenterDeepLinkStatus.Invalid,
                []);

        }

        string[] remainingArguments = [.. arguments.Skip(2)];

        ApplicationDeepLink deepLink;

        try
        {

            deepLink = ApplicationDeepLinkCodec.Decode(
                arguments[1]);

        }
        catch
        {

            return new CommandCenterDeepLinkIntake(
                CommandCenterDeepLinkStatus.Invalid,
                remainingArguments);

        }

        if (deepLink.TargetApplication != DesktopApplication.CommandCenter)
        {

            return new CommandCenterDeepLinkIntake(
                CommandCenterDeepLinkStatus.Invalid,
                remainingArguments);

        }

        if (deepLink.ResourceKind == ApplicationResourceKind.None)
        {

            return new CommandCenterDeepLinkIntake(
                CommandCenterDeepLinkStatus.Valid,
                remainingArguments);

        }

        if (deepLink.ResourceKind == ApplicationResourceKind.Session)
        {

            return Guid.TryParse(deepLink.ResourceId, out Guid sessionId)
                && sessionId != Guid.Empty
                ? new CommandCenterDeepLinkIntake(
                    CommandCenterDeepLinkStatus.Valid,
                    remainingArguments,
                    sessionId)
                : new CommandCenterDeepLinkIntake(
                    CommandCenterDeepLinkStatus.Invalid,
                    remainingArguments);

        }

        return new CommandCenterDeepLinkIntake(
            CommandCenterDeepLinkStatus.Unsupported,
            remainingArguments);

    }

    private enum CommandCenterDeepLinkStatus
    {

        Absent = 0,

        Valid = 1,

        Invalid = 2,

        Unsupported = 3,

    }

    private sealed record CommandCenterDeepLinkIntake(
        CommandCenterDeepLinkStatus Status,
        string[] RemainingArguments,
        Guid? StartupSessionId = null);

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
