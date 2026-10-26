using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    private const string ProtectedMaintenanceStatus = "Finishing protected memory maintenance...";

    [Fact]
    public async Task LongRestStatus_a_streamed_turn_with_protected_debt_says_so_once_before_the_assistant_begins()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await Task7ParkAsync(f);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Task7SuccessfulMaintenanceResponses(f, h);

        ScriptingChatClient chat = new();

        chat.EnqueueText("{\"entities\":[]}");

        chat.EnqueueText("answer after protected maintenance");

        WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
            ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
            claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository,
            healthTracker: new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance));

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
            f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.NoTools, f.Invocation.ReadAuthorityEpoch).Value;

        List<IntelligenceEvent> events = [];

        int sendsWhenStatusArrived = -1;

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None))
        {
            if (evt.Type == IntelligenceEventType.Status && evt.Message == ProtectedMaintenanceStatus) sendsWhenStatusArrived = f.Handler.SendCount;

            events.Add(evt);
        }

        IntelligenceEvent status = Assert.Single(events, static evt => evt.Type == IntelligenceEventType.Status && evt.Message == ProtectedMaintenanceStatus);

        // Said before the maintenance it announces did its paid work, and before any assistant token.
        Assert.Equal(0, sendsWhenStatusArrived);

        Assert.True(f.Handler.SendCount > 0);

        int statusIndex = events.IndexOf(status);

        int firstToken = events.FindIndex(static evt => evt.Type == IntelligenceEventType.Token);

        Assert.True(firstToken < 0 || statusIndex < firstToken);
    }

    // The announcement exists for a client that is streaming. A buffered turn has no client waiting on a stream, so it neither emits the
    // event to its buffered sinks nor holds the preparer for an acknowledgement.
    [Fact]
    public async Task LongRestStatus_a_buffered_turn_with_protected_debt_neither_says_it_nor_holds_the_preparer()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await Task7ParkAsync(f);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Task7SuccessfulMaintenanceResponses(f, h);

        ScriptingChatClient chat = new();

        chat.EnqueueText("{\"entities\":[]}");

        chat.EnqueueText("answer after protected maintenance");

        WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
            ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
            claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository,
            healthTracker: new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance));

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
            f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.NoTools, f.Invocation.ReadAuthorityEpoch).Value;

        List<TurnEvent> events = [];

        await foreach (TurnEvent evt in new TurnEngine(wizard).RunTurnAsync(
            new TurnExecutionRequest(h.Request, invocation, TurnResponseMode.Buffered), CancellationToken.None))
        {
            events.Add(evt);
        }

        // The protected maintenance still ran under this turn; only the announcement is a streaming matter.
        Assert.True(f.Handler.SendCount > 0);

        Assert.Contains(events, static evt => evt is RunCompleted);

        Assert.DoesNotContain(events, static evt => evt is TurnStatusChanged { Message: ProtectedMaintenanceStatus });

        // Between the protected request that finds work still to finish and the first capability request for that work the preparer does
        // nothing but signal the caller, and a preparer held for an acknowledgement waits out its five-second bound there for a caller that
        // never acknowledges. Only that interval is timed, so the extraction, the sends and the database work of the rest of the turn cannot
        // fail the test on a slow runner, and a held preparer still does (its interval is at least five seconds).
        Assert.NotEqual(0L, h.Coordinator.ProtectedRequestReturned);

        Assert.NotEqual(0L, h.Maintenance.FirstCapability);

        TimeSpan held = System.Diagnostics.Stopwatch.GetElapsedTime(h.Coordinator.ProtectedRequestReturned, h.Maintenance.FirstCapability);

        Assert.True(held < TimeSpan.FromSeconds(4), $"the preparer waited {held} between the protected request and its first paid work");
    }

    [Fact]
    public async Task LongRestStatus_a_streamed_turn_with_no_protected_partition_never_says_it()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("{\"entities\":[]}");

        chat.EnqueueText("answer without protected maintenance");

        WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
            ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
            claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository,
            healthTracker: new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance));

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
            f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.NoTools, f.Invocation.ReadAuthorityEpoch).Value;

        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None)) events.Add(evt);

        Assert.DoesNotContain(events, static evt => evt.Type == IntelligenceEventType.Status && evt.Message == ProtectedMaintenanceStatus);
    }
}
