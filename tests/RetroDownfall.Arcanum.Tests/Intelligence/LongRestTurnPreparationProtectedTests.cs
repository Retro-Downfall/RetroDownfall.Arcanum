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

        (CampaignRollupTurnPreparer preparer, GrimoireRepository repository, CampaignRollupTurnSnapshotTests.ProbeStore rollups, Guid session) =
            await BuildAsync(f, f.Coordinator);

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

    [Fact]
    public async Task A_busy_clean_scope_is_skipped_but_a_busy_protected_partition_stays_fatal_for_the_turn()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Decorated coordinator = new(f.Coordinator) { Clean = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy."), Partition = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy.") };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        // The busy clean scope was skipped (the protected loop was reached); the busy protected partition ended the turn.
        Assert.Equal(1, coordinator.CleanRequests);

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        // The claim the turn acquired was terminalized, not left pending.
        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Fact]
    public async Task A_protected_resume_that_is_busy_or_fails_still_ends_the_turn()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Decorated coordinator = new(f.Coordinator) { Resume = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy.") };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.Equal(1, coordinator.Resumes);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task The_progress_signal_completes_before_the_first_protected_request_when_a_protected_partition_exists()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        CampaignPreparationProgress progress = new();

        bool signalledBeforeRequest = false;

        Decorated coordinator = new(f.Coordinator)
        {
            Partition = () =>
            {
                signalledBeforeRequest = progress.ProtectedMaintenanceStarted.IsCompleted;

                return new Error(ErrorCodes.LongRest.Unavailable, "Stop here.");
            },
        };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        Assert.True(result.IsFailure);

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.True(signalledBeforeRequest);

        Assert.True(progress.ProtectedMaintenanceStarted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task The_progress_signal_never_completes_for_a_turn_with_no_protected_partition()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        CampaignPreparationProgress progress = new();

        Decorated coordinator = new(f.Coordinator);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Equal(0, coordinator.PartitionRequests);

        Assert.False(progress.ProtectedMaintenanceStarted.IsCompleted);
    }

    /// <summary>Delegates to the real coordinator unless a member is scripted to answer instead.</summary>
    private sealed class Decorated(ILongRestSweepCoordinator inner) : ILongRestSweepCoordinator
    {
        internal Func<Error?>? Clean { get; init; }

        internal Func<Error?>? Partition { get; init; }

        internal Func<Error?>? Resume { get; init; }

        internal int CleanRequests;

        internal int PartitionRequests;

        internal int Resumes;

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            CleanRequests++;

            return Clean?.Invoke() is { } error ? Task.FromResult(Result<LongRunningOperationDto>.Failure(error)) : inner.RequestAsync(scope, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
        {
            PartitionRequests++;

            return Partition?.Invoke() is { } error ? Task.FromResult(Result<LongRunningOperationDto>.Failure(error)) : inner.RequestAsync(scope, partition, ct);
        }

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            Resumes++;

            return Resume?.Invoke() is { } error ? Task.FromResult(Result<LongRestSweepProgress>.Failure(error)) : inner.ResumeAsync(operationId, capability, ct);
        }
    }

    private static async Task<(CampaignRollupTurnPreparer Preparer, GrimoireRepository Repository, CampaignRollupTurnSnapshotTests.ProbeStore Rollups, Guid Session)>
        BuildAsync(LongRestMaintenanceTests.MaintenanceFixture f, ILongRestSweepCoordinator coordinator)
    {
        Guid session = await f.H.SessionAsync(f.CampaignId);

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), f.Epoch.Capture().Stamp.BootId);

        GrimoireRepository repository = new(f.H.Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), null, null,
            FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), FixtureLabeledArtifactGuard.For(f.H.Saga.Context));

        CampaignRollupTurnSnapshotTests.ProbeStore rollups = new();

        ServiceProvider services = new ServiceCollection()
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
            .AddSingleton(coordinator)
            .AddSingleton<ILongRestMaintenance>(f.Maintenance)
            .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
            .AddSingleton<ILongRestSweepStore>(new LongRestSweepStore(f.H.Saga.Context, f.Clock))
            .AddSingleton<ILongRestDiscoveryStore>(f.H.Store)
            .AddSingleton<ILongRunningOperationStore>(f.Operations)
            .BuildServiceProvider();

        // Current constructors consume only current ports. No proposed constructor/type is referenced.
        return (ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(services), repository, rollups, session);
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
