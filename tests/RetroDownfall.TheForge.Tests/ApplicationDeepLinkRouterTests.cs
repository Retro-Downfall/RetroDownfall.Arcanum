using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
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
    public void StartDeepLinkRouting_WhenTheArgumentFailedToParse_ShowsAnErrorWhisper()
    {

        FakeWhispersService whispers = new();

        ServiceCollection serviceCollection = new();

        serviceCollection.AddSingleton<IWhispersService>(whispers);

        using ServiceProvider provider = serviceCollection.BuildServiceProvider();

        // A --arcanum-deep-link argument was present (unlike the "no argument at all" case, which
        // must stay silent) but TheForgeDeepLinkStartup.Parse could not turn it into a link.
        App.ConfigureServices(provider, startupDeepLink: null, deepLinkParseFailed: true);

        CancellationTokenSource? cancellation = App.StartDeepLinkRouting(provider);

        Assert.Null(cancellation);

        Assert.Contains(
            whispers.Calls,
            call => call.Severity == WhisperSeverity.Error
                && call.Message == "The requested resource could not be opened.");

    }

    [Fact]
    public void StartDeepLinkRouting_WhenNoArgumentWasPresent_StaysSilent()
    {

        FakeWhispersService whispers = new();

        ServiceCollection serviceCollection = new();

        serviceCollection.AddSingleton<IWhispersService>(whispers);

        using ServiceProvider provider = serviceCollection.BuildServiceProvider();

        // No --arcanum-deep-link argument at all — an ordinary launch, which must not whisper.
        App.ConfigureServices(provider, startupDeepLink: null, deepLinkParseFailed: false);

        CancellationTokenSource? cancellation = App.StartDeepLinkRouting(provider);

        Assert.Null(cancellation);

        Assert.Empty(whispers.Calls);

    }

    [Theory]
    [MemberData(nameof(DeepLinkArgumentFailedToParseCases))]
    public void DeepLinkArgumentFailedToParse_MatchesWhetherTheArgumentWasPresentButUnparseable(
        string[] arguments,
        bool expected)
    {

        // Drives Program's predicate the way Main actually would: parse the raw arguments first
        // (TheForgeDeepLinkStartup.Parse, the same call Main makes), then hand both the raw
        // arguments and the parse result to the predicate under test — nothing here seeds
        // deepLinkParseFailed directly.
        TheForgeStartupArguments startup = TheForgeDeepLinkStartup.Parse(arguments);

        Assert.Equal(expected, Program.DeepLinkArgumentFailedToParse(arguments, startup.DeepLink));

    }

    public static TheoryData<string[], bool> DeepLinkArgumentFailedToParseCases()
    {

        string validPayload = ApplicationDeepLinkCodec.Encode(
            NewLink(ApplicationResourceKind.None, resourceId: null!));

        return new TheoryData<string[], bool>
        {

            { ["--arcanum-deep-link", "{not json"], true },

            { [], false },

            { ["--arcanum-deep-link", validPayload], false },

        };

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

    [Fact]
    public async Task Coordinator_RejectsWhenConnectionIsAlreadyInAuthFailure()
    {

        // A rotated master key settles the health poller on Error with the bad key cached for the process
        // lifetime, so waiting for Connected never returns and the caller's "could not be opened" whisper
        // never fires.
        MutableArcanumConnection connection = new(ConnectionState.Error, "Auth.Unauthorized");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkCoordinator coordinator = new(connection, new TheForgeDeepLinkRouter(target));

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            "77777777-7777-7777-7777-777777777777");

        TheForgeDeepLinkRouteResult result = await coordinator
            .RouteAsync(link, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    [Fact]
    public async Task Coordinator_RejectsWhenConnectionTransitionsToAuthFailure()
    {

        MutableArcanumConnection connection = new(ConnectionState.Connecting);

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkCoordinator coordinator = new(connection, new TheForgeDeepLinkRouter(target));

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            "88888888-8888-8888-8888-888888888888");

        Task<TheForgeDeepLinkRouteResult> routing = coordinator.RouteAsync(link, CancellationToken.None);

        await Task.Yield();

        Assert.False(routing.IsCompleted);

        connection.SetState(ConnectionState.Error, "Security.MissingApiKey");

        TheForgeDeepLinkRouteResult result = await routing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    [Fact]
    public async Task Coordinator_WaitsThroughTransientErrorAndRoutesOnRecovery()
    {

        // Three consecutive missed health polls settle the poller on Error while it keeps polling, so a link
        // that arrives during an Arcanum restart must still route once the connection recovers.
        Guid sessionId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        MutableArcanumConnection connection = new(ConnectionState.Connecting);

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkCoordinator coordinator = new(connection, new TheForgeDeepLinkRouter(target));

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            sessionId.ToString("D"));

        Task<TheForgeDeepLinkRouteResult> routing = coordinator.RouteAsync(link, CancellationToken.None);

        await Task.Yield();

        connection.SetState(ConnectionState.Error, "Connection.Failed");

        await SettleAsync();

        Assert.False(routing.IsCompleted);

        Assert.False(target.HasCalls);

        connection.SetState(ConnectionState.Connected);

        TheForgeDeepLinkRouteResult result = await routing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Accepted);

        Assert.Equal(
            (DocumentKind.Session, sessionId.ToString("D"), (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task Coordinator_RoutesWhenConnectionStartsInTransientErrorAndRecovers()
    {

        // The link can also arrive after the poller has already given up on a restarting server; the
        // pre-subscription fast path must not turn that into a rejection either.
        Guid sessionId = Guid.Parse("aaaaaaaa-9999-9999-9999-999999999999");

        MutableArcanumConnection connection = new(ConnectionState.Error, "Connection.Timeout");

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkCoordinator coordinator = new(connection, new TheForgeDeepLinkRouter(target));

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            sessionId.ToString("D"));

        Task<TheForgeDeepLinkRouteResult> routing = coordinator.RouteAsync(link, CancellationToken.None);

        await SettleAsync();

        Assert.False(routing.IsCompleted);

        Assert.False(target.HasCalls);

        connection.SetState(ConnectionState.Connected);

        TheForgeDeepLinkRouteResult result = await routing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Accepted);

        Assert.Equal(
            (DocumentKind.Session, sessionId.ToString("D"), (string?)null),
            Assert.Single(target.OpenedDocuments));

    }

    [Fact]
    public async Task Coordinator_RejectsWhenAuthFailureArrivesDuringTransientError()
    {

        // Once State is Error a later auth failure changes only LastErrorCode, raising no State
        // notification; watching State alone would wait forever on a key that never clears.
        MutableArcanumConnection connection = new(ConnectionState.Connecting);

        RecordingDeepLinkTarget target = new();

        TheForgeDeepLinkCoordinator coordinator = new(connection, new TheForgeDeepLinkRouter(target));

        ApplicationDeepLink link = NewLink(
            ApplicationResourceKind.Session,
            "bbbbbbbb-9999-9999-9999-999999999999");

        Task<TheForgeDeepLinkRouteResult> routing = coordinator.RouteAsync(link, CancellationToken.None);

        await Task.Yield();

        connection.SetState(ConnectionState.Error, "Connection.Failed");

        await SettleAsync();

        Assert.False(routing.IsCompleted);

        connection.SetErrorCode("Auth.Unauthorized");

        TheForgeDeepLinkRouteResult result = await routing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Accepted);

        Assert.False(target.HasCalls);

    }

    /// <summary>
    /// Lets any continuation the coordinator scheduled actually run, so a "still waiting" assertion
    /// observes a settled coordinator rather than a scheduling race: the wait completes its
    /// TaskCompletionSource with RunContinuationsAsynchronously, so a bare yield can outrun the rejection.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromMilliseconds(50));

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

        public MutableArcanumConnection(ConnectionState state, string? lastErrorCode = null)
        {

            State = state;

            LastErrorCode = lastErrorCode;

        }

        public ConnectionState State { get; private set; }

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode { get; private set; }

        public string? LastErrorMessage => null;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

        /// <summary>
        /// Mirrors the health poller, which assigns LastErrorCode and raises its notification before
        /// flipping State, so a State observer always reads the code that caused the transition.
        /// </summary>
        public void SetState(ConnectionState state, string? errorCode = null)
        {

            SetErrorCode(errorCode);

            State = state;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

        }

        /// <summary>
        /// Changes only the error code, as a later failing poll does once State has already settled on
        /// Error and therefore raises no further State notification.
        /// </summary>
        public void SetErrorCode(string? errorCode)
        {

            LastErrorCode = errorCode;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastErrorCode)));

        }

    }

}
