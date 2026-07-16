using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Theme;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

[ExcludeFromCodeCoverage] // Reason: ConsoleAppFramework wiring factory; covered via CliApplicationFactoryTests and command smoke tests.
internal static class CliApplicationFactory
{

    public static void ConfigureCliServices(IServiceCollection services, IConfiguration configuration)
    {

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        services.AddArcanumThemeDetection();

        services.AddSingleton<ICliEnvironment>(sp =>
        {
            bool showManaBar = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value.Cli.ShowManaBar;

            return new CliEnvironment(showManaBar);
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

            ThemeColors tc = cli.ThemeColors;

            ThemeSemanticColors sem = useDark ? tc.Dark : tc.Light;

            ThemeColors builtin = new();

            ThemeSemanticColors fb = useDark ? builtin.Dark : builtin.Light;

            return new ConfiguredThemePalette(sem, fb);
        });

        services.AddSingleton<CliSessionManager>();

        services.AddSingleton<MarkdigSpectreRenderer>();

        // W6.4: shared secret/grimoire stack (Data Protection + digest cache + secret store +
        // CLI Grimoire), owned by Infrastructure so it cannot drift from the host wiring.
        services.AddArcanumCliClientStack();

        services.AddHttpClient(
            ArcanumApiClient.StreamingHttpClientName,
            (serviceProvider, client) =>
            {
                int port = ArcanumSettingClamps.HostPort(
                    serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Host.Port);

                client.BaseAddress = new Uri($"http://localhost:{port}/");

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddHttpClient(
            ArcanumApiClient.RequestHttpClientName,
            (serviceProvider, client) =>
            {
                ArcanumSettings settings = serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

                int port = ArcanumSettingClamps.HostPort(settings.Host.Port);

                int timeoutSeconds = ArcanumSettingClamps.ApiRequestTimeoutSeconds(
                    settings.Cli.ApiRequestTimeoutSeconds);

                client.BaseAddress = new Uri($"http://localhost:{port}/");

                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });

        services.AddSingleton<ArcanumApiClient>();

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

        services.AddTransient<SpellCommands>();

        services.AddTransient<SpellVersionCommands>();

        services.AddTransient<PromptCommands>();

        services.AddTransient<WardCommands>();

        services.AddTransient<TrialCommands>();

        services.AddTransient<ApprenticeCommands>();

        services.AddTransient<ModelCommands>();

        services.AddTransient<ProviderCommands>();

    }

    /// <summary>
    /// Runs the CLI end-to-end: merges repeatable-flag occurrences (see
    /// <see cref="RepeatableOptionMerger"/>), assigns the DI <paramref name="serviceProvider"/>,
    /// disables ConsoleAppFramework's default 5s post-SIGINT force-kill (long streams like
    /// <c>ask</c>/<c>chat</c>/<c>apprentice chronicle</c> manage their own graceful shutdown),
    /// registers the command tree (paths and descriptions mirror the pre-migration
    /// Spectre.Console.Cli tree byte-for-byte; only the parsing/dispatch framework changed), runs
    /// it, and returns the resulting exit code. Used by both <c>Program.Main</c> and the CLI test
    /// harness so both paths exercise the exact same dispatch logic.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider serviceProvider)
    {

        ConsoleApp.ServiceProvider = serviceProvider;

        ConsoleApp.Timeout = Timeout.InfiniteTimeSpan;

        // ConsoleAppFramework falls back to JsonSerializer.Deserialize<T> for a JSON-array-syntax
        // option value (e.g. a repeated flag merged by RepeatableOptionMerger into --tag
        // ["a","b","c"]). Under this project's PublishAot/IsAotCompatible settings, the runtime
        // disables reflection-based JsonSerializer by default (IsReflectionEnabledByDefault=false),
        // so that fallback throws unless an explicit source-generated JsonSerializerContext-backed
        // JsonSerializerOptions is supplied.
        ConsoleApp.JsonSerializerOptions = CliJsonArrayContext.Default.Options;

        string[] mergedArgs = RepeatableOptionMerger.Merge(args);

        var app = ConsoleApp.Create();

        app.Add<ServeCommand>("serve");

        app.Add<AskCommand>("ask");

        app.Add<ChatCommand>("chat");

        app.Add<LookCommand>("look");

        app.Add<DoctorCommand>("doctor");

        app.Add<KeyCommands>("key");

        app.Add<LoreCommands>("lore");

        app.Add<DaemonCommands>("daemon");

        app.Add<CampaignCommands>("campaign");

        app.Add<CampaignCodexCommands>("campaign codex");

        app.Add<SessionCommands>("session");

        app.Add<SagaCommands>("saga");

        app.Add<SpellCommands>("spell");

        app.Add<SpellVersionCommands>("spell version");

        app.Add<PromptCommands>("prompt");

        app.Add<WardCommands>("ward");

        app.Add<TrialCommands>("trial");

        app.Add<ApprenticeCommands>("apprentice");

        app.Add<ModelCommands>("model");

        app.Add<ProviderCommands>("provider");

        Environment.ExitCode = 0;

        await app.RunAsync(mergedArgs, startHost: true, stopHost: true, disposeServiceProvider: false).ConfigureAwait(false);

        return Environment.ExitCode;

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

}

/// <summary>
/// Minimal AOT-safe (source-generated, reflection-free) JSON context for
/// <see cref="ConsoleApp.JsonSerializerOptions"/>, covering the one CLR type
/// ConsoleAppFramework needs JSON support for: JSON-array-syntax array-option values
/// (e.g. <c>--tag ["a","b","c"]</c>, produced by <see cref="RepeatableOptionMerger"/>).
/// </summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class CliJsonArrayContext : JsonSerializerContext;
