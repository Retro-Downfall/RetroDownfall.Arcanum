using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildBudgetLedger
{
    internal async Task<Result<GuildBudgetSnapshot>> ReadWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return Invalid;

        try
        {
            Result lineage = await ValidateLineageAsync(connection, transaction, accountId, false, ct).ConfigureAwait(false);

            if (lineage.IsFailure) return lineage.Error;

            Dictionary<Guid, Account> accounts = [];

            await using (SqliteCommand command = Command(connection, transaction, Subtree + """
                SELECT AccountId,OwnerKindCode,OwnerId,RootAccountId,ParentAccountId,UsdLimit,TokenLimit,
                    Closed,SettlementReceiptId,Revision FROM guild_budget_accounts WHERE AccountId IN(SELECT Id FROM subtree)
                """))
            {
                Add(command, "$account", Id(accountId));

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    Account account = ParseAccount(reader);

                    if (!Enum.IsDefined(account.Kind) || account.Revision < 1 || account.Usd < 0 || account.Tokens < 0) return Invalid;

                    accounts.Add(account.Id, account);
                }
            }

            if (!accounts.TryGetValue(accountId, out Account? root)) return Missing;

            Dictionary<Guid, List<Guid>> children = [];

            Dictionary<Guid, Totals> sums = accounts.Keys.ToDictionary(id => id, _ => new Totals());

            foreach (Account account in accounts.Values)
            {
                if (account.RootId != root.RootId) return Invalid;

                if (account.Id == accountId) continue;

                if (account.ParentId is not { } parent || !accounts.ContainsKey(parent)) return Invalid;

                if (account.Kind == GuildBudgetOwnerKind.Cohort && accounts[parent].Kind != GuildBudgetOwnerKind.Quest) return Invalid;

                if (!children.TryGetValue(parent, out List<Guid>? list)) children[parent] = list = [];

                list.Add(account.Id);
            }

            await using (SqliteCommand effects = Command(connection, transaction, Subtree + """
                SELECT e.AccountId,e.UsdBound,e.TokenBound,e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.StateCode
                FROM guild_budget_effects e WHERE e.AccountId IN(SELECT Id FROM subtree)
                """))
            {
                Add(effects, "$account", Id(accountId));

                await using SqliteDataReader reader = await effects.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    Totals total = sums[Guid.Parse(reader.GetString(0))];

                    decimal? boundUsd = reader.IsDBNull(1) ? null : ExactUsdText.Read(reader, 1);

                    long? boundTokens = reader.IsDBNull(2) ? null : reader.GetInt64(2);

                    decimal knownUsd = ExactUsdText.Read(reader, 3);

                    long knownTokens = reader.GetInt64(4);

                    bool unknownUsd = reader.GetBoolean(5), unknownTokens = reader.GetBoolean(6);

                    GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(7);

                    if (!Enum.IsDefined(state) || boundUsd < 0 || boundTokens < 0 || knownUsd < 0 || knownTokens < 0) return Invalid;

                    total.KnownUsd = ExactUsdText.CheckedAdd(total.KnownUsd, knownUsd);
                    total.KnownTokens = checked(total.KnownTokens + knownTokens);
                    total.UnknownUsd |= unknownUsd;
                    total.UnknownTokens |= unknownTokens;
                    total.CapabilityFault |= boundUsd is { } qualifiedUsd && knownUsd > qualifiedUsd
                        || boundTokens is { } qualifiedTokens && knownTokens > qualifiedTokens;

                    bool outstanding = state is GuildBudgetEffectState.Prepared or GuildBudgetEffectState.PossiblyDispatched;

                    decimal? holdUsd = state != GuildBudgetEffectState.ProvenNoDispatch && (outstanding || unknownUsd)
                        ? boundUsd is { } usd ? knownUsd >= usd ? 0m : ExactUsdText.CheckedAdd(usd, -knownUsd) : null : 0m;

                    long? holdTokens = state != GuildBudgetEffectState.ProvenNoDispatch && (outstanding || unknownTokens)
                        ? boundTokens is { } tokens ? Math.Max(0L, checked(tokens - knownTokens)) : null : 0L;

                    total.HoldUsd = Sum(total.HoldUsd, holdUsd);
                    total.HoldTokens = Sum(total.HoldTokens, holdTokens);

                    if (state == GuildBudgetEffectState.Complete)
                    {
                        if (unknownUsd) total.Unpriced = checked(total.Unpriced + 1);
                        else total.Priced = checked(total.Priced + 1);
                    }

                    if (state == GuildBudgetEffectState.PossiblyDispatched || unknownUsd || unknownTokens)
                        total.Uncertain = checked(total.Uncertain + 1);
                }
            }

            Stack<(Guid Id, bool Visit)> work = new();

            HashSet<Guid> visited = [];

            work.Push((accountId, false));

            while (work.TryPop(out var entry))
            {
                ct.ThrowIfCancellationRequested();

                if (!entry.Visit)
                {
                    if (!visited.Add(entry.Id)) return Invalid;

                    work.Push((entry.Id, true));

                    if (children.TryGetValue(entry.Id, out List<Guid>? descendants))
                        foreach (Guid child in descendants) work.Push((child, false));

                    continue;
                }

                Totals total = sums[entry.Id];

                total.LiabilityUsd = Sum(total.KnownUsd, total.HoldUsd);
                total.LiabilityTokens = Sum(total.KnownTokens, total.HoldTokens);

                if (children.TryGetValue(entry.Id, out List<Guid>? childIds))
                {
                    foreach (Guid childId in childIds)
                    {
                        Account child = accounts[childId];

                        Totals childTotal = sums[childId];

                        decimal? committedUsd = child.Closed ? childTotal.LiabilityUsd : Max(child.Usd, childTotal.LiabilityUsd);

                        long? committedTokens = child.Closed ? childTotal.LiabilityTokens : Max(child.Tokens, childTotal.LiabilityTokens);

                        // Closed envelopes retain physical liability, not an active grant. Any
                        // still-active descendants remain visible at the first active frontier.
                        total.ActiveUsd = Sum(total.ActiveUsd, child.Closed ? childTotal.ActiveUsd : child.Usd);
                        total.ActiveTokens = Sum(total.ActiveTokens, child.Closed ? childTotal.ActiveTokens : child.Tokens);
                        total.LiabilityUsd = Sum(total.LiabilityUsd, committedUsd);
                        total.LiabilityTokens = Sum(total.LiabilityTokens, committedTokens);
                        total.KnownUsd = ExactUsdText.CheckedAdd(total.KnownUsd, childTotal.KnownUsd);
                        total.KnownTokens = checked(total.KnownTokens + childTotal.KnownTokens);
                        total.HoldUsd = Sum(total.HoldUsd, childTotal.HoldUsd);
                        total.HoldTokens = Sum(total.HoldTokens, childTotal.HoldTokens);
                        total.UnknownUsd |= childTotal.UnknownUsd;
                        total.UnknownTokens |= childTotal.UnknownTokens;
                        total.CapabilityFault |= childTotal.CapabilityFault;
                        total.Priced = checked(total.Priced + childTotal.Priced);
                        total.Unpriced = checked(total.Unpriced + childTotal.Unpriced);
                        total.Uncertain = checked(total.Uncertain + childTotal.Uncertain);
                    }
                }
            }

            if (visited.Count != accounts.Count) return Invalid;

            Totals aggregate = sums[accountId];

            decimal? remainingUsd = root.Usd is { } usdLimit ? aggregate.LiabilityUsd is { } liability
                ? liability >= usdLimit ? 0m : ExactUsdText.CheckedAdd(usdLimit, -liability) : 0m : null;

            long? remainingTokens = root.Tokens is { } tokenLimit ? aggregate.LiabilityTokens is { } tokenLiability
                ? Math.Max(0L, checked(tokenLimit - tokenLiability)) : 0L : null;

            return new GuildBudgetSnapshot(accountId, Grant(root.Usd, root.Tokens), Grant(remainingUsd, remainingTokens),
                Grant(aggregate.ActiveUsd, aggregate.ActiveTokens), Grant(aggregate.HoldUsd, aggregate.HoldTokens),
                aggregate.KnownUsd, aggregate.KnownTokens, aggregate.UnknownUsd, aggregate.UnknownTokens,
                aggregate.Priced, aggregate.Unpriced, aggregate.Uncertain, root.Revision, root.Closed,
                Grant(aggregate.LiabilityUsd, aggregate.LiabilityTokens), aggregate.CapabilityFault);
        }
        catch (OverflowException)
        {
            return Exceeded;
        }
        catch (FormatException)
        {
            return Invalid;
        }
    }

    private const string Subtree = """
        WITH RECURSIVE subtree(Id) AS (
            SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$account
            UNION
            SELECT a.AccountId FROM guild_budget_accounts a JOIN subtree parent ON a.ParentAccountId=parent.Id
        )
        """;

    private static decimal? Sum(decimal? left, decimal? right) => left is { } a && right is { } b ? ExactUsdText.CheckedAdd(a, b) : null;

    private static long? Sum(long? left, long? right) => left is { } a && right is { } b ? checked(a + b) : null;

    private static decimal? Max(decimal? left, decimal? right) => left is { } a && right is { } b ? Math.Max(a, b) : null;

    private static long? Max(long? left, long? right) => left is { } a && right is { } b ? Math.Max(a, b) : null;

    private sealed class Totals
    {
        internal decimal KnownUsd;

        internal long KnownTokens;

        internal decimal? HoldUsd = 0m, ActiveUsd = 0m, LiabilityUsd = 0m;

        internal long? HoldTokens = 0L, ActiveTokens = 0L, LiabilityTokens = 0L;

        internal bool UnknownUsd, UnknownTokens, CapabilityFault;

        internal long Priced, Unpriced, Uncertain;
    }
}
