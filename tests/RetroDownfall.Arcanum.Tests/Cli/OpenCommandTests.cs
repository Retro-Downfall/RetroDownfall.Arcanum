using System.CommandLine;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class OpenCommandTests
{

    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]

    public void Root_and_open_help_expose_the_complete_application_launch_tree()
    {

        FakeResourceCatalog resources = new();

        FakeApplicationLauncher launcher = new();

        FakeCommandCenterHost center = new();

        ServiceCollection services = CreateServices(resources, launcher, center);

        CliTestResult root = CliTestHarness.Run(services, "--help");

        CliTestResult open = CliTestHarness.Run(services, "open", "--help");

        Assert.Equal((int)CliExitCode.Success, root.ExitCode);

        Assert.Contains("center", root.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("open", root.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Equal((int)CliExitCode.Success, open.ExitCode);

        string[] expected =
        [
            "center",
            "theforge",
            "compendium",
            "session",
            "campaign",
            "spell",
            "prompt",
            "apprentice",
        ];

        foreach (string command in expected)
        {

            Assert.Contains(command, open.Output, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]

    public void Open_session_resolves_a_friendly_selector_to_the_canonical_id_before_launch()
    {

        Guid sessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        FakeResourceCatalog resources = new()
        {

            SessionResult = ResourceSelectionResult<SessionSummaryDto>.Selected(
                Session(sessionId, "Night Work")),

        };

        FakeApplicationLauncher launcher = new();

        ServiceCollection services = CreateServices(
            resources,
            launcher,
            new FakeCommandCenterHost());

        CliTestResult result = CliTestHarness.Run(
            services,
            "open",
            "session",
            "Night Work");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("Night Work", resources.SessionIdentifier);

        ApplicationLaunchRequest request = Assert.Single(launcher.Requests);

        ApplicationDeepLink deepLink = Assert.IsType<ApplicationDeepLink>(request.DeepLink);

        Assert.Equal(DesktopApplication.TheForge, request.Application);

        Assert.Equal(ApplicationDeepLink.CurrentSchemaVersion, deepLink.SchemaVersion);

        Assert.Equal(DesktopApplication.TheForge, deepLink.TargetApplication);

        Assert.Equal(ApplicationResourceKind.Session, deepLink.ResourceKind);

        Assert.Equal(sessionId.ToString("D"), deepLink.ResourceId);

        Assert.Null(deepLink.ResourceScopeId);

        Assert.Equal(ApplicationInitialView.Workbench, deepLink.InitialView);

        Assert.Equal(
            $"arcanum session show {sessionId:D}",
            request.CliFallbackCommand);

    }

    [Fact]

    public void Cancelled_resource_selection_returns_success_without_launching()
    {

        FakeResourceCatalog resources = new()
        {

            SessionResult = ResourceSelectionResult<SessionSummaryDto>.Cancelled(),

        };

        FakeApplicationLauncher launcher = new();

        ServiceCollection services = CreateServices(
            resources,
            launcher,
            new FakeCommandCenterHost());

        CliTestResult result = CliTestHarness.Run(
            services,
            "open",
            "session",
            "pick one");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Empty(launcher.Requests);

    }

    [Fact]

    public void Resource_selection_error_is_reported_without_launching()
    {

        FakeResourceCatalog resources = new()
        {

            SessionResult = ResourceSelectionResult<SessionSummaryDto>.Failure(
                "The session identifier is ambiguous."),

        };

        FakeApplicationLauncher launcher = new();

        ServiceCollection services = CreateServices(
            resources,
            launcher,
            new FakeCommandCenterHost());

        CliTestResult result = CliTestHarness.Run(
            services,
            "open",
            "session",
            "night");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(
            "ambiguous",
            result.Output + result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Empty(launcher.Requests);

    }

    [Fact]

    public void Open_spell_resolves_the_spell_and_safe_workspace_id_without_putting_the_path_in_the_link()
    {

        const string SpellName = "Review Changes";

        const string WorkspacePath = "/server/workspaces/Spell Lab";

        const string WorkspaceId = "workspace-opaque-42";

        FakeResourceCatalog resources = new()
        {

            SpellResult = ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    SpellName,
                    "Review a change set.",
                    SpellSource.Workspace,
                    [])),

            WorkspaceResult = ResourceSelectionResult<WorkspaceInfo>.Selected(
                new WorkspaceInfo(
                    WorkspaceId,
                    "Spell Lab",
                    WorkspacePath,
                    WorkspaceType.Spell,
                    Timestamp,
                    Persisted: true)),

        };

        FakeApplicationLauncher launcher = new();

        ServiceCollection services = CreateServices(
            resources,
            launcher,
            new FakeCommandCenterHost());

        CliTestResult result = CliTestHarness.Run(
            services,
            "open",
            "spell",
            SpellName,
            "--workspace",
            WorkspacePath);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(SpellName, resources.SpellIdentifier);

        Assert.Equal(WorkspacePath, resources.SpellWorkspace);

        Assert.Equal(WorkspacePath, resources.WorkspaceIdentifier);

        ApplicationLaunchRequest request = Assert.Single(launcher.Requests);

        ApplicationDeepLink deepLink = Assert.IsType<ApplicationDeepLink>(request.DeepLink);

        Assert.Equal(ApplicationResourceKind.Spell, deepLink.ResourceKind);

        Assert.Equal(SpellName, deepLink.ResourceId);

        Assert.Equal(WorkspaceId, deepLink.ResourceScopeId);

        Assert.DoesNotContain(
            WorkspacePath,
            deepLink.ToString(),
            StringComparison.Ordinal);

        Assert.Equal(
            $"arcanum spell show --workspace {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(WorkspaceId)} {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(SpellName)}",
            request.CliFallbackCommand);

        Assert.DoesNotContain(
            WorkspacePath,
            request.CliFallbackCommand,
            StringComparison.Ordinal);

    }

    [Fact]

    public void Missing_application_prints_every_candidate_and_copyable_development_and_cli_fallbacks()
    {

        ApplicationDiscoveryCandidate bundle = new(
            ApplicationCandidateKind.ApplicationBundle,
            "/Applications/Compendium.app",
            "/Applications/Compendium.app",
            Exists: false);

        ApplicationDiscoveryCandidate project = new(
            ApplicationCandidateKind.DevelopmentProject,
            "/checkout/src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj",
            "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj",
            Exists: false,
            ProjectRelativePath:
                "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj");

        FakeApplicationLauncher launcher = new()
        {

            Launch = request => new ApplicationLaunchResult(
                ApplicationLaunchStatus.Unavailable,
                [bundle, project],
                SelectedCandidate: null,
                "Compendium was not found.",
                "dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj",
                request.CliFallbackCommand),

        };

        ServiceCollection services = CreateServices(
            new FakeResourceCatalog(),
            launcher,
            new FakeCommandCenterHost());

        CliTestResult result = CliTestHarness.Run(
            services,
            "open",
            "compendium");

        string combined = result.Output + result.Error;

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("ApplicationBundle", combined, StringComparison.Ordinal);

        Assert.Contains(bundle.DisplayPath, combined, StringComparison.Ordinal);

        Assert.Contains("DevelopmentProject", combined, StringComparison.Ordinal);

        Assert.Contains(project.DisplayPath, combined, StringComparison.Ordinal);

        Assert.Contains(
            "dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj",
            combined,
            StringComparison.Ordinal);

        Assert.Contains("arcanum config edit", combined, StringComparison.Ordinal);

    }

    [Fact]

    public void Resource_launch_requests_use_the_current_exact_cli_fallbacks()
    {

        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid campaignId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Guid promptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        Guid apprenticeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        FakeResourceCatalog resources = new()
        {

            SessionResult = ResourceSelectionResult<SessionSummaryDto>.Selected(
                Session(sessionId, "Night Work")),

            CampaignResult = ResourceSelectionResult<CampaignDto>.Selected(
                Campaign(campaignId, "Moonfall")),

            SpellResult = ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    "Review Changes",
                    null,
                    SpellSource.Builtin,
                    [])),

            PromptResult = ResourceSelectionResult<PromptSummaryDto>.Selected(
                new PromptSummaryDto(
                    promptId,
                    null,
                    "Daily Brief",
                    "v1",
                    null,
                    [],
                    Timestamp)),

            ApprenticeResult = ResourceSelectionResult<ApprenticeSummaryDto>.Selected(
                new ApprenticeSummaryDto(
                    apprenticeId,
                    null,
                    "Scout",
                    "Survey the boundary.",
                    "Running",
                    0,
                    1,
                    Timestamp,
                    Timestamp)),

        };

        FakeApplicationLauncher launcher = new();

        FakeCommandCenterHost center = new();

        ServiceCollection services = CreateServices(resources, launcher, center);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "session", "Night Work").ExitCode);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "campaign", "Moonfall").ExitCode);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "spell", "Review Changes").ExitCode);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "prompt", "Daily Brief").ExitCode);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "apprentice", "Scout").ExitCode);

        Assert.Equal(0, CliTestHarness.Run(services, "open", "compendium").ExitCode);

        Assert.Equal(
            [
                $"arcanum session show {sessionId:D}",
                $"arcanum campaign show {campaignId:D}",
                $"arcanum spell show {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform("Review Changes")}",
                $"arcanum prompt show {promptId:D}",
                $"arcanum apprentice show {apprenticeId:D}",
                "arcanum config edit",
            ],
            launcher.Requests.Select(static request => request.CliFallbackCommand));

        // A fallback is printed as the remedy when the desktop app cannot start, so a spelling the
        // parser rejects would send the operator straight to exit 2.
        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        List<string> broken = [];

        foreach (string fallback in launcher.Requests.Select(static request => request.CliFallbackCommand))
        {

            ParseResult parsed = root.Parse(
                TokenizeFallback(fallback),
                new ParserConfiguration { ResponseFileTokenReplacer = null });

            if (parsed.Errors.Count > 0)
            {

                broken.Add($"{fallback} -> {parsed.Errors[0].Message}");

            }

        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));

    }

    [Fact]

    public void Center_and_open_center_reuse_the_command_center_host_without_launching_a_process()
    {

        FakeApplicationLauncher launcher = new();

        FakeCommandCenterHost center = new();

        ServiceCollection services = CreateServices(
            new FakeResourceCatalog(),
            launcher,
            center);

        CliTestResult alias = CliTestHarness.Run(services, "center");

        CliTestResult open = CliTestHarness.Run(services, "open", "center");

        Assert.Equal((int)CliExitCode.Success, alias.ExitCode);

        Assert.Equal((int)CliExitCode.Success, open.ExitCode);

        Assert.Equal(2, center.RunCount);

        Assert.Empty(launcher.Requests);

    }

    [Fact]

    public async Task Bare_interactive_invocation_still_reuses_the_command_center_host()
    {

        FakeApplicationLauncher launcher = new();

        FakeCommandCenterHost center = new();

        ServiceCollection services = CreateServices(
            new FakeResourceCatalog(),
            launcher,
            center);

        CliTestResult result = await CliTestHarness.RunAsync(services, []);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, center.RunCount);

        Assert.Empty(launcher.Requests);

    }

    /// <summary>
    /// Splits a printed fallback the way a shell would. Fallback arguments are quoted by
    /// <see cref="CommandDisplayFormatter"/>, which uses apostrophes on POSIX and PowerShell alike,
    /// so both quote characters have to be honoured.
    /// </summary>
    private static string[] TokenizeFallback(string fallback)
    {

        string command = fallback["arcanum".Length..].TrimStart();

        List<string> tokens = [];

        System.Text.StringBuilder current = new();

        char quote = '\0';

        foreach (char character in command)
        {

            if (quote == '\0' && character is '"' or '\'')
            {

                quote = character;

                continue;

            }

            if (quote == character)
            {

                quote = '\0';

                continue;

            }

            if (character == ' ' && quote == '\0')
            {

                if (current.Length > 0)
                {

                    tokens.Add(current.ToString());

                    current.Clear();

                }

                continue;

            }

            current.Append(character);

        }

        if (current.Length > 0)
        {

            tokens.Add(current.ToString());

        }

        return [.. tokens];

    }

    private static ServiceCollection CreateServices(
        FakeResourceCatalog resources,
        FakeApplicationLauncher launcher,
        FakeCommandCenterHost center)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<ICliResourceCatalog>();

        services.AddSingleton<ICliResourceCatalog>(resources);

        services.RemoveAll<IApplicationLauncher>();

        services.AddSingleton<IApplicationLauncher>(launcher);

        services.RemoveAll<ICommandCenterHost>();

        services.AddSingleton<ICommandCenterHost>(center);

        services.RemoveAll<ICliEnvironment>();

        services.AddSingleton<ICliEnvironment>(new InteractiveCliEnvironment());

        return services;

    }

    private static SessionSummaryDto Session(Guid id, string title) =>
        new(
            id,
            null,
            title,
            "active",
            3,
            Timestamp,
            Timestamp);

    private static CampaignDto Campaign(Guid id, string name) =>
        new(
            id,
            name,
            "/server/campaigns/" + name,
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            Timestamp,
            Timestamp);

    private sealed class FakeApplicationLauncher : IApplicationLauncher
    {

        public Func<ApplicationLaunchRequest, ApplicationLaunchResult>? Launch { get; init; }

        public List<ApplicationLaunchRequest> Requests { get; } = [];

        public ApplicationLaunchResult TryLaunch(ApplicationLaunchRequest request)
        {

            Requests.Add(request);

            return Launch?.Invoke(request)
                ?? new ApplicationLaunchResult(
                    ApplicationLaunchStatus.Started,
                    [],
                    SelectedCandidate: null,
                    "Started application.",
                    DevelopmentFallbackCommand: null,
                    request.CliFallbackCommand);

        }

    }

    private sealed class FakeCommandCenterHost : ICommandCenterHost
    {

        public int RunCount { get; private set; }

        public Task<int> RunAsync(CancellationToken cancellationToken)
        {

            RunCount++;

            return Task.FromResult((int)CliExitCode.Success);

        }

    }

    private sealed class InteractiveCliEnvironment : ICliEnvironment
    {

        public bool IsInteractive => true;

        public bool ColorEnabled => false;

        public bool ShouldShowManaBar => false;

    }

    private sealed class FakeResourceCatalog : ICliResourceCatalog
    {

        public ResourceSelectionResult<CampaignDto> CampaignResult { get; init; } =
            ResourceSelectionResult<CampaignDto>.Failure("Unexpected campaign selection.");

        public ResourceSelectionResult<SessionSummaryDto> SessionResult { get; init; } =
            ResourceSelectionResult<SessionSummaryDto>.Failure("Unexpected session selection.");

        public ResourceSelectionResult<WorkspaceInfo> WorkspaceResult { get; init; } =
            ResourceSelectionResult<WorkspaceInfo>.Failure("Unexpected workspace selection.");

        public ResourceSelectionResult<PromptSummaryDto> PromptResult { get; init; } =
            ResourceSelectionResult<PromptSummaryDto>.Failure("Unexpected prompt selection.");

        public ResourceSelectionResult<SpellSummary> SpellResult { get; init; } =
            ResourceSelectionResult<SpellSummary>.Failure("Unexpected spell selection.");

        public ResourceSelectionResult<ApprenticeSummaryDto> ApprenticeResult { get; init; } =
            ResourceSelectionResult<ApprenticeSummaryDto>.Failure("Unexpected Apprentice selection.");

        public string? CampaignIdentifier { get; private set; }

        public string? SessionIdentifier { get; private set; }

        public string? WorkspaceIdentifier { get; private set; }

        public string? PromptIdentifier { get; private set; }

        public string? SpellIdentifier { get; private set; }

        public string? SpellWorkspace { get; private set; }

        public string? ApprenticeIdentifier { get; private set; }

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            CampaignIdentifier = identifier;

            return Task.FromResult(CampaignResult);

        }

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            SessionIdentifier = identifier;

            return Task.FromResult(SessionResult);

        }

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<EntryDto>.Failure(
                    "Unexpected session-entry selection."));

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            WorkspaceIdentifier = identifier;

            return Task.FromResult(WorkspaceResult);

        }

        public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            PromptIdentifier = identifier;

            return Task.FromResult(PromptResult);

        }

        public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
            string? identifier,
            string? workspace,
            CancellationToken cancellationToken)
        {

            SpellIdentifier = identifier;

            SpellWorkspace = workspace;

            return Task.FromResult(SpellResult);

        }

        public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
            string? identifier,
            CancellationToken cancellationToken)
        {

            ApprenticeIdentifier = identifier;

            return Task.FromResult(ApprenticeResult);

        }

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<ModelInfoDto>.Failure(
                    "Unexpected model selection."));

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<ProviderInfoDto>.Failure(
                    "Unexpected provider selection."));

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ResourceSelectionResult<McpServerInfo>.Failure(
                    "Unexpected MCP server selection."));

    }

}
