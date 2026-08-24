using System.Diagnostics.CodeAnalysis;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Commands.Conclave;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Commands.Configuration;
using RetroDownfall.Arcanum.Cli.Commands.Daemon;
using RetroDownfall.Arcanum.Cli.Commands.Lore;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Commands.Wards;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
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

        services.AddSingleton<CliContextStore>();

        services.AddSingleton<ICliContextStore>(serviceProvider =>
            serviceProvider.GetRequiredService<CliContextStore>());

        services.AddSingleton<ICliContextExclusiveWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<CliContextStore>());

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

        services.AddArcanumInstallationReset(settingsSnapshot);

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

        services.AddSingleton<ConfigurationCommandService>();

        services.AddSingleton<IConfigurationCommandService>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfigurationCommandService>());

        services.AddSingleton<IConfigurationCommandExclusiveWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfigurationCommandService>());

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

        services.AddTransient<ISetupCommand>(serviceProvider =>
            serviceProvider.GetRequiredService<SetupCommand>());

        services.AddTransient<ServeCommand>();

        services.AddTransient<AskCommand>();

        services.AddTransient<RunCommand>();

        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ICliCompletionResolver, CliCompletionResolver>();

        services.AddTransient<CompletionCommands>();

        services.AddTransient<HelpTopicCommands>();

        services.AddTransient<IRunInputReader, RunInputReader>();

        services.AddTransient<IRunAttachmentStager, RunAttachmentStager>();

        services.AddTransient<IRunExecutionDispatcher, RunExecutionDispatcher>();

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

        services.AddTransient<CovenantCommands>();

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
        services.AddTransient<BudgetCommands>();

        services.AddTransient<BackupCommands>();

        services.AddTransient<DataEncryptionCommands>();

        services.AddTransient<DataRetentionCommands>();

        services.AddSingleton<CovenantExternalRetentionDisclosureWriter>();

        services.AddTransient<InstallationFactoryResetCommand>();

        services.AddSingleton<IInstallationResetConfirmationPrompt, InstallationResetConfirmationPrompt>();

        services.AddSingleton<
            IFullInstallationResetAttestationFileReader,
            FullInstallationResetAttestationFileReader>();

        services.AddScoped<IInstallationResetApplyBoundary, InstallationResetApplyBoundary>();

        services.AddScoped<IInstallationResetOnlinePlanValidator, InstallationResetOnlinePlanValidator>();

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
            string? requestedFormat = ReadGlobalOption(parseResult, globalOptions.OutputFormat);

            bool jsonShorthand = ReadGlobalOption(parseResult, globalOptions.Json);

            CliInvocationOptions options = new(
                ResolveJsonOutput(requestedFormat, jsonShorthand),
                ReadGlobalOption(parseResult, globalOptions.Plain),
                ReadGlobalOption(parseResult, globalOptions.Yes),
                ReadGlobalOption(parseResult, globalOptions.NoContext),
                ReadGlobalOption(parseResult, globalOptions.Print),
                ReadGlobalOption(parseResult, globalOptions.Verbose));
            activeOptions = options;

            using IDisposable invocationScope =
                CliInvocationContext.Push(options);

            // --json is shorthand for --output-format json, so pairing it with an explicit text
            // format is a contradiction rather than a precedence question. Failing here keeps the
            // one-document stdout contract unambiguous for scripts.
            if (jsonShorthand
                && string.Equals(requestedFormat, "text", StringComparison.Ordinal))
            {

                IConsoleDispatcher dispatcher =
                    serviceProvider.GetRequiredService<IConsoleDispatcher>();

                dispatcher.WriteDiagnostic(
                    "--json is shorthand for --output-format json and cannot be combined with --output-format text.");

                return (int)CliExitCode.ConfigurationError;

            }

            // A removed spelling or a typo must name the canonical command. With no alias layer,
            // this diagnostic is the whole migration path, so it is emitted before the generic
            // parse-error handling in both text and JSON modes.
            string? suggestion = parseResult.Errors.Count > 0
                ? CliSuggestionEngine.Describe(parseResult, args)
                : null;

            if (parseResult.Errors.Count > 0 && options.Json)
            {
                IConsoleDispatcher dispatcher =
                    serviceProvider.GetRequiredService<IConsoleDispatcher>();

                if (suggestion is not null)
                {

                    dispatcher.WriteDiagnostic(suggestion);

                }

                dispatcher.WriteDiagnostic("The command line is invalid.");

                if (!IsJsonStreamInvocation(parseResult))
                {

                    dispatcher.WriteJson(
                        new CliErrorPayload(
                            suggestion ?? "The command line is invalid.",
                            (int)CliExitCode.ConfigurationError),
                        CliJsonContext.Default.CliErrorPayload);

                }

                return (int)CliExitCode.ConfigurationError;
            }

            // Returning here suppresses System.CommandLine's own error rendering. That output would
            // otherwise bury the actionable line under a full help dump plus its own nearest-name
            // guess, which for a removed spelling is actively misleading — `mana` invites "did you
            // mean saga, data" when the real answer is `context cost`. One correct sentence and
            // exit 2 is the whole contract.
            if (suggestion is not null)
            {

                serviceProvider
                    .GetRequiredService<IConsoleDispatcher>()
                    .WriteDiagnostic(suggestion);

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

                // Appended after invocation rather than from inside the help action: HelpAction is
                // sealed, and its Terminating/ClearsParseErrors flags are read-only, so a wrapper
                // or subclass cannot preserve the exit-0-under---help contract for commands with
                // required arguments. Writing to config.Output keeps examples inside the same
                // captured stream as the rest of help, so `--help --json` stays one document.
                CliHelpExamples.Append(root, parseResult, config.Output);

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
    /// be torn down by System.CommandLine's process-termination handler. Command Center is entered
    /// before parsing, so no parsed verb currently claims the keypress; the hook stays because any
    /// future long-lived interactive verb needs it and silently inheriting the termination handler
    /// would kill that verb's own Ctrl+C contract.
    /// </summary>
    private static readonly string[] SelfManagedTerminationCommands = [];

    internal static TimeSpan? ResolveProcessTerminationTimeout(ParseResult parseResult) =>
        OwnsCancelKeyPress(parseResult) ? null : ProcessTerminationGrace;

    private static bool OwnsCancelKeyPress(ParseResult parseResult)
    {

        if (SelfManagedTerminationCommands.Length == 0)
        {

            return false;

        }

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

    /// <summary>
    /// Reads a recursive global option without letting its own value failure escape. A rejected,
    /// missing, or repeated value makes <c>GetValue</c> throw rather than yield the default, and that
    /// throw lands before the invocation options are known — so an invalid <c>--output-format</c>
    /// surfaced as the generic exit 1 "unexpected CLI error" with no JSON envelope for scripts.
    /// Falling back to the default lets the parse-error handling below own the outcome: the real
    /// System.CommandLine message, one <see cref="CliErrorPayload"/> document under <c>--json</c>,
    /// and the documented exit 2 for an invalid command line.
    /// </summary>
    private static T? ReadGlobalOption<T>(ParseResult parseResult, Option<T> option)
    {

        try
        {

            return parseResult.GetValue(option);

        }
        catch (InvalidOperationException)
        {

            return default;

        }

    }

    /// <summary>
    /// One switch drives the structured-output contract. <c>--output-format json</c> is the
    /// Claude-compatible spelling and <c>--json</c> is its shorthand; either alone selects JSON.
    /// </summary>
    private static bool ResolveJsonOutput(string? outputFormat, bool jsonShorthand) =>
        jsonShorthand
        || string.Equals(outputFormat, "json", StringComparison.Ordinal);

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
              arcanum run "<prompt>"  Run one turn and exit (script-safe, no TUI)
              arcanum <command> …     Run a direct command (script-safe, no TUI)

            This terminal is not interactive, so Command Center cannot open here.
            Use `arcanum run` for scripted turns:

              arcanum run -p "hello"
              arcanum run -c "keep going"
              cat error.log | arcanum run "What failed here?"
              arcanum doctor
              arcanum campaign list

            Escape hatches:
              ARCANUM_NO_COMMAND_CENTER=1   Print this usage instead of the TUI
              ARCANUM_NO_AUTO_SERVE=1       Disable auto-start of `arcanum serve`

            Run `arcanum --help` for the full command list, or
            `arcanum help <topic>` for a task-oriented guide.
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
