using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal sealed class OpenCommands(
    ICliResourceCatalog resources,
    IApplicationLauncher launcher,
    ICommandCenterHost commandCenter,
    CliSessionManager sessionManager,
    IConsoleDispatcher console)
{

    public Task<int> Center(CancellationToken cancellationToken) =>
        Center(
            continueSession: false,
            resume: false,
            resumeTarget: null,
            cancellationToken);

    public async Task<int> Center(
        bool continueSession,
        bool resume,
        string? resumeTarget,
        CancellationToken cancellationToken)
    {

        if (continueSession && resume)
        {

            console.WriteDiagnostic(
                "--continue and --resume each select a session; supply exactly one.");

            return (int)CliExitCode.ConfigurationError;

        }

        Guid? startupSession = null;

        if (continueSession)
        {

            startupSession = sessionManager.GetLastSessionId(quiet: true);

            if (startupSession is null)
            {

                console.WriteDiagnostic(
                    "No previous session to continue. Open Command Center with: arcanum");

                return (int)CliExitCode.ConfigurationError;

            }

        }
        else if (resume)
        {

            ResourceSelectionResult<SessionSummaryDto> selection = await resources
                .SelectSessionAsync(
                    string.IsNullOrWhiteSpace(resumeTarget) ? null : resumeTarget,
                    cancellationToken)
                .ConfigureAwait(false);

            if (selection.Status == ResourceSelectionStatus.Cancelled)
            {

                return (int)CliExitCode.Success;

            }

            if (selection.Status != ResourceSelectionStatus.Selected
                || selection.Value is null)
            {

                console.WriteDiagnostic(
                    selection.Error ?? "The session could not be selected.");

                return (int)CliExitCode.GenericError;

            }

            startupSession = selection.Value.Id;

        }

        return await commandCenter
            .RunAsync(startupSession, cancellationToken)
            .ConfigureAwait(false);

    }

    public int TheForge() =>
        Launch(
            DesktopApplication.TheForge,
            Link(
                DesktopApplication.TheForge,
                ApplicationResourceKind.None),
            "arcanum --help");

    public int Compendium() =>
        Launch(
            DesktopApplication.Compendium,
            Link(
                DesktopApplication.Compendium,
                ApplicationResourceKind.Configuration,
                initialView: ApplicationInitialView.Settings),
            "arcanum config edit");

    public Task<int> Session(
        string? identifier,
        CancellationToken cancellationToken) =>
        ResolveAndLaunchAsync(
            token => resources.SelectSessionAsync(identifier, token),
            static session => Link(
                DesktopApplication.TheForge,
                ApplicationResourceKind.Session,
                session.Id.ToString("D"),
                initialView: ApplicationInitialView.Workbench),
            static session => $"arcanum session show {session.Id:D}",
            cancellationToken);

    public Task<int> Campaign(
        string? identifier,
        CancellationToken cancellationToken) =>
        ResolveAndLaunchAsync(
            token => resources.SelectCampaignAsync(identifier, token),
            static campaign => Link(
                DesktopApplication.TheForge,
                ApplicationResourceKind.Campaign,
                campaign.Id.ToString("D"),
                initialView: ApplicationInitialView.Atelier),
            static campaign => $"arcanum campaign show {campaign.Id:D}",
            cancellationToken);

    public async Task<int> Spell(
        string? identifier,
        string? workspace,
        CancellationToken cancellationToken)
    {

        WorkspaceInfo? selectedWorkspace = null;

        string? serverWorkspacePath = null;

        if (!string.IsNullOrWhiteSpace(workspace))
        {

            ResourceSelectionResult<WorkspaceInfo> workspaceSelection =
                await resources
                    .SelectWorkspaceAsync(workspace, cancellationToken)
                    .ConfigureAwait(false);

            if (workspaceSelection.Status == ResourceSelectionStatus.Cancelled)
            {

                return (int)CliExitCode.Success;

            }

            if (workspaceSelection.Status == ResourceSelectionStatus.Error)
            {

                WriteSelectionError(workspaceSelection.Error, "Workspace selection failed.");

                return (int)CliExitCode.GenericError;

            }

            selectedWorkspace = workspaceSelection.Value!;

            serverWorkspacePath = selectedWorkspace.Path;

        }

        string? scopeId = selectedWorkspace?.Id;

        return await ResolveAndLaunchAsync(
                token => resources.SelectSpellAsync(
                    identifier,
                    serverWorkspacePath,
                    token),
                spell => Link(
                    DesktopApplication.TheForge,
                    ApplicationResourceKind.Spell,
                    spell.Name,
                    scopeId,
                    ApplicationInitialView.Workbench),
                spell => SpellFallback(spell.Name, scopeId),
                cancellationToken)
            .ConfigureAwait(false);

    }

    public Task<int> Prompt(
        string? identifier,
        CancellationToken cancellationToken) =>
        ResolveAndLaunchAsync(
            token => resources.SelectPromptAsync(identifier, token),
            static prompt => Link(
                DesktopApplication.TheForge,
                ApplicationResourceKind.Prompt,
                prompt.Id.ToString("D"),
                initialView: ApplicationInitialView.Workbench),
            static prompt => $"arcanum prompt show {prompt.Id:D}",
            cancellationToken);

    public Task<int> Apprentice(
        string? identifier,
        CancellationToken cancellationToken) =>
        ResolveAndLaunchAsync(
            token => resources.SelectApprenticeAsync(identifier, token),
            static apprentice => Link(
                DesktopApplication.TheForge,
                ApplicationResourceKind.Apprentice,
                apprentice.Id.ToString("D"),
                initialView: ApplicationInitialView.WarTable),
            static apprentice => $"arcanum apprentice show {apprentice.Id:D}",
            cancellationToken);

    private async Task<int> ResolveAndLaunchAsync<T>(
        Func<CancellationToken, Task<ResourceSelectionResult<T>>> select,
        Func<T, ApplicationDeepLink> createDeepLink,
        Func<T, string> createFallback,
        CancellationToken cancellationToken)
        where T : class
    {

        ResourceSelectionResult<T> selection = await select(cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return (int)CliExitCode.Success;

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            WriteSelectionError(selection.Error, "Resource selection failed.");

            return (int)CliExitCode.GenericError;

        }

        T resource = selection.Value!;

        return Launch(
            DesktopApplication.TheForge,
            createDeepLink(resource),
            createFallback(resource));

    }

    private int Launch(
        DesktopApplication application,
        ApplicationDeepLink? deepLink,
        string cliFallbackCommand)
    {

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(
                application,
                deepLink,
                cliFallbackCommand));

        if (result.Launched)
        {

            if (!string.IsNullOrWhiteSpace(result.Message))
            {

                console.WritePayload(result.Message);

            }

            return (int)CliExitCode.Success;

        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {

            console.WriteDiagnostic(result.Message);

        }

        foreach (ApplicationDiscoveryCandidate candidate in result.TriedCandidates)
        {

            console.WriteDiagnostic(
                $"Tried {candidate.Kind}: {candidate.DisplayPath}");

        }

        if (!string.IsNullOrWhiteSpace(result.DevelopmentFallbackCommand))
        {

            console.WriteDiagnostic(
                $"Development fallback: {result.DevelopmentFallbackCommand}");

        }

        console.WriteDiagnostic($"CLI fallback: {result.CliFallbackCommand}");

        return (int)CliExitCode.GenericError;

    }

    private void WriteSelectionError(string? error, string fallback) =>
        console.WriteDiagnostic(
            string.IsNullOrWhiteSpace(error)
                ? fallback
                : error);

    private static ApplicationDeepLink Link(
        DesktopApplication target,
        ApplicationResourceKind resourceKind,
        string? resourceId = null,
        string? resourceScopeId = null,
        ApplicationInitialView? initialView = null) =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            target,
            resourceKind,
            resourceId,
            resourceScopeId,
            initialView,
            ConnectionProfileId: null);

    private static string SpellFallback(
        string name,
        string? workspaceId)
    {

        string workspaceOption = workspaceId is null
            ? string.Empty
            : $"--workspace {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(workspaceId)} ";

        string separator = name.StartsWith("-", StringComparison.Ordinal)
            ? "-- "
            : string.Empty;

        return $"arcanum spell show {workspaceOption}{separator}{CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(name)}";

    }

}
