using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.LongRest;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestTurnPreparationProtectedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_protected_debt_finishes_under_the_new_pending_request_before_real_assistant_begin(bool campaign)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            ownWorker: false, campaign: campaign);

        // Establish actual interrupted protected debt. Recovery can park it but cannot acquire authority.
        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", parked.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        Guid session = await f.H.SessionAsync(f.CampaignId);

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), f.Epoch.Capture().Stamp.BootId);

        GrimoireRepository repository = new(f.H.Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), null, null,
            FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), FixtureLabeledArtifactGuard.For(f.H.Saga.Context));

        CampaignRollupTurnSnapshotTests.ProbeStore rollups = new();

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Options)
            .AddSingleton<ICampaignRollupStore>(rollups)
            .AddSingleton<ICanonicalCampaignContextResolver>(new ExactScopeResolver(f.Scope))
            .AddSingleton<ICovenantOperationGate>(f.Gate)
            .AddSingleton<ICampaignRollupMaintenance>(new CampaignRollupTurnSnapshotTests.RefusingMaintenance())
            .AddSingleton<ISessionTurnClaimBeginStore>(repository)
            .AddSingleton<ISessionTurnBeginStore>(repository)
            .AddSingleton<ISessionTurnClaimCoordinator>(claims)
            .AddSingleton<ISessionTurnClaimLookup>(claims)
            .AddSingleton<ICovenantAuthoritySnapshotProvider>(new CovenantAuthoritySnapshotProvider(f.Runtime))
            .AddSingleton<ILongRestSweepCoordinator>(f.Coordinator)
            .AddSingleton<ILongRestMaintenance>(f.Maintenance)
            .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
            .AddSingleton<ILongRestSweepStore>(new LongRestSweepStore(f.H.Saga.Context, f.Clock))
            .AddSingleton<ILongRestDiscoveryStore>(f.H.Store)
            .AddSingleton<ILongRunningOperationStore>(f.Operations)
            .BuildServiceProvider();

        // Current constructors consume only current ports. No proposed constructor/type is referenced.
        CampaignRollupTurnPreparer preparer = ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(services);

        f.Handler.InsideSend = async () =>
        {
            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

            Assert.Equal(1L, await PendingClaimsAsync(f.H.Connection, session));

            Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))! > 0);

            f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";
        };

        PingRequest request = new("Continue after protected maintenance.", Model: "model", SessionId: session,
            CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            request, f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.NotNull(result.Value);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.NotNull(snapshot.ClaimLease);

        Assert.Equal(session, snapshot.ClaimLease!.Claim.SessionId);

        Assert.Equal(SessionTurnClaimState.PendingMaintenance, snapshot.ClaimLease.Claim.State);

        Assert.Equal(f.Scope, snapshot.Campaign);

        Assert.NotNull(snapshot.Authority);

        Assert.Null(snapshot.Artifact);

        Assert.Equal(0, rollups.Reads);

        Assert.True(f.Handler.SendCount > 0);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

        Result<AssistantReplyBeginReceipt> begun = await repository.BeginClaimedAssistantReplyAsync(
            snapshot.ClaimLease, f.Scope, request.Prompt, "model", CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        snapshot.MarkBegun();

        Assert.Equal(snapshot.ClaimLease.FutureAssistantEntryId, begun.Value.AssistantEntryId);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));
    }

    private static async Task<long> PendingClaimsAsync(SqliteConnection connection, Guid session)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM session_turn_claims WHERE SessionId = $session AND StateCode = 1;";

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ExactScopeResolver(CanonicalCampaignContext scope) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(scope));
    }
}
