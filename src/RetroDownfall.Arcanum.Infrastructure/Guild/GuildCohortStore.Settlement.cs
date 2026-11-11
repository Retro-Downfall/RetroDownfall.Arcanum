using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCohortSettlementStep(GuildBudgetSnapshot? Snapshot, long PendingMembers,
    long PendingDescendants, long CancelRequested, bool Complete, long PendingAccounts = 0, long PendingEffects = 0);

internal sealed partial class GuildCohortStore
{
    internal static async Task<Result<GuildCohortSettlementStep>> AdvanceSettlementWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, CohortDto cohort, CancellationToken ct)
    {
        try
        {
            var rootRead = await NewSettlementNodeAsync(connection, transaction, cohort, cohort.AllocationAccountId, null, ct).ConfigureAwait(false);

            if (rootRead.IsFailure) return rootRead.Error;

            GuildCohortSettlementNode root = rootRead.Value;

            if (root.Kind != GuildBudgetOwnerKind.Cohort || root.OwnerId != cohort.Id) return Unavailable;

            long epoch;

            await using (SqliteCommand clock = Command(connection, transaction,
                "SELECT COALESCE((SELECT Revision FROM guild_cohort_settlement_clock WHERE RootAccountId=$root),0)"))
            {
                Add(clock, "$root", Id(root.RootAccountId));

                epoch = (long)(await clock.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? -1L);
            }

            if (epoch < 0) return Unavailable;

            long generation = 1;

            Guid? current = root.AccountId;

            bool initialized = false;

            bool complete = false;

            await using (SqliteCommand state = Command(connection, transaction,
                "SELECT Generation,Epoch,CurrentAccountId,Complete,typeof(Generation)='integer' AND typeof(Epoch)='integer' AND typeof(Complete)='integer' FROM guild_cohort_settlement_state WHERE CohortId=$cohort"))
            {
                Add(state, "$cohort", Id(cohort.Id));

                await using SqliteDataReader reader = await state.ExecuteReaderAsync(ct).ConfigureAwait(false);

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (reader.GetInt64(4) != 1) return Unavailable;

                    generation = reader.GetInt64(0);

                    if (reader.GetInt64(1) == epoch)
                    {
                        initialized = true;
                        current = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
                        complete = reader.GetBoolean(3);
                    }
                    else generation = checked(generation + 1);
                }
            }

            if (generation < 1 || complete != (current is null)) return Unavailable;

            if (!initialized)
            {
                await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, root, ct).ConfigureAwait(false);

                await SaveSettlementStateAsync(connection, transaction, cohort.Id, generation, epoch, current, ct).ConfigureAwait(false);
            }

            int work = 4;

            // Retire obsolete checkpoints within this same work budget. A changing ledger
            // never accumulates a complete retained traversal for each historical epoch.
            await using (SqliteCommand retire = Command(connection, transaction, """
                DELETE FROM guild_cohort_settlement_nodes
                WHERE (CohortId,Generation,AccountId) IN (
                    SELECT CohortId,Generation,AccountId FROM guild_cohort_settlement_nodes
                    WHERE CohortId=$cohort AND Generation<$generation ORDER BY Generation,AccountId LIMIT 90)
                """))
            {
                Add(retire, "$cohort", Id(cohort.Id));
                Add(retire, "$generation", generation);

                int retired = await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                work += retired;

                if (retired == 90) return new GuildCohortSettlementStep(null, 0, 0, 0, false);
            }

            // Every iteration admits one source row or advances one bounded traversal edge.
            // Parent nodes are persisted before descending, so restart never repeats a fold.
            for (; work < 96 && current is { } account; work += 2)
            {
                var read = await LoadSettlementNodeAsync(connection, transaction, cohort.Id, generation, account, ct).ConfigureAwait(false);

                if (read.IsFailure) return read.Error;

                GuildCohortSettlementNode node = read.Value;

                if (node.RootAccountId != root.RootAccountId) return Unavailable;

                if (node.Phase == 0)
                {
                    Result<bool> effect = await AccumulateSettlementEffectAsync(connection, transaction, node, ct).ConfigureAwait(false);

                    if (effect.IsFailure) return effect.Error;

                    if (!effect.Value)
                    {
                        if (node.PendingEffects > 0 && node.PendingMembers == 0 && node.PendingDescendants == 0)
                        {
                            if (node.IsPrimaryMember) node.PendingMembers = 1;
                            else node.PendingDescendants = 1;
                        }

                        node.Phase = 1;
                        node.LiabilityUsd = AddUsd(node.KnownUsd, node.HoldUsd);
                        node.LiabilityTokens = AddTokens(node.KnownTokens, node.HoldTokens);
                    }

                    await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, node, ct).ConfigureAwait(false);

                    continue;
                }

                if (node.Phase != 1) return Unavailable;

                Guid? child;

                await using (SqliteCommand next = Command(connection, transaction, """
                    SELECT AccountId FROM guild_budget_accounts
                    WHERE ParentAccountId=$parent AND AccountId>$after ORDER BY AccountId LIMIT 1
                    """))
                {
                    Add(next, "$parent", Id(node.AccountId));
                    Add(next, "$after", node.LastChild is { } after ? Id(after) : "");

                    child = await next.ExecuteScalarAsync(ct).ConfigureAwait(false) is string text ? Guid.Parse(text) : null;
                }

                if (child is { } childId)
                {
                    await using (SqliteCommand seen = Command(connection, transaction,
                        "SELECT EXISTS(SELECT 1 FROM guild_cohort_settlement_nodes WHERE CohortId=$cohort AND Generation=$generation AND AccountId=$account)"))
                    {
                        Add(seen, "$cohort", Id(cohort.Id));
                        Add(seen, "$generation", generation);
                        Add(seen, "$account", Id(childId));

                        if (Equals(1L, await seen.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unavailable;
                    }

                    work += 2;

                    var admitted = await NewSettlementNodeAsync(connection, transaction, cohort, childId, node.AccountId, ct).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    if (admitted.Value.RootAccountId != root.RootAccountId
                        || admitted.Value.Kind == GuildBudgetOwnerKind.Cohort && node.Kind != GuildBudgetOwnerKind.Quest) return Unavailable;

                    node.LastChild = childId;

                    await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, node, ct).ConfigureAwait(false);
                    await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, admitted.Value, ct).ConfigureAwait(false);

                    current = childId;
                }
                else
                {
                    node.Phase = 2;

                    await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, node, ct).ConfigureAwait(false);

                    if (node.ParentAccountId is { } parent)
                    {
                        var parentRead = await LoadSettlementNodeAsync(connection, transaction, cohort.Id, generation, parent, ct).ConfigureAwait(false);

                        if (parentRead.IsFailure) return parentRead.Error;

                        GuildCohortSettlementNode target = parentRead.Value;

                        if (target.Phase != 1 || target.LastChild != node.AccountId) return Unavailable;

                        FoldSettlementNode(target, node);

                        await SaveSettlementNodeAsync(connection, transaction, cohort.Id, generation, target, ct).ConfigureAwait(false);

                        current = parent;
                    }
                    else current = null;
                }

                await SaveSettlementStateAsync(connection, transaction, cohort.Id, generation, epoch, current, ct).ConfigureAwait(false);
            }

            if (current is not null) return new GuildCohortSettlementStep(null, 0, 0, 0, false);

            var finished = await LoadSettlementNodeAsync(connection, transaction, cohort.Id, generation, root.AccountId, ct).ConfigureAwait(false);

            if (finished.IsFailure) return finished.Error;

            GuildCohortSettlementNode total = finished.Value;

            if (total.Phase != 2) return Unavailable;

            decimal? remainingUsd = total.UsdLimit is null ? null : total.LiabilityUsd is null ? 0m
                : Math.Max(0m, ExactUsdText.CheckedAdd(Usd(total.UsdLimit), -Usd(total.LiabilityUsd)));

            long? remainingTokens = total.TokenLimit is null ? null : total.LiabilityTokens is null ? 0
                : Math.Max(0L, checked(total.TokenLimit.Value - total.LiabilityTokens.Value));

            GuildBudgetSnapshot snapshot = new(total.AccountId, SettlementGrant(total.UsdLimit, total.TokenLimit),
                SettlementGrant(remainingUsd is { } value ? ExactUsdText.Format(value) : null, remainingTokens),
                SettlementGrant(total.ActiveUsd, total.ActiveTokens), SettlementGrant(total.HoldUsd, total.HoldTokens),
                Usd(total.KnownUsd), total.KnownTokens, total.UnknownUsd, total.UnknownTokens, total.Priced, total.Unpriced,
                total.Uncertain, total.Revision, total.Closed, SettlementGrant(total.LiabilityUsd, total.LiabilityTokens), total.CapabilityFault);

            return new GuildCohortSettlementStep(snapshot, total.PendingMembers, total.PendingDescendants, total.CancelRequested, true, total.PendingAccounts, total.PendingEffects);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or JsonException or ArgumentException)
        {
            return Unavailable;
        }
    }

    private static decimal Usd(string value) => ExactUsdText.Parse(value);

    private static string? AddUsd(string? left, string? right) => left is null || right is null ? null
        : ExactUsdText.Format(ExactUsdText.CheckedAdd(Usd(left), Usd(right)));

    private static long? AddTokens(long? left, long? right) => left is null || right is null ? null : checked(left.Value + right.Value);

    private static string? MaxUsd(string? left, string? right) => left is null || right is null ? null
        : ExactUsdText.Format(Math.Max(Usd(left), Usd(right)));

    private static long? MaxTokens(long? left, long? right) => left is null || right is null ? null : Math.Max(left.Value, right.Value);

    private static GuildBudgetGrant SettlementGrant(string? usd, long? tokens) => new(
        new(usd is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, usd is null ? null : Usd(usd)),
        new(tokens is null ? GuildBudgetMode.Unlimited : GuildBudgetMode.Finite, tokens));

    private static void FoldSettlementNode(GuildCohortSettlementNode target, GuildCohortSettlementNode child)
    {
        target.ActiveUsd = AddUsd(target.ActiveUsd, child.Closed ? child.ActiveUsd : child.UsdLimit);
        target.ActiveTokens = AddTokens(target.ActiveTokens, child.Closed ? child.ActiveTokens : child.TokenLimit);
        target.LiabilityUsd = AddUsd(target.LiabilityUsd, child.Closed ? child.LiabilityUsd : MaxUsd(child.UsdLimit, child.LiabilityUsd));
        target.LiabilityTokens = AddTokens(target.LiabilityTokens, child.Closed ? child.LiabilityTokens : MaxTokens(child.TokenLimit, child.LiabilityTokens));
        target.KnownUsd = AddUsd(target.KnownUsd, child.KnownUsd)!;
        target.KnownTokens = checked(target.KnownTokens + child.KnownTokens);
        target.HoldUsd = AddUsd(target.HoldUsd, child.HoldUsd);
        target.HoldTokens = AddTokens(target.HoldTokens, child.HoldTokens);
        target.UnknownUsd |= child.UnknownUsd;
        target.UnknownTokens |= child.UnknownTokens;
        target.CapabilityFault |= child.CapabilityFault;
        target.Priced = checked(target.Priced + child.Priced);
        target.Unpriced = checked(target.Unpriced + child.Unpriced);
        target.Uncertain = checked(target.Uncertain + child.Uncertain);
        target.PendingMembers = checked(target.PendingMembers + child.PendingMembers);
        target.PendingDescendants = checked(target.PendingDescendants + child.PendingDescendants);
        target.CancelRequested = checked(target.CancelRequested + child.CancelRequested);
        target.PendingAccounts = checked(target.PendingAccounts + child.PendingAccounts);
        target.PendingEffects = checked(target.PendingEffects + child.PendingEffects);
    }
}
