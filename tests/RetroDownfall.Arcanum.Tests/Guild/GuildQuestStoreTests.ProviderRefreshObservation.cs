using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_reader_observation_cannot_be_changed_by_copying_its_facts()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        GuildSpendSourceCredential.VerifiedSource source = await scenario.VerifiedAsync();

        using SpendMetadataHandler handler = new();

        var observed = await new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler).ReadAsync(source, default);

        Assert.True(observed.IsSuccess, observed.IsFailure ? observed.Error.Message : null);

        byte[] original = GuildSpendReceiptIdentity.ProviderDigest(observed.Value);

        var changedCopy = observed.Value.Amounts with { InputTokens = 999 };

        Assert.Equal(999, changedCopy.InputTokens);

        Assert.Equal(100, observed.Value.Amounts.InputTokens);

        Assert.Equal(original, GuildSpendReceiptIdentity.ProviderDigest(observed.Value));
    }

    [Theory]
    [InlineData("Provider")]
    [InlineData("Model")]
    public async Task Provider_source_verification_rejects_matching_configuration_that_disagrees_with_original_bill(string column)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER guild_spend_sources_guard_update";

        await command.ExecuteNonQueryAsync();

        command.CommandText = $"UPDATE guild_spend_sources SET {column}=$forged WHERE EffectId=$effect";

        command.Parameters.AddWithValue("$forged", column == "Provider" ? "forged-router" : "forged/model");

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        ProviderSettings provider = scenario.Settings.Providers[0] with { };

        string model = SpendModel;

        if (column == "Provider") provider.Name = "forged-router";

        else model = "forged/model";

        GuildSpendSourceCredential credential = new(provider, model, SpendSecret);

        var verified = await credential.VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        await transaction.RollbackAsync();
    }


    [Fact]
    public async Task Provider_source_verification_refuses_pruned_original_bill_before_metadata_read()
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DELETE FROM BillableOperations WHERE lower(replace(Id,'-',''))=(SELECT lower(replace(SourceId,'-','')) FROM guild_budget_effects WHERE EffectId=$effect)";

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var verified = await scenario.Credential().VerifyWithinAsync(connection, transaction, scenario.EffectId,
            scenario.Fixture.Host.InstallationGeneration, default);

        Assert.True(verified.IsFailure);

        Assert.Equal(ErrorCodes.Guild.SourceExpired, verified.Error.Code);

        await transaction.RollbackAsync();
    }


    [Theory]
    [InlineData("StartedAt")]
    [InlineData("ActualCostUsd")]
    public async Task Provider_source_verification_refuses_corrupt_bill_values_without_throwing(string column)
    {
        await using SpendScenario scenario = await SpendScenario.CreateAsync();

        await using SqliteConnection connection = await scenario.Fixture.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"UPDATE BillableOperations SET {column}='not-a-valid-value' WHERE lower(replace(Id,'-',''))=(SELECT lower(replace(SourceId,'-','')) FROM guild_budget_effects WHERE EffectId=$effect)";

        command.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendSourceCredential.VerifiedSource>? result = null;

        Exception? thrown = await Record.ExceptionAsync(async () => result = await scenario.Credential().VerifyWithinAsync(
            connection, transaction, scenario.EffectId, scenario.Fixture.Host.InstallationGeneration, default));

        Assert.Null(thrown);

        Assert.NotNull(result);

        Assert.True(result.IsFailure);

        await transaction.RollbackAsync();
    }

}
