using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    private static async Task<(DateTimeOffset Now, GuildQuestStore Store, GuildBudgetEffectRequest Effect, BudgetReservationService Daily)>
        CaptureCancellationRefreshAsync(Fixture f, GuildCallerContext caller, GuildQuestDto quest, ArcanumDbContext db,
            LongRunningOperationStore operations)
    {
        var recovery = await f.Policy.CreateRecoveryAsync(caller, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.IsFailure ? recovery.Error.Message : null);

        var execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.IsFailure ? execution.Error.Message : null);

        var run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.IsFailure ? run.Error.Message : null);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var lease = await operations.TryAcquireLeaseAsync(quest.OperationId, "quest-owner", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        GuildQuestDriver driver = new(execution.Value, "quest-owner", lease.Operation.Revision, run.Value);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('A', 64), quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"],
            CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N") };

        f.Settings.CurrentValue.Providers = [provider];

        BudgetReservationService daily = new(db);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, readiness: f.Readiness);

        GuildLocalInferenceExecution physical = new(store, driver, dispatch.Value);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Id, dispatch.Value.DispatchId, quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        BillableOperationRecord bill = new(quest.RunId, BillableOperationType.Chat, "router", "provider/model", "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var sent = await store.ExecuteAndSettleProviderResponseAsync(physical, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
            spendSource: new(provider, "provider/model", "private-provider-key-sentinel"), spendPricing: "{}");

        Assert.True(sent.IsSuccess, sent.IsFailure ? sent.Error.Message : null);

        return (now, store, effect, daily);
    }
}
