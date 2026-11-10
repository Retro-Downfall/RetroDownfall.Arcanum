using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>
/// Dormant accounting kernel. Every caller supplies the admitted immediate transaction.
/// Runtime admission must supply the daily service even when its current gate is disabled;
/// an absent service supports dormant accounting, read-only consumers and isolated tests.
/// </summary>
internal sealed partial class GuildBudgetLedger(BudgetReservationService? daily = null) : IGuildQuestAllocationParticipant
{
    internal bool RequiresDailyReservation => daily?.RequiresReservation == true;

    public async Task<Result<Guid>> AllocateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
        Guid? parentAccountId, GuildGrant grant, CancellationToken cancellationToken)
    {
        if (GuildGrantPolicy.Validate(grant).IsFailure) return Invalid;

        Result<Guid> account = await AllocateAccountWithinAsync(connection, transaction, GuildBudgetOwnerKind.Quest,
            questId, parentAccountId, grant.Budget, cancellationToken).ConfigureAwait(false);

        if (account.IsFailure) return account.Error;

        await using SqliteCommand binding = Command(connection, transaction,
            "INSERT INTO guild_quest_budget(QuestId,AccountId) VALUES($quest,$account) ON CONFLICT(QuestId) DO NOTHING");

        Add(binding, "$quest", Id(questId));
        Add(binding, "$account", Id(account.Value));

        _ = await binding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return account;
    }

    internal async Task<Result<Guid>> AllocateAccountWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetOwnerKind kind, Guid ownerId, Guid? parentAccountId, GuildBudgetGrant grant, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || ownerId == Guid.Empty || parentAccountId == Guid.Empty
            || kind == GuildBudgetOwnerKind.Cohort && parentAccountId is null || !Valid(grant)) return Invalid;

        decimal? usd = Limit(grant.Usd);

        long? tokens = Limit(grant.Tokens);

        await using (SqliteCommand prior = Command(connection, transaction,
            "SELECT AccountId,ParentAccountId,UsdLimit,TokenLimit FROM guild_budget_accounts WHERE OwnerKindCode=$kind AND OwnerId=$owner"))
        {
            Add(prior, "$kind", (int)kind);
            Add(prior, "$owner", Id(ownerId));

            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return (reader.IsDBNull(1) ? (Guid?)null : Guid.Parse(reader.GetString(1))) == parentAccountId
                    && (reader.IsDBNull(2) ? (decimal?)null : ExactUsdText.Read(reader, 2)) == usd
                    && (reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3)) == tokens
                    ? Guid.Parse(reader.GetString(0)) : Conflict;
            }
        }

        Guid accountId = Guid.NewGuid();

        Guid rootId = accountId;

        if (parentAccountId is { } parentId)
        {
            Result open = await RequireOpenLineageAsync(connection, transaction, parentId, ct).ConfigureAwait(false);

            if (open.IsFailure) return open.Error;

            Result<Account> parent = await ReadAccountAsync(connection, transaction, parentId, ct).ConfigureAwait(false);

            if (parent.IsFailure) return parent.Error;

            if (parent.Value.Closed) return Closed;

            if (kind == GuildBudgetOwnerKind.Cohort && parent.Value.Kind != GuildBudgetOwnerKind.Quest) return Invalid;

            Result<GuildBudgetSnapshot> available = await ReadWithinAsync(connection, transaction, parentId, ct).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            if (!Fits(usd, tokens, available.Value)) return Exceeded;

            rootId = parent.Value.RootId;

            if (parent.Value.Revision == long.MaxValue) return Exhausted;
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_budget_accounts(AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,
                UsdLimit,TokenLimit,Closed,SettlementReceiptId,Revision)
            VALUES($id,$kind,$owner,$root,$parent,$usd,$tokens,0,NULL,1)
            """);

        Add(insert, "$id", Id(accountId));
        Add(insert, "$kind", (int)kind);
        Add(insert, "$owner", Id(ownerId));
        Add(insert, "$root", Id(rootId));
        Add(insert, "$parent", parentAccountId is { } parentValue ? Id(parentValue) : null);
        Add(insert, "$usd", usd is { } amount ? ExactUsdText.Format(amount) : null);
        Add(insert, "$tokens", tokens);

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (parentAccountId is { } changedParent)
        {
            Result changed = await TouchAsync(connection, transaction, changedParent, ct).ConfigureAwait(false);

            if (changed.IsFailure) return changed.Error;
        }

        return accountId;
    }

    internal async Task<Result> CloseAccountWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, Guid settlementReceiptId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || settlementReceiptId == Guid.Empty) return Invalid;

        Result<Account> found = await ReadAccountAsync(connection, transaction, accountId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        if (found.Value.Closed) return found.Value.Receipt == settlementReceiptId ? Result.Success() : Conflict;

        if (found.Value.Revision == long.MaxValue) return Exhausted;

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_budget_accounts SET Closed=1,SettlementReceiptId=$receipt,Revision=Revision+1
            WHERE AccountId=$account AND Revision=$revision AND Closed=0
            """);

        Add(command, "$account", Id(accountId));
        Add(command, "$receipt", Id(settlementReceiptId));
        Add(command, "$revision", found.Value.Revision);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : RevisionConflict;
    }

    private static async Task<Result<Account>> ReadAccountAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,Closed,SettlementReceiptId,Revision FROM guild_budget_accounts WHERE AccountId=$id");

        Add(command, "$id", Id(accountId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ParseAccount(reader) : Missing;
    }

    private static Account ParseAccount(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        (GuildBudgetOwnerKind)reader.GetInt32(1), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : ExactUsdText.Read(reader, 5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetBoolean(7), reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)), reader.GetInt64(9));

    private static async Task<Result> TouchAsync(SqliteConnection connection, SqliteTransaction transaction, Guid accountId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "UPDATE guild_budget_accounts SET Revision=Revision+1 WHERE AccountId=$id AND Revision<9223372036854775807");

        Add(command, "$id", Id(accountId));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Exhausted;
    }

    private async Task<Result> RequireOpenLineageAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, CancellationToken ct)
    {
        Result valid = await ValidateLineageAsync(connection, transaction, accountId, true, ct).ConfigureAwait(false);

        if (valid.IsFailure) return valid;

        Result<Account> account = await ReadAccountAsync(connection, transaction, accountId, ct).ConfigureAwait(false);

        if (account.IsFailure) return account.Error;

        Result<GuildBudgetSnapshot> root = await ReadWithinAsync(connection, transaction, account.Value.RootId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        // A preallocated sibling cannot hide a violation of a qualified provider bound. Evidence
        // and replay remain readable; all new authority in the funding tree stops pending reconciliation.
        return root.Value.CapabilityFault ? new Error(ErrorCodes.Guild.GrantExceeded,
            "A provider exceeded a qualified physical bound; this funding tree cannot admit new work.") : Result.Success();
    }

    private static async Task<Result> ValidateLineageAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, bool requireOpen, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            WITH RECURSIVE lineage(Id) AS (
                SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$id
                UNION
                SELECT a.ParentAccountId FROM guild_budget_accounts a JOIN lineage l ON a.AccountId=l.Id WHERE a.ParentAccountId IS NOT NULL
            )
            SELECT AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,Closed,SettlementReceiptId,Revision
            FROM guild_budget_accounts a JOIN lineage l ON a.AccountId=l.Id
            """);

        Add(command, "$id", Id(accountId));

        Dictionary<Guid, Account> lineage = [];

        try
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Account account = ParseAccount(reader);

                if (!Enum.IsDefined(account.Kind) || account.Usd < 0 || account.Tokens < 0 || account.Revision < 1) return Invalid;

                lineage.Add(account.Id, account);
            }
        }
        catch (FormatException)
        {
            return Invalid;
        }

        if (!lineage.TryGetValue(accountId, out Account? current)) return Missing;

        Guid rootId = current.RootId;

        HashSet<Guid> visited = [];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!visited.Add(current.Id) || current.RootId != rootId) return Invalid;

            if (requireOpen && current.Closed) return Closed;

            if (current.ParentId is not { } parentId)
                return current.Kind == GuildBudgetOwnerKind.Quest && current.Id == rootId ? Result.Success() : Invalid;

            if (!lineage.TryGetValue(parentId, out Account? parent)
                || current.Kind == GuildBudgetOwnerKind.Cohort && parent.Kind != GuildBudgetOwnerKind.Quest) return Invalid;

            current = parent;
        }
    }

    private static bool Valid(GuildBudgetGrant? grant) => grant is not null
        && GuildGrantPolicy.Validate(new([], [], [], grant, [], null)).IsSuccess;

    private static decimal? Limit(GuildUsdBudget axis) => axis.Mode == GuildBudgetMode.Unlimited ? null : axis.Value;

    private static long? Limit(GuildTokenBudget axis) => axis.Mode == GuildBudgetMode.Unlimited ? null : axis.Value;

    private static GuildBudgetGrant Grant(decimal? usd, long? tokens) => new(
        new(usd is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, usd),
        new(tokens is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, tokens));

    private static bool Fits(decimal? usd, long? tokens, GuildBudgetSnapshot snapshot)
    {
        if (snapshot.Closed || snapshot.CapabilityFault
            || snapshot.Grant.Usd.Mode == GuildBudgetMode.Finite && snapshot.KnownUsd > snapshot.Grant.Usd.Value
            || snapshot.Grant.Tokens.Mode == GuildBudgetMode.Finite && snapshot.KnownTokens > snapshot.Grant.Tokens.Value
            || snapshot.Remaining.Usd.Mode == GuildBudgetMode.Finite && (usd is null || usd > snapshot.Remaining.Usd.Value)
            || snapshot.Remaining.Tokens.Mode == GuildBudgetMode.Finite && (tokens is null || tokens > snapshot.Remaining.Tokens.Value)) return false;

        try
        {
            if (snapshot.Committed is { } committed)
            {
                decimal? projectedUsd = Sum(Limit(committed.Usd), usd);

                long? projectedTokens = Sum(Limit(committed.Tokens), tokens);

                if (snapshot.Grant.Usd.Mode == GuildBudgetMode.Finite && (projectedUsd is null || projectedUsd > snapshot.Grant.Usd.Value)
                    || snapshot.Grant.Tokens.Mode == GuildBudgetMode.Finite && (projectedTokens is null || projectedTokens > snapshot.Grant.Tokens.Value)) return false;
            }

            if (snapshot.Remaining.Usd.Mode == GuildBudgetMode.Finite && usd is { } amount)
                _ = ExactUsdText.CheckedAdd(snapshot.Remaining.Usd.Value!.Value, -amount);

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid id) => id.ToString("D").ToUpperInvariant();

    private sealed record Account(Guid Id, GuildBudgetOwnerKind Kind, Guid OwnerId, Guid RootId, Guid? ParentId,
        decimal? Usd, long? Tokens, bool Closed, Guid? Receipt, long Revision);

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The budget identity, dimensions or evidence are invalid.");

    private static Error Missing => new(ErrorCodes.Guild.NotFound, "The durable budget account or effect was not found.");

    private static Error Conflict => new(ErrorCodes.Guild.IdempotencyConflict, "The budget identity already binds different immutable facts.");

    private static Error Closed => new(ErrorCodes.Guild.Forbidden, "The budget account is closed to new work.");

    private static Error Exceeded => new(ErrorCodes.Guild.GrantExceeded, "The request exceeds the remaining exact budget authority.");

    private static Error Exhausted => new(ErrorCodes.Guild.SequenceExhausted, "The budget evidence revision is exhausted.");

    private static Error RevisionConflict => new(ErrorCodes.Guild.RevisionConflict, "The budget evidence changed before this mutation.");
}
