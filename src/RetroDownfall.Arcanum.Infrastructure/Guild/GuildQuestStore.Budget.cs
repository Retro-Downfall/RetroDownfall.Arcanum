using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore : IGuildTreeSpendLedger
{
    Task<Result<GuildSpendDto>> IGuildTreeSpendLedger.ReadAsync(GuildCallerContext caller, Guid questId, CancellationToken cancellationToken)
    {
        if (caller is null || questId == Guid.Empty || allocations is not GuildBudgetLedger ledger)
            return Task.FromResult(Result<GuildSpendDto>.Failure(OwnerUnavailable));

        return authority.InQuestTransactionAsync<GuildSpendDto>(caller.Execution?.RootQuestId ?? questId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                Result<GuildBudgetSnapshot> read = await ledger.ReadWithinAsync(connection, transaction,
                    found.Value.AllocationAccountId, token).ConfigureAwait(false);

                if (read.IsFailure) return read.Error;

                await using SqliteCommand watermark = Command(connection, transaction,
                    "SELECT COALESCE(MAX(EventSequence),0) FROM guild_events WHERE RootQuestId=$root");

                Add(watermark, "$root", Id(found.Value.Quest.RootQuestId));

                long sequence = (long)(await watermark.ExecuteScalarAsync(token).ConfigureAwait(false))!;

                GuildBudgetSnapshot spend = read.Value;

                return new GuildSpendDto(questId, spend.KnownUsd, spend.KnownTokens, spend.UnknownUsd, spend.UnknownTokens,
                    spend.PricedCount, spend.UnpricedCount, spend.UncertainCount, spend.OutstandingAllocation,
                    spend.UnresolvedHolds, spend.Remaining, sequence);
            }, cancellationToken);
    }
}
