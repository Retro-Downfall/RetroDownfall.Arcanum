using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_source_verification_refuses_forged_physical_source_binding()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_spend_sources_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_spend_sources SET SourceId=$forged WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$forged", Guid.NewGuid().ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(0.5, "real")]
    [InlineData("invalid", "text")]
    public async Task Provider_source_verification_refuses_non_integer_physical_effect_kind(object forgedKind, string storageType)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand pragma = connection.CreateCommand();

        pragma.CommandText = "PRAGMA ignore_check_constraints=ON";

        await pragma.ExecuteNonQueryAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_budget_effects_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_budget_effects SET KindCode=$kind WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$kind", forgedKind);

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT typeof(KindCode) FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.Equal(storageType, await command.ExecuteScalarAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("SourceKindCode", 1.5, "real")]
    [InlineData("SourceKindCode", "1invalid", "text")]
    [InlineData("SourceVersion", 1.5, "real")]
    [InlineData("SourceVersion", "1invalid", "text")]
    public async Task Provider_source_verification_refuses_non_integer_source_selectors(string column, object forged, string storageType)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand pragma = connection.CreateCommand();

        pragma.CommandText = "PRAGMA ignore_check_constraints=ON";

        await pragma.ExecuteNonQueryAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_spend_sources_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = $"UPDATE guild_spend_sources SET {column}=$value WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$value", forged);

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = $"SELECT typeof({column}) FROM guild_spend_sources WHERE EffectId=$effect";

        Assert.Equal(storageType, await command.ExecuteScalarAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Provider_source_verification_refuses_generation_that_disagrees_with_initial_bill()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_spend_sources_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_spend_sources SET GenerationId='gen-forged' WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Provider_observation_identity_converges_for_same_verified_source_and_separates_physical_calls()
    {
        await using SpendScenario first = await SpendScenario.CreateAsync();

        await using SpendScenario second = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential.VerifiedSource firstSource = await first.VerifiedAsync();

        GuildSpendSourceCredential.VerifiedSource firstAgain = await first.VerifiedAsync();

        GuildSpendSourceCredential.VerifiedSource otherSource = await second.VerifiedAsync();

        var observed = ReadArchivedSpendObservation(firstSource);

        Assert.True(observed.IsSuccess, observed.IsFailure ? observed.Error.Message : null);

        byte[] original = GuildSpendReceiptIdentity.ProviderDigest(observed.Value);

        Assert.Equal(32, original.Length);

        Assert.True(observed.Value.IsFrom(firstAgain));

        Assert.False(observed.Value.IsFrom(otherSource));

        var replay = ReadArchivedSpendObservation(firstAgain);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.Equal(original, GuildSpendReceiptIdentity.ProviderDigest(replay.Value));

        var other = ReadArchivedSpendObservation(otherSource);

        Assert.True(other.IsSuccess, other.IsFailure ? other.Error.Message : null);

        Assert.NotEqual(original, GuildSpendReceiptIdentity.ProviderDigest(other.Value));

        (int Input, int Output, int Cached, int Reasoning, string Usd)[] variants =
        [
            (101, 20, 40, 5, "0.25"),
            (100, 21, 40, 5, "0.25"),
            (100, 20, 41, 5, "0.25"),
            (100, 20, 40, 6, "0.25"),
            (100, 20, 40, 5, "0.30"),
        ];

        foreach ((int input, int output, int cached, int reasoning, string usd) in variants)
        {
            string body = $$$"""
                {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
                "native_tokens_cached":{{{cached}}},"native_tokens_completion":{{{output}}},"native_tokens_completion_images":0,
                "native_tokens_prompt":{{{input}}},"native_tokens_reasoning":{{{reasoning}}},"num_input_audio_prompt":0,
                "num_media_completion":0,"num_media_prompt":0,"total_cost":{{{usd}}}}}
                """;

            string changedHandler = body;

            var changed = ReadArchivedSpendObservation(firstSource, changedHandler);

            Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.Message : null);

            Assert.NotEqual(original, GuildSpendReceiptIdentity.ProviderDigest(changed.Value));
        }
    }

    [Theory]
    [InlineData("DispatchId")]
    [InlineData("AccountingAssociationId")]
    [InlineData("ReservationId")]
    public async Task Provider_source_verification_refuses_effect_association_that_disagrees_with_initial_bill(string column)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteCommand pragma = connection.CreateCommand();

        pragma.CommandText = "PRAGMA foreign_keys=OFF";

        await pragma.ExecuteNonQueryAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_budget_effects_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = $"UPDATE guild_budget_effects SET {column}=$forged WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$forged", Guid.NewGuid().ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Provider_source_verification_refuses_pricing_that_disagrees_with_initial_bill()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_spend_sources_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE guild_spend_sources SET PricingSnapshotJson='forged-pricing' WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Provider_observation_identity_separates_real_physical_calls_in_one_installation()
    {
        await using SpendScenario first = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential.VerifiedSource firstSource = await first.VerifiedAsync();

        var observed = ReadArchivedSpendObservation(firstSource);

        Assert.True(observed.IsSuccess, observed.IsFailure ? observed.Error.Message : null);

        await using SqliteConnection connection = await first.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db);

        first.Fixture.UseBudgetParticipant(new GuildBudgetLedger(daily));

        Guid key = Guid.NewGuid();

        var issued = await first.Fixture.Store.IssueAsync(first.Fixture.Caller,
            first.Fixture.Identity with { LogicalCallId = key, IssuingStepId = Guid.NewGuid() },
            first.Fixture.Request with { IdempotencyKey = key }, default);

        Assert.True(issued.IsSuccess, issued.IsFailure ? issued.Error.Message : null);

        GuildQuestStored quest = issued.Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "router", SpendModel, [], 30);

        var bound = await first.Fixture.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.IsFailure ? bound.Error.Message : null);

        GuildQuestDriver driver = await first.Fixture.DriverAsync(quest);

        var prepared = await first.Fixture.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("second-provider-spend"), quest.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        Guid secondEffect = Guid.NewGuid();

        GuildBudgetEffectRequest effect = new(secondEffect, quest.Quest.Id, prepared.Value.DispatchId, quest.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 1000);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BillableOperationRecord bill = new(quest.Quest.RunId, BillableOperationType.Chat, "router", SpendModel, "guild",
            now, now.AddSeconds(1), 100, 20, 5, 40, "{}", 0m, BillableOperationStatus.Completed, "gen-test-123");

        var completed = await first.Fixture.Store.ExecuteAndSettleProviderResponseAsync(new(first.Fixture.Store, driver, prepared.Value),
            daily, effect, _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill),
            default, spendSource: first.Credential(), spendPricing: "{}");

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

        Assert.NotEqual(first.EffectId, secondEffect);

        var verified = await first.VerifyAsync(first.Credential(), secondEffect);

        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Message : null);

        Assert.False(observed.Value.IsFrom(verified.Value));

        var secondObservation = ReadArchivedSpendObservation(verified.Value);

        Assert.True(secondObservation.IsSuccess, secondObservation.IsFailure ? secondObservation.Error.Message : null);

        Assert.NotEqual(GuildSpendReceiptIdentity.ProviderDigest(observed.Value),
            GuildSpendReceiptIdentity.ProviderDigest(secondObservation.Value));
    }

    [Fact]
    public async Task Provider_observation_identity_normalizes_equivalent_exact_usd_spellings()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential.VerifiedSource source = await scenario.VerifiedAsync();

        var first = ReadArchivedSpendObservation(source);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        string equivalentHandler = """
            {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
            "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
            "native_tokens_prompt":100,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
            "num_media_completion":0,"num_media_prompt":0,"total_cost":0.250}}
            """;

        var equivalent = ReadArchivedSpendObservation(source, equivalentHandler);

        Assert.True(equivalent.IsSuccess, equivalent.IsFailure ? equivalent.Error.Message : null);

        Assert.Equal(first.Value.Amounts, equivalent.Value.Amounts);

        Assert.Equal(GuildSpendReceiptIdentity.ProviderDigest(first.Value),
            GuildSpendReceiptIdentity.ProviderDigest(equivalent.Value));
    }
}
