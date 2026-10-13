using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Fact]
    public async Task LongRestTurnPreparation_resolver_configuration_ABA_refuses_the_next_assistant_send()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        // Keep the scripted client exclusive to assistant inference in this dispatch-fence control.
        // The production-default Lexicon extractor otherwise makes an earlier auxiliary call.
        f.Options.Set(f.Options.CurrentValue with { Features = f.Options.CurrentValue.Features with { Lexicon = false } });

        await Task7ParkAsync(f);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Task7SuccessfulMaintenanceResponses(f, h);

        ScriptingChatClient chat = new();

        int transitions = 0;

        Task7SecurityResolver resolver = new(f.Scope, () =>
        {
            Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

            ArcanumSettings original = f.Options.CurrentValue;

            f.Options.Set(original with { Features = original.Features with { LongRest = false } });

            f.Options.Set(original);

            transitions++;
        });

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Options)
            .AddSingleton<ICampaignRollupStore>(h.Rollups)
            .AddSingleton<ICanonicalCampaignContextResolver>(resolver)
            .AddSingleton<ICovenantOperationGate>(f.Gate)
            .AddSingleton<ISessionTurnClaimBeginStore>(h.Begin)
            .AddSingleton<ISessionTurnBeginStore>(h.Begin)
            .AddSingleton<ISessionTurnClaimCoordinator>(h.Claims)
            .AddSingleton<ISessionTurnClaimLookup>(h.Claims)
            .AddSingleton<ISessionTurnClaimReplayStore>(h.Repository)
            .AddSingleton<ICovenantAuthoritySnapshotProvider>(new CovenantAuthoritySnapshotProvider(f.Runtime))
            .AddSingleton<ILongRestSweepCoordinator>(h.Coordinator)
            .AddSingleton<ILongRestMaintenance>(h.Maintenance)
            .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
            .AddSingleton<ILongRestDiscoveryStore>(h.Discovery)
            .BuildServiceProvider();

        CampaignRollupTurnPreparer preparer = ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(services);

        chat.EnqueueText("must not be sent after the resolver changes configuration");

        WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
            ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: preparer,
            claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(h.Request, f.Invocation, CancellationToken.None);

        Assert.True(f.Handler.SendCount > 0, "The real protected maintenance must precede dispatch validation.");

        Assert.True(transitions > 0);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.Single(h.Claims.Acquisitions);

        Assert.Equal(0, h.Rollups.Reads);
    }

    private sealed class Task7SecurityResolver(CanonicalCampaignContext scope, Action duringResolution) : ICanonicalCampaignContextResolver
    {
        public async ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();

            cancellationToken.ThrowIfCancellationRequested();

            duringResolution();

            return scope;
        }
    }
}
