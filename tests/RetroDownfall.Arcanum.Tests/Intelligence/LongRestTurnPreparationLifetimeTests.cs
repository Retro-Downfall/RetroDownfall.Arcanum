using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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
public sealed class LongRestTurnPreparationLifetimeTests
{
    [Fact]
    public async Task Separate_protected_partitions_receive_the_latest_persisted_claim_and_return_it_for_assistant_begin()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        Guid source = Guid.NewGuid();

        await f.H.SeedAsync(source.ToString("D"), "A separate protected partition.", f.SourceSession);

        CovenantDigest second = await f.H.ProtectAsync(source, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        Assert.NotEqual(f.Partition.ProtectedPartitionDigest, second);

        await using Harness h = await Harness.CreateAsync(f);

        Result<CampaignRollupTurnSnapshot?> prepared = await h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = Assert.IsType<CampaignRollupTurnSnapshot>(prepared.Value);

        Assert.Equal(2, h.Maintenance.Inputs.Count);

        Assert.Equal(2, h.Coordinator.Renewed.Count);

        Assert.NotEqual(h.Maintenance.Inputs[0], h.Coordinator.Renewed[0]);

        Assert.Equal(h.Coordinator.Renewed[0], h.Maintenance.Inputs[1]);

        Assert.Equal(h.Coordinator.Renewed[1], snapshot.ClaimLease);

        Assert.Equal(2, h.Coordinator.Partitions.Select(partition => partition.ProtectedPartitionDigest).Distinct().Count());

        Assert.Null(snapshot.Artifact);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_or_cancelled_resume_discards_with_the_latest_persisted_claim(bool cancel)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await using Harness h = await Harness.CreateAsync(f);

        h.Coordinator.Fail = true;

        h.Coordinator.Cancel = cancel;

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None));
        }
        else
        {
            Result<CampaignRollupTurnSnapshot?> result = await h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.LongRest.StaleInput, result.Error.Code);
        }

        Assert.Equal(Assert.Single(h.Coordinator.Renewed), Assert.Single(h.Claims.Completed));

        Assert.NotEqual(Assert.Single(h.Maintenance.Inputs), h.Claims.Completed[0]);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_or_running_clean_work_does_not_resume_on_the_live_turn_or_discard_its_claim(bool running)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        Guid cleanSession = await f.H.SessionAsync(f.CampaignId);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Legitimate clean debt remains independently scheduled.", cleanSession);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "A second clean observation has separate content.", cleanSession);

        await using Harness h = await Harness.CreateAsync(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<LongRestSweepProgress>>? worker = null;

        Guid cleanOperation = Guid.Empty;

        f.Handler.InsideSend = async () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted clean summary.\"}";

            entered.TrySetResult();

            if (running) await release.Task;
        };

        h.Coordinator.AfterCleanRequest = async operation =>
        {
            cleanOperation = operation.Id;

            Assert.Equal(LongRunningOperationState.Pending, operation.State);

            if (running)
            {
                worker = f.Coordinator.ResumeAsync(operation.Id, null, CancellationToken.None);

                Task reached = await Task.WhenAny(entered.Task, worker).WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Same(entered.Task, reached);

                Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(operation.Id))!.State);
            }
        };

        ArcanumInvocationContext ordinary = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, f.Scope, InvocationAttendance.Attended,
            CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        try
        {
            Result<CampaignRollupTurnSnapshot?> prepared = await h.Preparer.PrepareLiveAsync(h.Request, ordinary, false, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

            await using CampaignRollupTurnSnapshot snapshot = Assert.IsType<CampaignRollupTurnSnapshot>(prepared.Value);

            Assert.NotEqual(Guid.Empty, cleanOperation);

            Assert.Equal(0, h.Coordinator.LiveCleanResumes);

            Assert.Empty(h.Claims.Completed);

            Assert.Equal(SessionTurnClaimState.PendingMaintenance, snapshot.ClaimLease!.Claim.State);

            Assert.Equal(running ? LongRunningOperationState.Running : LongRunningOperationState.Pending,
                (await f.Operations.GetAsync(cleanOperation))!.State);

            Result<AssistantReplyBeginReceipt> begun = await h.Repository.BeginClaimedAssistantReplyAsync(
                snapshot.ClaimLease, f.Scope, h.Request.Prompt, "model", CancellationToken.None);

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

            snapshot.MarkBegun();

            Assert.Empty(h.Claims.Completed);

            Assert.NotEqual(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
        }
        finally
        {
            release.TrySetResult();

            if (worker is not null)
            {
                Result<LongRestSweepProgress> completed = await worker.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

                Assert.Equal(LongRestSweepPhase.Completed, completed.Value.Phase);

                Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(cleanOperation))!.State);
            }
        }
    }

    [Fact]
    public async Task Throwing_claim_settlement_still_releases_the_genuine_borrowed_turn_lease()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await using Harness h = await Harness.CreateAsync(f);

        h.Coordinator.Fail = true;

        h.Claims.ThrowOnComplete = true;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None));

        Assert.Equal("Settlement failed.", failure.Message);

        CovenantTurnLease borrowed = Assert.Single(h.Maintenance.Authorities);

        Result held = await f.Gate.ValidateTurnLeaseAsync(borrowed, CancellationToken.None);

        Assert.True(held.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, held.Error.Code);

        Assert.Equal(Assert.Single(h.Coordinator.Renewed), Assert.Single(h.Claims.Completed));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Long_rest_only_snapshot_refuses_configuration_ABA_with_no_rollup_artifact()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await using Harness h = await Harness.CreateAsync(f);

        Result<CampaignRollupTurnSnapshot?> prepared = await h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = Assert.IsType<CampaignRollupTurnSnapshot>(prepared.Value);

        Assert.Null(snapshot.Artifact);

        Assert.True((await snapshot.ValidateAsync(CancellationToken.None)).IsSuccess);

        ArcanumSettings original = f.Options.CurrentValue;

        f.Options.Set(original with { Features = original.Features with { LongRest = false } });

        f.Options.Set(original);

        Result current = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(current.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, current.Error.Code);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal CampaignRollupTurnPreparer Preparer = null!;

        internal PingRequest Request = null!;

        internal GrimoireRepository Repository = null!;

        internal ClaimProbe Claims = null!;

        internal MaintenanceProbe Maintenance = null!;

        internal RenewingCoordinator Coordinator = null!;

        private ServiceProvider _services = null!;

        internal static async Task<Harness> CreateAsync(LongRestMaintenanceTests.MaintenanceFixture f)
        {
            _ = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

            Guid session = await f.H.SessionAsync(f.CampaignId);

            SessionTurnClaimStore native = new(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), f.Epoch.Capture().Stamp.BootId);

            GrimoireRepository repository = new(f.H.Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
                new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), null, null,
                FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), FixtureLabeledArtifactGuard.For(f.H.Saga.Context), memoryDelegation: null);

            Harness h = new() { Claims = new(native), Maintenance = new(f.Maintenance), Coordinator = new(f), Repository = repository };

            h._services = new ServiceCollection()
                .AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Options)
                .AddSingleton<ICampaignRollupStore>(new CampaignRollupTurnSnapshotTests.ProbeStore())
                .AddSingleton<ICanonicalCampaignContextResolver>(new Resolver(f.Scope))
                .AddSingleton<ICanonicalCampaignRootEnricher>(new PassThroughRootEnricher())
                .AddSingleton<ICovenantOperationGate>(f.Gate)
                .AddSingleton<ISessionTurnClaimBeginStore>(repository)
                .AddSingleton<ISessionTurnBeginStore>(repository)
                .AddSingleton<ISessionTurnClaimCoordinator>(h.Claims)
                .AddSingleton<ISessionTurnClaimLookup>(native)
                .AddSingleton<ICovenantAuthoritySnapshotProvider>(new CovenantAuthoritySnapshotProvider(f.Runtime))
                .AddSingleton<ILongRestSweepCoordinator>(h.Coordinator)
                .AddSingleton<ILongRestMaintenance>(h.Maintenance)
                .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
                .AddSingleton<ILongRestDiscoveryStore>(f.H.Store)
                .BuildServiceProvider();

            h.Preparer = ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(h._services);

            h.Request = new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true);

            f.Handler.InsideSend = () =>
            {
                f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                    ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

                return Task.CompletedTask;
            };

            return h;
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class Resolver(CanonicalCampaignContext scope) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(scope));
    }

    private sealed class ClaimProbe(SessionTurnClaimStore inner) : ISessionTurnClaimCoordinator
    {
        internal List<SessionTurnClaimLease> Completed = [];

        internal bool ThrowOnComplete;

        public ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken ct) => inner.AcquireAsync(request, ct);

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken ct) => inner.MarkBegunAsync(lease, begin, ct);

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken ct)
        {
            Completed.Add(lease);

            if (ThrowOnComplete) throw new InvalidOperationException("Settlement failed.");

            return inner.CompleteAsync(lease, outcome, ct);
        }
    }

    private sealed class MaintenanceProbe(ILongRestMaintenance inner) : ILongRestMaintenance
    {
        internal List<SessionTurnClaimLease> Inputs = [];

        internal List<CovenantTurnLease> Authorities = [];

        public Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operation, ArcanumInvocationContext invocation,
            SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct)
        {
            Inputs.Add(claim);

            Authorities.Add(lease);

            return inner.CreateCapabilityAsync(operation, invocation, claim, scope, lease, ct);
        }
    }

    private sealed class RenewingCoordinator(LongRestMaintenanceTests.MaintenanceFixture f) : ILongRestSweepCoordinator
    {
        internal bool Fail;

        internal int LiveCleanResumes;

        internal Func<LongRunningOperationDto, Task>? AfterCleanRequest;

        internal bool Cancel;

        internal List<SessionTurnClaimLease> Renewed = [];

        internal List<LongRestSweepScope> Partitions = [];

        public async Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            Result<LongRunningOperationDto> result = await f.Coordinator.RequestAsync(scope, ct);

            if (result.IsSuccess && AfterCleanRequest is not null) await AfterCleanRequest(result.Value);

            return result;
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
        {
            Partitions.Add(partition);

            return f.Coordinator.RequestAsync(scope, partition, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct)
        {
            Partitions.Add(partition);

            return f.Coordinator.RequestAsync(scope, partition, authority, ct);
        }

        public async Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operation, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            if (capability is null)
            {
                LiveCleanResumes++;

                return await f.Coordinator.ResumeAsync(operation, null, ct);
            }

            LongRestAttemptStore attempts = new(f.H.Saga.Context, f.Clock);

            SessionTurnClaimLease renewed = LongRestSweepHarness.Require(await attempts.RenewClaimAsync(capability.Claim, capability.CanonicalScope, ct));

            Assert.True((await capability.RefreshClaimAsync(renewed, capability.Configuration, ct)).IsSuccess);

            if (Fail)
            {
                Renewed.Add(capability.Claim);

                if (Cancel) throw new OperationCanceledException();

                return new Error(ErrorCodes.LongRest.StaleInput, "The renewed request was refused.");
            }

            Result<LongRestSweepProgress> result = await f.Coordinator.ResumeAsync(operation, capability, ct);

            Renewed.Add(capability.Claim);

            return result;
        }
    }
}
