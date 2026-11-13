using Microsoft.Data.Sqlite;

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
}
