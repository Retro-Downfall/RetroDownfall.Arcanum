using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Deleted_ancestor_owner_refuses_real_descendant_refresh_retry_and_recovery()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new();

        ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [SpendModel],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
            CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N") };

        settings.Providers = [provider];

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestStored child = await f.ChildAsync(root, await f.DriverAsync(root));

        Assert.NotEqual(root.Quest.SessionId, child.Quest.SessionId);

        GuildQuestDriver driver = await f.DriverAsync(child);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("ancestor-refresh"), child.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch.Value);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), child.Quest.Id, dispatch.Value.DispatchId, child.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(child.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var captured = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
            spendSource: new(provider, SpendModel, SpendSecret), spendPricing: "{}");

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

        using GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

        using ProviderCredentialStore credentialStore = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentialStore);

        var currentness = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, resolver, "router", SpendModel, default);

        Assert.True(currentness.IsSuccess, currentness.IsFailure ? currentness.Error.Message : null);

        Guid retryRefresh = Guid.NewGuid();

        var live = await f.Store.PrepareProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, retryRefresh, default);

        Assert.True(live.IsSuccess, live.IsFailure ? live.Error.Message : null);

        var retryAdmitted = await f.Store.AdmitProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, retryRefresh, currentness.Value, default);

        Assert.True(retryAdmitted.IsSuccess, retryAdmitted.IsFailure ? retryAdmitted.Error.Message : null);

        using LongRunningOperationStore operations = new(db, f.Connections);

        Guid retryId = retryAdmitted.Value.OperationId!.Value;

        LongRunningOperation retryPending = (await operations.GetAsync(retryId))!;

        Assert.True(await operations.TryTransitionAsync(retryId, retryPending.Revision, null, LongRunningOperationState.ReconciliationRequired,
            now, ErrorCodes.Guild.Unavailable));

        LongRunningOperation parked = (await operations.GetAsync(retryId))!;

        Guid recoveryRefresh = Guid.NewGuid();

        var recoveryAdmitted = await f.Store.AdmitProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, recoveryRefresh, currentness.Value, default);

        Assert.True(recoveryAdmitted.IsSuccess, recoveryAdmitted.IsFailure ? recoveryAdmitted.Error.Message : null);

        Guid recoveryId = recoveryAdmitted.Value.OperationId!.Value;

        var lease = await operations.TryAcquireLeaseAsync(recoveryId, "ancestor-recovery", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(recoveryId, out Guid claim));

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-ancestor-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        int gets = 0;

        GuildSpendRefreshService service = new(f.Store, configuration, resolver, new GuildOpenRouterSpendReader(new PublicSpendDns(), () =>
        {
            gets++;

            return new SpendMetadataHandler();
        }), new RefreshTestReadiness(hostLock), f.Gate, operations, ownership, TimeProvider.System, new GuildSpendRefreshTracker());

        Guid acceptedRefresh = Guid.NewGuid();

        var accepted = await service.RefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, acceptedRefresh, default);

        Assert.True(accepted.IsSuccess, accepted.IsFailure ? accepted.Error.Message : null);

        Assert.NotNull(accepted.Value.Receipt);

        Assert.Equal(1, gets);

        using (CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention))
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            delete.Parameters.AddWithValue("$id", root.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var replay = await service.RefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, acceptedRefresh, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(accepted.Value.Receipt, replay.Value.Receipt);

        Assert.Equal(1, gets);

        var historical = await f.Store.ReadOperatorSpendEffectAsync(f.Caller, child.Quest.Id, effect.EffectId, default);

        Assert.True(historical.IsSuccess, historical.IsFailure ? historical.Error.Message : null);

        var selected = await f.Store.SelectProviderSpendRefreshSourceAsync(f.Caller, child.Quest.Id, effect.EffectId, default);

        Assert.True(selected.IsFailure);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, selected.Error.Code);

        var directAdmission = await f.Store.AdmitProviderSpendRefreshAsync(f.Caller, child.Quest.Id, effect.EffectId,
            Guid.NewGuid(), currentness.Value, default);

        Assert.True(directAdmission.IsFailure);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, directAdmission.Error.Code);

        long beforeOperations = await f.CountAsync("LongRunningOperations");

        var fresh = await service.RefreshAsync(f.Caller, child.Quest.Id, effect.EffectId, Guid.NewGuid(), default);

        Assert.True(fresh.IsFailure);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, fresh.Error.Code);

        Assert.Equal(beforeOperations, await f.CountAsync("LongRunningOperations"));

        var retried = await service.RetryAsync(f.Caller, parked, default);

        Assert.True(retried.IsFailure);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, retried.Error.Code);

        Assert.Equal(parked.Revision, (await operations.GetAsync(retryId))!.Revision);

        var recovered = await service.RecoverClaimedAsync(f.Caller, lease.Operation, default);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);

        Assert.Equal(1, gets);

        Assert.True(ownership.Release(recoveryId, claim));
    }
}
