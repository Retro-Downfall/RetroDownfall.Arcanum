using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildBudgetLedgerTests
{
    [Fact]
    public async Task Nonrepresentable_finite_remainder_is_refused_before_child_write()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(decimal.MaxValue, 100), default)).Value;

        Result<Guid> refused = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root, Grant(0.1m, 1), default);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, refused.Error.Code);

        Assert.Equal(decimal.MaxValue,
            (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value.Remaining.Usd.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonrepresentable_sibling_allocations_fail_before_child_write(bool usdOverflow)
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(null, null), default)).Value;

        Assert.True((await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root,
            usdOverflow ? Grant(decimal.MaxValue, 0) : Grant(0m, long.MaxValue), default)).IsSuccess);

        Result<Guid> refused = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root,
            usdOverflow ? Grant(0.1m, 0) : Grant(0m, 1), default);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, refused.Error.Code);

        await using SqliteCommand count = connection.CreateCommand();

        count.Transaction = transaction;

        count.CommandText = "SELECT count(*) FROM guild_budget_accounts";

        Assert.Equal(2L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Sibling_grants_partition_the_parent_and_refused_allocation_leaves_no_partial_child()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Result<Guid> root = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(10m, 100), default);

        Assert.True(root.IsSuccess, root.Error.Message);

        Result<Guid> first = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), root.Value, Grant(7m, 70), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Result<Guid> refused = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root.Value, Grant(4m, 40), default);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, refused.Error.Code);

        Result<GuildBudgetSnapshot> state = await ledger.ReadWithinAsync(connection, transaction, root.Value, default);

        Assert.True(state.IsSuccess, state.Error.Message);

        Assert.Equal(3m, state.Value.Remaining.Usd.Value);

        Assert.Equal(30L, state.Value.Remaining.Tokens.Value);

        await using SqliteCommand count = connection.CreateCommand();

        count.Transaction = transaction;

        count.CommandText = "SELECT count(*) FROM guild_budget_accounts";

        Assert.Equal(2L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Closed_cohort_retains_active_descendants_then_returns_only_proven_unused_authority_once()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(10m, 100), default)).Value;

        Guid cohort = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), root, Grant(8m, 80), default)).Value;

        Guid child = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), cohort, Grant(6m, 60), default)).Value;

        Assert.Equal(2m, (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value.Remaining.Usd.Value);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), cohort, Grant(1m, 1), default)).Error.Code);

        Guid receipt = Guid.NewGuid();

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, cohort, receipt, default)).IsSuccess);

        Assert.Equal(4m, (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value.Remaining.Usd.Value);

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, cohort, receipt, default)).IsSuccess);

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, child, Guid.NewGuid(), default)).IsSuccess);

        Assert.Equal(10m, (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value.Remaining.Usd.Value);

        Assert.Equal(ErrorCodes.Guild.Forbidden, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), cohort, Grant(1m, 1), default)).Error.Code);
    }

    private static GuildBudgetGrant Grant(decimal? usd, long? tokens) => new(
        new(usd is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, usd),
        new(tokens is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, tokens));

    [Fact]
    public async Task Account_identity_replay_cannot_change_parent_or_grant_and_unlimited_requires_unlimited_parent()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid owner = Guid.NewGuid();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, owner, null, Grant(10m, 100), default)).Value;

        Assert.Equal(root, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, owner, null, Grant(10m, 100), default)).Value);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, owner, null, Grant(11m, 100), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.GrantExceeded, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root, Grant(null, 10), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            (GuildBudgetOwnerKind)99, Guid.NewGuid(), root, Grant(1m, 1), default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.Empty, root, Grant(1m, 1), default)).Error.Code);
    }

    [Fact]
    public async Task Unlimited_parent_preserves_usage_and_finite_child_allocation_without_inventing_a_ceiling()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(null, null), default)).Value;

        Assert.True((await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), root, Grant(null, null), default)).IsSuccess);

        Assert.True((await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), root, Grant(2m, 20), default)).IsSuccess);

        GuildBudgetSnapshot snapshot = (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value;

        Assert.Equal(GuildBudgetMode.Unlimited, snapshot.Remaining.Usd.Mode);

        Assert.Equal(GuildBudgetMode.Unlimited, snapshot.OutstandingAllocation.Tokens.Mode);

        Assert.False(snapshot.UnknownUsd);

        Assert.Equal(0m, snapshot.KnownUsd);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", default);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

        return connection;
    }

    [Fact]
    public async Task Closing_ancestor_prevents_new_descendant_authority_and_retains_existing_obligations()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(10m, 100), default)).Value;

        Guid cohort = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Cohort, Guid.NewGuid(), root, Grant(8m, 80), default)).Value;

        Guid child = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), cohort, Grant(6m, 60), default)).Value;

        Assert.True((await ledger.CloseAccountWithinAsync(connection, transaction, cohort, Guid.NewGuid(), default)).IsSuccess);

        Result<Guid> refused = await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), child, Grant(1m, 1), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, refused.Error.Code);

        Assert.Equal(4m, (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value.Remaining.Usd.Value);
    }

    [Fact]
    public async Task Corrupt_ancestor_cycle_cannot_be_interpreted_as_a_valid_subtree()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(10m, 100), default)).Value;

        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        await using SqliteCommand corrupt = connection.CreateCommand();

        corrupt.Transaction = transaction;

        corrupt.CommandText = """
            PRAGMA defer_foreign_keys=ON;
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,Closed,Revision)
            VALUES($first,0,$first,$root,$second,'1',1,0,1),($second,0,$second,$root,$first,'1',1,0,1);
            """;

        corrupt.Parameters.AddWithValue("$first", first.ToString("D").ToUpperInvariant());
        corrupt.Parameters.AddWithValue("$second", second.ToString("D").ToUpperInvariant());
        corrupt.Parameters.AddWithValue("$root", root.ToString("D").ToUpperInvariant());

        _ = await corrupt.ExecuteNonQueryAsync();

        Assert.True((await ledger.ReadWithinAsync(connection, transaction, first, default)).IsFailure);
    }

    [Fact]
    public async Task Deep_account_tree_is_read_iteratively_without_recounting_nested_grants()
    {
        await using SqliteConnection connection = await OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetLedger ledger = new();

        Guid root = (await ledger.AllocateAccountWithinAsync(connection, transaction,
            GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, Grant(1m, 1), default)).Value;

        Guid parent = root;

        for (int i = 0; i < 256; i++)
        {
            Result<Guid> child = await ledger.AllocateAccountWithinAsync(connection, transaction,
                GuildBudgetOwnerKind.Quest, Guid.NewGuid(), parent, Grant(1m, 1), default);

            Assert.True(child.IsSuccess, child.Error.Message);

            parent = child.Value;
        }

        GuildBudgetSnapshot snapshot = (await ledger.ReadWithinAsync(connection, transaction, root, default)).Value;

        Assert.Equal(0m, snapshot.Remaining.Usd.Value);

        Assert.Equal(1m, snapshot.OutstandingAllocation.Usd.Value);

        Assert.Equal(0m, snapshot.KnownUsd);
    }
}
