using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_refresh_effect_read_reports_eligible_for_actual_valid_source()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        int gets = 0;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long refreshes = await f.Scenario.Fixture.CountAsync("guild_spend_refreshes");

        var effect = await service.ReadEffectAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, default);

        Assert.True(effect.IsSuccess, effect.IsFailure ? effect.Error.Message : null);

        Assert.Null(effect.Value.ProviderIneligibility);

        var page = await service.ListEffectsAsync(f.Scenario.Fixture.Caller, f.QuestId, null, 1, default);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

        GuildSpendEffectDto item = Assert.Single(page.Value.Items);

        Assert.Equal(f.Scenario.EffectId, item.EffectId);

        Assert.Null(item.ProviderIneligibility);

        Assert.Equal(0, gets);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(refreshes, await f.Scenario.Fixture.CountAsync("guild_spend_refreshes"));
    }

    [Fact]
    public async Task Provider_refresh_none_capture_reports_disabled_without_credentials_or_new_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ArcanumSettings settings = new() { Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1",
            Models = [SpendModel], GuildUsageReconciliation = GuildUsageReconciliationSource.None }] };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("none-eligibility"), quest.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.Value.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var captured = await f.Store.ExecuteAndSettleProviderResponseAsync(new(f.Store, driver, dispatch.Value), daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default);

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

        await using var query = connection.CreateCommand();

        query.CommandText = "SELECT SourceKindCode FROM guild_spend_sources WHERE EffectId=$effect";

        query.Parameters.AddWithValue("$effect", effect.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(0L, await query.ExecuteScalarAsync());

        query.CommandText = "DELETE FROM guild_spend_authority_state";

        Assert.Equal(1, await query.ExecuteNonQueryAsync());

        using GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        CountingRefreshResolver resolver = new();

        int gets = 0;

        GuildSpendRefreshService service = new(f.Store, configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.CountAsync("LongRunningOperations");

        long reservations = await f.CountAsync("BudgetReservations");

        var read = await service.ReadEffectAsync(f.Caller, quest.Quest.Id, effect.EffectId, default);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        Assert.Equal("reconciliationDisabled", read.Value.ProviderIneligibility);

        var page = await service.ListEffectsAsync(f.Caller, quest.Quest.Id, null, 1, default);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

        Assert.Equal("reconciliationDisabled", Assert.Single(page.Value.Items).ProviderIneligibility);

        settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration;

        var enabledLater = await service.ReadEffectAsync(f.Caller, quest.Quest.Id, effect.EffectId, default);

        Assert.True(enabledLater.IsSuccess, enabledLater.IsFailure ? enabledLater.Error.Message : null);

        Assert.Equal("sourceUnavailable", enabledLater.Value.ProviderIneligibility);

        Assert.Equal(0, resolver.Reads);

        Assert.Equal(0, gets);

        Assert.Equal(operations, await f.CountAsync("LongRunningOperations"));

        Assert.Equal(reservations, await f.CountAsync("BudgetReservations"));

        Assert.Equal(0, await f.CountAsync("guild_spend_refreshes"));
    }

    [Theory]
    [InlineData("missingBill", "sourceExpired")]
    [InlineData("missingReservation", "sourceUnavailable")]
    [InlineData("aliasReservation", "sourceUnavailable")]
    [InlineData("authority", "sourceUnavailable")]
    [InlineData("disabled", "reconciliationDisabled")]
    public async Task Provider_refresh_eligibility_revalidates_actual_evidence_without_get(string mutation, string reason)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        if (mutation == "disabled") f.Scenario.Settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None;
        else
        {
            f.Query.CommandText = mutation switch
            {
                "missingBill" => "DELETE FROM BillableOperations",
                "missingReservation" => "DELETE FROM BudgetReservations",
                "aliasReservation" => "UPDATE BudgetReservations SET Id=substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21)",
                "authority" => "DELETE FROM guild_spend_authority_state",
                _ => throw new InvalidOperationException(),
            };

            Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());
        }

        int gets = 0;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        long reservations = await f.Scenario.Fixture.CountAsync("BudgetReservations");

        var effect = await service.ReadEffectAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, default);

        Assert.True(effect.IsSuccess, effect.IsFailure ? effect.Error.Message : null);

        Assert.Equal(reason, effect.Value.ProviderIneligibility);

        Assert.Equal(0, gets);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));

        Assert.Equal(reservations, await f.Scenario.Fixture.CountAsync("BudgetReservations"));
    }

    [Fact]
    public async Task Provider_refresh_list_keeps_cursor_members_and_reuses_currentness_per_page()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = [SpendModel],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
            CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N") };

        ArcanumSettings settings = new() { Providers = [provider] };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(quest);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("eligibility-page"), quest.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.IsFailure ? dispatch.Error.Message : null);

        GuildLocalInferenceExecution execution = new(f.Store, driver, dispatch.Value);

        List<Guid> expected = [];

        for (int i = 0; i < 3; i++)
        {
            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), quest.Quest.Id, dispatch.Value.DispatchId, quest.Quest.RunId,
                GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
                now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

            var captured = await f.Store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new(provider, SpendModel, SpendSecret), spendPricing: "{}");

            Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

            expected.Add(effect.EffectId);
        }

        Guid remoteId = Guid.NewGuid();

        await using (var transaction = connection.BeginTransaction())
        {
            GuildBudgetLedger ledger = new(daily);

            GuildBudgetEffectRequest remote = new(remoteId, quest.Quest.Id, dispatch.Value.DispatchId, quest.Quest.RunId,
                GuildBudgetEffectKind.External, Guid.NewGuid(), null, null, null);

            var prepared = await ledger.PrepareEffectWithinAsync(connection, transaction, remote, default);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

            var exposed = await ledger.ExposeEffectWithinAsync(connection, transaction, remoteId, prepared.Value.Revision, default);

            Assert.True(exposed.IsSuccess, exposed.IsFailure ? exposed.Error.Message : null);

            var applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction, remoteId,
                new(Guid.NewGuid(), ReceiptDigest("eligibility-remote"), .5m, 12, false, false, true), default);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);

            await transaction.CommitAsync();
        }

        expected.Add(remoteId);

        using GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<ArcanumSettings>(settings));

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

        using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

        CountingEligibilityResolver resolver = new(new ProviderApiKeyResolver(credentials));

        int gets = 0;

        GuildSpendRefreshService service = new(f.Store, configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.CountAsync("LongRunningOperations");

        long reservations = await f.CountAsync("BudgetReservations");

        var first = await service.ListEffectsAsync(f.Caller, quest.Quest.Id, null, 2, default);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.Equal(2, first.Value.Items.Length);

        Assert.NotNull(first.Value.NextCursor);

        Assert.All(first.Value.Items, item => Assert.Null(item.ProviderIneligibility));

        Assert.Equal(1, resolver.Peeks);

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first.Value, GuildJsonContext.Default.GuildSpendEffectPageDto).Length <= GuildQuestStore.TreePageMaximumBytes);

        var second = await service.ListEffectsAsync(f.Caller, quest.Quest.Id, first.Value.NextCursor, 2, default);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);

        Assert.Equal(2, second.Value.Items.Length);

        Assert.Null(second.Value.NextCursor);

        Assert.Null(second.Value.Items[0].ProviderIneligibility);

        Assert.Equal("remoteEffect", second.Value.Items[1].ProviderIneligibility);

        Assert.Equal(expected, first.Value.Items.Concat(second.Value.Items).Select(item => item.EffectId));

        Assert.Equal(2, resolver.Peeks);

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(second.Value, GuildJsonContext.Default.GuildSpendEffectPageDto).Length <= GuildQuestStore.TreePageMaximumBytes);

        Assert.Equal(0, gets);

        Assert.Equal(operations, await f.CountAsync("LongRunningOperations"));

        Assert.Equal(reservations, await f.CountAsync("BudgetReservations"));

        Assert.Equal(0, await f.CountAsync("guild_spend_refreshes"));
    }

    private sealed class CountingEligibilityResolver(IProviderApiKeyResolver inner) : IProviderApiKeyResolver
    {
        internal int Peeks { get; private set; }

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default) => inner.ResolveAsync(provider, ct);

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default)
        {
            Peeks++;

            return inner.PeekWithIdentityAsync(provider, ct);
        }

        public Task<ProviderCredentialCurrentness> GetCurrentnessAsync(ProviderSettings provider, ProviderCredentialStamp stamp,
            CancellationToken ct = default) => inner.GetCurrentnessAsync(provider, stamp, ct);

        public ProviderCredentialCurrentness GetObservedLocalCurrentness(ProviderSettings provider, ProviderCredentialStamp stamp) =>
            inner.GetObservedLocalCurrentness(provider, stamp);
    }

    [Fact]
    public async Task Provider_refresh_eligibility_refuses_a_different_real_credential()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), "different-private-key");

        using ProviderCredentialStore credentials = new(os, new EphemeralDataProtectionProvider());

        int gets = 0;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, new ProviderApiKeyResolver(credentials),
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        long operations = await f.Scenario.Fixture.CountAsync("LongRunningOperations");

        var effect = await service.ReadEffectAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, default);

        Assert.True(effect.IsSuccess, effect.IsFailure ? effect.Error.Message : null);

        Assert.Equal("sourceUnavailable", effect.Value.ProviderIneligibility);

        Assert.Equal(0, gets);

        Assert.Equal(operations, await f.Scenario.Fixture.CountAsync("LongRunningOperations"));
    }

    [Fact]
    public async Task Provider_refresh_eligibility_refuses_configuration_change_during_credential_observation()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        CallbackEligibilityResolver resolver = new(f.Resolver,
            () => f.Scenario.Settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None);

        int gets = 0;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        var effect = await service.ReadEffectAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, default);

        Assert.True(effect.IsSuccess, effect.IsFailure ? effect.Error.Message : null);

        Assert.Equal("sourceUnavailable", effect.Value.ProviderIneligibility);

        Assert.Equal(1, resolver.Peeks);

        Assert.Equal(0, gets);
    }

    private sealed class CallbackEligibilityResolver(IProviderApiKeyResolver inner, Action afterPeek) : IProviderApiKeyResolver
    {
        internal int Peeks { get; private set; }

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default) => inner.ResolveAsync(provider, ct);

        public async Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default)
        {
            Peeks++;

            ResolvedProviderCredential result = await inner.PeekWithIdentityAsync(provider, ct);

            afterPeek();

            return result;
        }

        public Task<ProviderCredentialCurrentness> GetCurrentnessAsync(ProviderSettings provider, ProviderCredentialStamp stamp,
            CancellationToken ct = default) => inner.GetCurrentnessAsync(provider, stamp, ct);

        public ProviderCredentialCurrentness GetObservedLocalCurrentness(ProviderSettings provider, ProviderCredentialStamp stamp) =>
            inner.GetObservedLocalCurrentness(provider, stamp);
    }

    [Fact]
    public async Task Provider_refresh_eligibility_keeps_effect_read_available_when_credential_observation_fails()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        int gets = 0;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, new ThrowingEligibilityResolver(),
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => { gets++; return new SpendMetadataHandler(); }),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        var effect = await service.ReadEffectAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, default);

        Assert.True(effect.IsSuccess, effect.IsFailure ? effect.Error.Message : null);

        Assert.Equal("sourceUnavailable", effect.Value.ProviderIneligibility);

        Assert.Equal(0, gets);
    }

    private sealed class ThrowingEligibilityResolver : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default) =>
            Task.FromException<string?>(new InvalidOperationException("injected credential failure"));

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default) =>
            Task.FromException<ResolvedProviderCredential>(new InvalidOperationException("injected credential failure"));
    }

    [Fact]
    public async Task Provider_refresh_eligibility_propagates_cancellation_during_credential_observation()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        using CancellationTokenSource cancellation = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, new CancelEligibilityResolver(cancellation),
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new SpendMetadataHandler()),
            null!, null!, null!, null!, TimeProvider.System, new GuildSpendRefreshTracker());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadEffectAsync(f.Scenario.Fixture.Caller,
            f.QuestId, f.Scenario.EffectId, cancellation.Token));
    }

    private sealed class CancelEligibilityResolver(CancellationTokenSource cancellation) : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken ct = default)
        {
            cancellation.Cancel();

            return Task.FromCanceled<ResolvedProviderCredential>(cancellation.Token);
        }
    }
}
