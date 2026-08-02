using System.ComponentModel;

using Avalonia.Threading;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.TheForge.Ux.Models;

using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.Services;

public readonly record struct TheForgeDeepLinkRouteResult(bool Accepted)
{

    public static TheForgeDeepLinkRouteResult Rejected => new(false);

    public static TheForgeDeepLinkRouteResult Routed => new(true);

}

public interface ITheForgeDeepLinkTarget
{

    void OpenDocument(DocumentKind kind, string id, string? workspace = null);

    Task<bool> FocusCampaignAsync(Guid campaignId, CancellationToken cancellationToken);

    Task<bool> FocusApprenticeAsync(Guid apprenticeId, CancellationToken cancellationToken);

    Task<string?> ResolveWorkspacePathAsync(
        string scopeId,
        CancellationToken cancellationToken);

}

public sealed class TheForgeDeepLinkRouter
{

    private readonly ITheForgeDeepLinkTarget _target;

    public TheForgeDeepLinkRouter(ITheForgeDeepLinkTarget target)
    {

        _target = target;

    }

    public async Task<TheForgeDeepLinkRouteResult> RouteAsync(
        ApplicationDeepLink? deepLink,
        CancellationToken cancellationToken)
    {

        if (!CanRoute(deepLink))
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        try
        {

            return deepLink!.ResourceKind switch
            {

                ApplicationResourceKind.None => TheForgeDeepLinkRouteResult.Routed,

                ApplicationResourceKind.Session => RouteDocument(
                    deepLink.ResourceId,
                    DocumentKind.Session),

                ApplicationResourceKind.Prompt => RouteDocument(
                    deepLink.ResourceId,
                    DocumentKind.Prompt),

                ApplicationResourceKind.Spell => await RouteSpellAsync(
                    deepLink,
                    cancellationToken).ConfigureAwait(false),

                ApplicationResourceKind.Campaign => await RouteCampaignAsync(
                    deepLink.ResourceId,
                    cancellationToken).ConfigureAwait(false),

                ApplicationResourceKind.Apprentice => await RouteApprenticeAsync(
                    deepLink.ResourceId,
                    cancellationToken).ConfigureAwait(false),

                _ => TheForgeDeepLinkRouteResult.Rejected,

            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

    }

    internal static bool CanRoute(ApplicationDeepLink? deepLink) =>
        deepLink is
        {

            SchemaVersion: ApplicationDeepLink.CurrentSchemaVersion,

            TargetApplication: DesktopApplication.TheForge,

        };

    private TheForgeDeepLinkRouteResult RouteDocument(
        string? resourceId,
        DocumentKind documentKind)
    {

        if (!Guid.TryParse(resourceId, out Guid id)
            || id == Guid.Empty)
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        _target.OpenDocument(documentKind, id.ToString("D"));

        return TheForgeDeepLinkRouteResult.Routed;

    }

    private async Task<TheForgeDeepLinkRouteResult> RouteSpellAsync(
        ApplicationDeepLink deepLink,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(deepLink.ResourceId))
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        if (string.IsNullOrWhiteSpace(deepLink.ResourceScopeId))
        {

            _target.OpenDocument(DocumentKind.Spell, deepLink.ResourceId);

            return TheForgeDeepLinkRouteResult.Routed;

        }

        string? workspacePath = await _target
            .ResolveWorkspacePathAsync(deepLink.ResourceScopeId, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(workspacePath))
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        _target.OpenDocument(
            DocumentKind.Spell,
            deepLink.ResourceId,
            workspacePath);

        return TheForgeDeepLinkRouteResult.Routed;

    }

    private async Task<TheForgeDeepLinkRouteResult> RouteCampaignAsync(
        string? resourceId,
        CancellationToken cancellationToken)
    {

        if (!Guid.TryParse(resourceId, out Guid campaignId)
            || campaignId == Guid.Empty)
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        bool focused = await _target
            .FocusCampaignAsync(campaignId, cancellationToken)
            .ConfigureAwait(false);

        return focused
            ? TheForgeDeepLinkRouteResult.Routed
            : TheForgeDeepLinkRouteResult.Rejected;

    }

    private async Task<TheForgeDeepLinkRouteResult> RouteApprenticeAsync(
        string? resourceId,
        CancellationToken cancellationToken)
    {

        if (!Guid.TryParse(resourceId, out Guid apprenticeId)
            || apprenticeId == Guid.Empty)
        {

            return TheForgeDeepLinkRouteResult.Rejected;

        }

        bool focused = await _target
            .FocusApprenticeAsync(apprenticeId, cancellationToken)
            .ConfigureAwait(false);

        return focused
            ? TheForgeDeepLinkRouteResult.Routed
            : TheForgeDeepLinkRouteResult.Rejected;

    }

}

public sealed class TheForgeDeepLinkCoordinator
{

