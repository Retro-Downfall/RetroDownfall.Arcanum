using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("unlimited", true)]
    [InlineData("usd-equal", true)]
    [InlineData("tokens-equal", true)]
    [InlineData("usd-narrow", true)]
    [InlineData("tokens-narrow", true)]
    [InlineData("usd-missing", false)]
    [InlineData("usd-wider", false)]
    [InlineData("tokens-missing", false)]
    [InlineData("tokens-wider", false)]
    [InlineData("missing-parent", false)]
    [InlineData("dropped-parent", false)]
    [InlineData("wrong-root", false)]
    [InlineData("missing-owner", false)]
    [InlineData("receipt-invalid", false)]
    [InlineData("receipt-tokens", false)]
    [InlineData("receipt-unknown-tokens", false)]
    public async Task Physical_retention_validates_nested_quest_funding_and_current_receipt(string scenario, bool expected)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestStored middle = await f.ChildAsync(root, await f.DriverAsync(root));

        GuildQuestStored leaf = await f.ChildAsync(middle, await f.DriverAsync(middle));

        GuildQuestDriver driver = await f.DriverAsync(leaf);

        Guid dispatch = Guid.NewGuid(), run = Guid.NewGuid(), effect = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("nested-retention"), run, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction,
                new(effect, leaf.Quest.Id, dispatch, run, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null), default)).IsSuccess);

            Assert.True((await ledger.ExposeAsync(connection, transaction, leaf.Quest.Id, leaf.AllocationAccountId, dispatch, run, default)).IsSuccess);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, effect,
                new(Guid.NewGuid(), ReceiptDigest("nested-retention-evidence"), 1m, 10, false, false, true), default)).IsSuccess);

            await transaction.CommitAsync();
        }

        await using SqliteCommand mutate = connection.CreateCommand();

        mutate.Parameters.AddWithValue("$root", root.AllocationAccountId.ToString("D").ToUpperInvariant());

        mutate.Parameters.AddWithValue("$middle", middle.AllocationAccountId.ToString("D").ToUpperInvariant());

        mutate.Parameters.AddWithValue("$leaf", leaf.AllocationAccountId.ToString("D").ToUpperInvariant());

        mutate.Parameters.AddWithValue("$effect", effect.ToString("D").ToUpperInvariant());

        // Simulate persisted corruption only after creating the real Quest hierarchy and evidence.
        mutate.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_accounts_guard_update; DROP TRIGGER guild_budget_evidence_guard_update";

        await mutate.ExecuteNonQueryAsync();

        mutate.CommandText = scenario switch
        {
            "usd-equal" => "UPDATE guild_budget_accounts SET UsdLimit='10'",
            "tokens-equal" => "UPDATE guild_budget_accounts SET TokenLimit=100",
            "usd-narrow" => "UPDATE guild_budget_accounts SET UsdLimit=CASE AccountId WHEN $root THEN '10' WHEN $middle THEN '8' ELSE '5' END",
            "tokens-narrow" => "UPDATE guild_budget_accounts SET TokenLimit=CASE AccountId WHEN $root THEN 100 WHEN $middle THEN 80 ELSE 50 END",
            "usd-missing" => "UPDATE guild_budget_accounts SET UsdLimit=CASE AccountId WHEN $root THEN '10' WHEN $middle THEN '8' ELSE NULL END",
            "usd-wider" => "UPDATE guild_budget_accounts SET UsdLimit=CASE AccountId WHEN $root THEN '10' WHEN $middle THEN '8' ELSE '9' END",
            "tokens-missing" => "UPDATE guild_budget_accounts SET TokenLimit=CASE AccountId WHEN $root THEN 100 WHEN $middle THEN 80 ELSE NULL END",
            "tokens-wider" => "UPDATE guild_budget_accounts SET TokenLimit=CASE AccountId WHEN $root THEN 100 WHEN $middle THEN 80 ELSE 90 END",
            "dropped-parent" => "UPDATE guild_budget_accounts SET ParentAccountId=NULL WHERE AccountId=$leaf",
            "missing-parent" => "UPDATE guild_budget_accounts SET ParentAccountId='AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA' WHERE AccountId=$leaf",
            "wrong-root" => "UPDATE guild_budget_accounts SET RootAccountId=$middle WHERE AccountId=$leaf",
            "missing-owner" => "DELETE FROM guild_quest_budget WHERE AccountId=$middle",
            "receipt-invalid" => "UPDATE guild_budget_evidence SET ReceiptId='not-a-guid' WHERE EffectId=$effect",
            "receipt-tokens" => "UPDATE guild_budget_evidence SET KnownTokens=9 WHERE EffectId=$effect",
            "receipt-unknown-tokens" => "UPDATE guild_budget_evidence SET UnknownTokens=1 WHERE EffectId=$effect",
            _ => "SELECT 1"
        };

        await mutate.ExecuteNonQueryAsync();

        string key = run.ToString("N");

        Dictionary<string, bool> result = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.Equal(expected, result[key]);
    }
    [Fact]
    public async Task Physical_retention_pins_shared_run_if_one_valid_origin_loses_its_root()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        GuildQuestStored first = await f.ChildAsync(root, rootDriver);

        GuildQuestStored second = await f.ChildAsync(root, rootDriver);

        GuildQuestStored[] origins = [first, second];

        Array.Sort(origins, (left, right) => StringComparer.Ordinal.Compare(
            left.AllocationAccountId.ToString("D"), right.AllocationAccountId.ToString("D")));

        Guid run = Guid.NewGuid();

        foreach (GuildQuestStored origin in origins)
        {
            GuildQuestDriver driver = await f.DriverAsync(origin);

            Guid dispatch = Guid.NewGuid(), effect = Guid.NewGuid();

            Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("shared-run"), run, false, default)).IsSuccess);

            await using SqliteConnection write = await f.OpenAsync();

            await using SqliteTransaction transaction = write.BeginTransaction(deferred: false);

            Assert.True((await ledger.PrepareEffectWithinAsync(write, transaction,
                new(effect, origin.Quest.Id, dispatch, run, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null), default)).IsSuccess);

            Assert.True((await ledger.ExposeAsync(write, transaction, origin.Quest.Id, origin.AllocationAccountId, dispatch, run, default)).IsSuccess);

            Assert.True((await ledger.ApplyEvidenceWithinAsync(write, transaction, effect,
                new(Guid.NewGuid(), ReceiptDigest("shared-run-evidence"), 1m, 10, false, false, true), default)).IsSuccess);

            await transaction.CommitAsync();
        }

        await using SqliteConnection connection = await f.OpenAsync();

        string key = run.ToString("N");

        var before = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.True(before[key]);

        // Break only the first origin's parent edge: its account, owner, root reference,
        // dispatch and complete receipt still exist, while the intact origin sorts later.
        await using SqliteCommand corrupt = connection.CreateCommand();

        corrupt.CommandText = "PRAGMA ignore_check_constraints=ON; DROP TRIGGER guild_budget_accounts_guard_update";

        await corrupt.ExecuteNonQueryAsync();

        corrupt.CommandText = "UPDATE guild_budget_accounts SET ParentAccountId=AccountId WHERE AccountId=$account";

        corrupt.Parameters.AddWithValue("$account", origins[0].AllocationAccountId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());

        var after = await GuildPhysicalRetentionProof.ReadPageAsync(connection, null, [key], default);

        Assert.False(after[key]);
    }

}
