using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("usd-bound-equal", true)]
    [InlineData("usd-bound-exceeded", false)]
    [InlineData("token-bound-equal", true)]
    [InlineData("token-bound-exceeded", false)]
    [InlineData("negative-token-bound", false)]
    [InlineData("negative-usd-bound", false)]
    [InlineData("negative-known-tokens", false)]
    [InlineData("negative-known-usd", false)]
    [InlineData("invalid-state", false)]
    public async Task Physical_retention_checks_exact_effect_bounds_and_state(string scenario, bool expected)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await ExecuteAsync("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_effects_guard_update; DROP TRIGGER guild_budget_evidence_guard_update");

        string mutation = scenario switch
        {
            "usd-bound-equal" => "UPDATE guild_budget_effects SET UsdBound='1.5'",
            "usd-bound-exceeded" => "UPDATE guild_budget_effects SET UsdBound='1.4'",
            "token-bound-equal" => "UPDATE guild_budget_effects SET TokenBound=2",
            "token-bound-exceeded" => "UPDATE guild_budget_effects SET TokenBound=1",
            "negative-token-bound" => "UPDATE guild_budget_effects SET TokenBound=-1",
            "negative-usd-bound" => "UPDATE guild_budget_effects SET UsdBound='-1'",
            "negative-known-tokens" => "UPDATE guild_budget_effects SET KnownTokens=-1; UPDATE guild_budget_evidence SET KnownTokens=-1",
            "negative-known-usd" => "UPDATE guild_budget_effects SET KnownUsd='-1'; UPDATE guild_budget_evidence SET KnownUsd='-1'",
            _ => "UPDATE guild_budget_effects SET StateCode=99"
        };

        await ExecuteAsync(mutation);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.Equal(expected, proof[key]);
    }

    [SkippableTheory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Reservation_validation_rejects_invalid_effect_identity(string effect)
    {
        RequireSqlCipher();

        Guid reservation = Guid.NewGuid();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true, reservation: reservation);

        await ExecuteAsync("""
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            VALUES(@id,@run,'2000-01-01',1,@at,@at,@at,'2','1.5');
            PRAGMA foreign_keys=OFF;
            PRAGMA ignore_check_constraints=ON;
            DROP TRIGGER guild_budget_effects_guard_update;
            UPDATE guild_budget_effects SET EffectId=@effect;
            """, ("@id", reservation.ToString("N")), ("@run", BudgetPhysicalRun.ToString("N")), ("@at", OldTimestamp), ("@effect", effect));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        Assert.False(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction,
            Guid.Parse("DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD"), default));
    }

    [SkippableFact]
    public async Task Physical_retention_does_not_pin_a_run_without_guild_effects()
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string unrelated = Guid.NewGuid().ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [unrelated], default);

        Assert.True(proof[unrelated]);
    }

    [SkippableTheory]
    [InlineData("zero", true)]
    [InlineData("usd", false)]
    [InlineData("tokens", false)]
    [InlineData("unknown-usd", false)]
    [InlineData("unknown-tokens", false)]
    public async Task Physical_retention_requires_zero_known_and_unknown_usage_for_proven_no_dispatch(string scenario, bool expected)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(2, true, false, authentic: true);

        await ExecuteAsync("PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_effects_guard_update; DROP TRIGGER guild_budget_evidence_guard_update");

        string usd = scenario == "usd" ? "1" : "0";

        int tokens = scenario == "tokens" ? 1 : 0;

        int unknownUsd = scenario == "unknown-usd" ? 1 : 0;

        int unknownTokens = scenario == "unknown-tokens" ? 1 : 0;

        await ExecuteAsync("""
            UPDATE guild_budget_effects SET StateCode=3,KnownUsd=@usd,KnownTokens=@tokens,UnknownUsd=@unknownUsd,UnknownTokens=@unknownTokens;
            UPDATE guild_budget_evidence SET KnownUsd=@usd,KnownTokens=@tokens,UnknownUsd=@unknownUsd,UnknownTokens=@unknownTokens,NoDispatchProven=1;
            """, ("@usd", usd), ("@tokens", tokens), ("@unknownUsd", unknownUsd), ("@unknownTokens", unknownTokens));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.Equal(expected, proof[key]);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Physical_retention_pins_nonterminal_effect_with_matching_incomplete_receipt(int state)
    {
        RequireSqlCipher();

        await SeedGuildPhysicalRetentionAsync(state, true, false, authentic: true);

        await ExecuteAsync("DROP TRIGGER guild_budget_evidence_guard_update; UPDATE guild_budget_evidence SET Complete=0");

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        string key = BudgetPhysicalRun.ToString("N");

        var proof = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.False(proof[key]);
    }
}
