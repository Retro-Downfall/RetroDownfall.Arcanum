using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ApplicationDeepLinkRouterTests
{

    [Fact]
    public void StartupParser_ValidDeepLink_DecodesAndStripsOnlyPrivateArguments()
    {

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            "11111111-1111-1111-1111-111111111111");

        string payload = ApplicationDeepLinkCodec.Encode(link);

        string[] unrelatedArguments =
        [

            "--theme",

            "dark mode",

            "--renderer=名字",

        ];

        string[] arguments =
        [

            unrelatedArguments[0],

            unrelatedArguments[1],

            ApplicationDeepLinkCodec.ArgumentName,

            payload,

            unrelatedArguments[2],

        ];

        TheForgeStartupArguments startup = TheForgeDeepLinkStartup.Parse(arguments);

        Assert.Equal(link, startup.DeepLink);

        Assert.Equal(unrelatedArguments, startup.AvaloniaArguments);

        Assert.DoesNotContain(payload, startup.AvaloniaArguments);

    }

    [Fact]
    public void StartupParser_MalformedOrWrongTarget_StripsPayloadAndSafelyDiscardsLink()
    {

        ApplicationDeepLink wrongTarget = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: ApplicationInitialView.Settings);

        string[] privatePayloads =
        [

            "{not-json:api-key=must-not-surface",

            ApplicationDeepLinkCodec.Encode(wrongTarget),

        ];

        foreach (string privatePayload in privatePayloads)
        {

            string[] unrelatedArguments =
            [

                "--theme",

                "light",

            ];

            string[] arguments =
            [

                unrelatedArguments[0],

                ApplicationDeepLinkCodec.ArgumentName,

                privatePayload,

                unrelatedArguments[1],

            ];

            TheForgeStartupArguments startup = TheForgeDeepLinkStartup.Parse(arguments);

            Assert.Null(startup.DeepLink);

            Assert.Equal(unrelatedArguments, startup.AvaloniaArguments);

            Assert.DoesNotContain(privatePayload, startup.AvaloniaArguments);

        }

    }

    [Fact]
    public async Task RouteAsync_Session_OpensSessionDocument()
    {

        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            sessionId.ToString("D"));

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Equal(
            (DocumentKind.Session, sessionId.ToString("D"), (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task RouteAsync_Prompt_OpensPromptDocument()
    {

        Guid promptId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Prompt,
            promptId.ToString("D"));

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Equal(
            (DocumentKind.Prompt, promptId.ToString("D"), (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task RouteAsync_Spell_ResolvesSafeWorkspaceIdBeforeOpeningDocument()
    {

        const string spellName = "restore-light";

        const string workspaceId = "workspace-server-id-42";

        const string serverReturnedPath = "/server/resolved/workspaces/42";

        RecordingDeepLinkTarget target = new()
        {

            WorkspacePath = serverReturnedPath,

        };

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Spell,
            spellName,
            workspaceId);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Equal(workspaceId, target.ResolvedWorkspaceScopeId);

        Assert.Equal(
            (DocumentKind.Spell, spellName, serverReturnedPath),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task RouteAsync_BuiltInSpell_OpensWithoutWorkspaceResolution()
    {

        const string spellName = "builtin-light";

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Spell,
            spellName);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Null(target.ResolvedWorkspaceScopeId);

        Assert.Equal(
            (DocumentKind.Spell, spellName, (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task RouteAsync_Campaign_FocusesAtelierCampaignByCanonicalId()
    {

        Guid campaignId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Campaign,
            campaignId.ToString("D"),
            initialView: ApplicationInitialView.Atelier);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Equal(campaignId, Assert.Single(target.FocusedCampaigns));

        Assert.Empty(target.OpenedDocuments);

    }

    [Fact]
    public async Task RouteAsync_CampaignMissingFromAtelier_ReturnsRejected()
    {

        Guid campaignId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        RecordingDeepLinkTarget target = new()
        {

            CampaignFocusResult = false,

        };

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Campaign,
            campaignId.ToString("D"),
            initialView: ApplicationInitialView.Atelier);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.False(result.Accepted);

        Assert.Equal(campaignId, Assert.Single(target.FocusedCampaigns));

    }

    [Theory]

    [InlineData(ApplicationResourceKind.Session)]

    [InlineData(ApplicationResourceKind.Prompt)]

    [InlineData(ApplicationResourceKind.Campaign)]

    [InlineData(ApplicationResourceKind.Apprentice)]

    public async Task RouteAsync_EmptyCanonicalId_RejectsWithoutReportingNavigation(
        ApplicationResourceKind resourceKind)
    {

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            resourceKind,
            Guid.Empty.ToString("D"),
            initialView: ApplicationInitialView.Workbench);

        TheForgeDeepLinkRouteResult result = await router
            .RouteAsync(link, CancellationToken.None);

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    [Fact]
    public async Task RouteAsync_Apprentice_FocusesWarTableApprenticeByCanonicalId()
    {

        Guid apprenticeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Apprentice,
            apprenticeId.ToString("D"),
            initialView: ApplicationInitialView.WarTable);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.True(result.Accepted);

        Assert.Equal(apprenticeId, Assert.Single(target.FocusedApprentices));

        Assert.Empty(target.OpenedDocuments);

    }

    [Fact]
    public async Task RouteAsync_ApprenticeMissingFromWarTable_ReturnsRejected()
    {

        Guid apprenticeId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

        RecordingDeepLinkTarget target = new()
        {

            ApprenticeFocusResult = false,

        };

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Apprentice,
            apprenticeId.ToString("D"),
            initialView: ApplicationInitialView.WarTable);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.False(result.Accepted);

        Assert.Equal(apprenticeId, Assert.Single(target.FocusedApprentices));

    }

    [Fact]
    public async Task RouteAsync_TargetMismatch_SafelyRejectsWithoutRouting()
    {

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: ApplicationInitialView.Settings);

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    [Fact]
    public async Task RouteAsync_FutureSchema_SafelyRejectsWithoutRouting()
    {

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        ApplicationDeepLink link = new(
            ApplicationDeepLink.CurrentSchemaVersion + 1,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Session,
            "55555555-5555-5555-5555-555555555555");

        TheForgeDeepLinkRouteResult result = await router.RouteAsync(link, CancellationToken.None);

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    [Fact]
    public async Task Coordinator_DefersRoutingUntilConnectionIsConnected()
    {

        Guid sessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");

        MutableArcanumConnection connection = new(ConnectionState.Connecting);

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkRouter router = new(target);

        TheForgeDeepLinkCoordinator coordinator = new(connection, router);

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            sessionId.ToString("D"));

        Task<TheForgeDeepLinkRouteResult> routing = coordinator.RouteAsync(link, CancellationToken.None);

        await Task.Yield();

        Assert.False(routing.IsCompleted);

        Assert.False(target.HasCalls);

        connection.SetState(ConnectionState.Connected);

        TheForgeDeepLinkRouteResult result = await routing;

        Assert.True(result.Accepted);

        Assert.Equal(
            (DocumentKind.Session, sessionId.ToString("D"), (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    private static ApplicationDeepLink NewLink(
        ApplicationResourceKind resourceKind,
        string resourceId,
        string? resourceScopeId = null,
        ApplicationInitialView? initialView = null) =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            resourceKind,
            resourceId,
            resourceScopeId,
            initialView);

    private sealed class RecordingDeepLinkTarget : ITheForgeDeepLinkTarget
    {

        public List<(DocumentKind Kind, string Id, string? Workspace)> OpenedDocuments { get; } = [];

        public List<Guid> FocusedCampaigns { get; } = [];

        public List<Guid> FocusedApprentices { get; } = [];

        public string? WorkspacePath { get; init; }

        public bool CampaignFocusResult { get; init; } = true;

        public bool ApprenticeFocusResult { get; init; } = true;

        public string? ResolvedWorkspaceScopeId { get; private set; }

        public bool HasCalls =>
            OpenedDocuments.Count > 0
            || FocusedCampaigns.Count > 0
            || FocusedApprentices.Count > 0
            || ResolvedWorkspaceScopeId is not null;

        public void OpenDocument(DocumentKind kind, string id, string? workspace = null)
        {

            OpenedDocuments.Add((kind, id, workspace));

        }

        public Task<bool> FocusCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {

            FocusedCampaigns.Add(campaignId);

            return Task.FromResult(CampaignFocusResult);

        }

        public Task<bool> FocusApprenticeAsync(Guid apprenticeId, CancellationToken cancellationToken)
        {

            FocusedApprentices.Add(apprenticeId);

            return Task.FromResult(ApprenticeFocusResult);

        }

        public Task<string?> ResolveWorkspacePathAsync(
            string scopeId,
            CancellationToken cancellationToken)
        {

            ResolvedWorkspaceScopeId = scopeId;

            return Task.FromResult(WorkspacePath);

        }

    }

    private sealed class MutableArcanumConnection : IArcanumConnection
    {

        public MutableArcanumConnection(ConnectionState state)
        {

            State = state;

        }

        public ConnectionState State { get; private set; }

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        public void SetState(ConnectionState state)
        {

            State = state;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

    }

}