    private readonly IArcanumConnection _connection;

    private readonly TheForgeDeepLinkRouter _router;

    public TheForgeDeepLinkCoordinator(
        IArcanumConnection connection,
        TheForgeDeepLinkRouter router)
    {

        _connection = connection;

        _router = router;

    }

    public async Task<TheForgeDeepLinkRouteResult> RouteAsync(
        ApplicationDeepLink? deepLink,
        CancellationToken cancellationToken)
    {

        if (!RequiresAuthenticatedConnection(deepLink))
        {

            return await _router
                .RouteAsync(deepLink, cancellationToken)
                .ConfigureAwait(false);

        }

        if (_connection.State == ConnectionState.Disconnected)
        {

            _connection.Connect();

        }

        await WaitForConnectedAsync(cancellationToken).ConfigureAwait(false);

        return await _router
            .RouteAsync(deepLink, cancellationToken)
            .ConfigureAwait(false);

    }

    private static bool RequiresAuthenticatedConnection(ApplicationDeepLink? deepLink) =>
        TheForgeDeepLinkRouter.CanRoute(deepLink)
        && deepLink!.ResourceKind is
            ApplicationResourceKind.Session
            or ApplicationResourceKind.Campaign
            or ApplicationResourceKind.Spell
            or ApplicationResourceKind.Prompt
            or ApplicationResourceKind.Apprentice;

    private async Task WaitForConnectedAsync(CancellationToken cancellationToken)
    {

        if (_connection.State == ConnectionState.Connected)
        {

            return;

        }

        TaskCompletionSource connected = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        PropertyChangedEventHandler handler = (_, args) =>
        {

            if (args.PropertyName == nameof(IArcanumConnection.State)
                && _connection.State == ConnectionState.Connected)
            {

                connected.TrySetResult();

            }

        };

        _connection.PropertyChanged += handler;

        try
        {

            if (_connection.State == ConnectionState.Connected)
            {

                connected.TrySetResult();

            }

            await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _connection.PropertyChanged -= handler;

        }

    }

}

internal sealed class TheForgeDeepLinkTarget : ITheForgeDeepLinkTarget
{

    private readonly INavigationService _navigation;

    private readonly WorkspaceService _workspaces;

    public TheForgeDeepLinkTarget(
        INavigationService navigation,
        WorkspaceService workspaces)
    {

        _navigation = navigation;

        _workspaces = workspaces;

    }

    public void OpenDocument(DocumentKind kind, string id, string? workspace = null)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            _navigation.OpenDocument(kind, id, workspace);

            return;

        }

        Dispatcher.UIThread.Post(() =>
            _navigation.OpenDocument(kind, id, workspace));

    }

    public Task<bool> FocusCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken) =>
        InvokeOnUiThreadAsync(() =>
            _navigation.FocusCampaignAsync(campaignId, cancellationToken));

    public Task<bool> FocusApprenticeAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken) =>
        InvokeOnUiThreadAsync(() =>
            _navigation.FocusApprenticeAsync(apprenticeId, cancellationToken));

    public async Task<string?> ResolveWorkspacePathAsync(
        string scopeId,
        CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceInfo[]>? response = await _workspaces
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (response is not { IsSuccess: true, Data: { } workspaces })
        {

            return null;

        }

        return workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Id, scopeId, StringComparison.Ordinal))?.Path;

    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            return action();

        }

        return Dispatcher.UIThread.InvokeAsync(action);

    }

}

internal sealed record TheForgeStartupArguments(
    ApplicationDeepLink? DeepLink,
    string[] AvaloniaArguments);

internal static class TheForgeDeepLinkStartup
{

    public static TheForgeStartupArguments Parse(string[] arguments)
    {

        ApplicationDeepLink? deepLink;

        try
        {

            deepLink = ApplicationDeepLinkCodec.ParseArguments(
                arguments,
                DesktopApplication.TheForge);

        }
        catch
        {

            deepLink = null;

        }

        return new(
            deepLink,
            RemovePrivateArguments(arguments));

    }

    private static string[] RemovePrivateArguments(string[] arguments)
    {

        List<string> forwarded = new(arguments.Length);

        for (int index = 0; index < arguments.Length; index++)
        {

            if (!string.Equals(
                    arguments[index],
                    ApplicationDeepLinkCodec.ArgumentName,
                    StringComparison.Ordinal))
            {

                forwarded.Add(arguments[index]);

                continue;

            }

            if (index + 1 < arguments.Length)
            {

                index++;

            }

        }

        return [.. forwarded];

    }

}
