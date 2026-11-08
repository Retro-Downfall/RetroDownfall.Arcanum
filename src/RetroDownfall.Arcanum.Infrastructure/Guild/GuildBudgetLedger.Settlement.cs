using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildBudgetLedger
{
    public async Task<Result<GuildAccountingState>> SettleAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, Guid accountId, GuildResultReceipt receipt, CancellationToken cancellationToken)
    {
        bool noDispatch, stopped;

        await using (SqliteCommand proof = Command(connection, transaction, """
            SELECT r.NoDispatchProven,r.WorkStoppedProven FROM guild_result_receipts r
            JOIN guild_quest_budget b ON b.QuestId=r.QuestId
            WHERE r.QuestId=$quest AND r.ResultId=$receipt AND b.AccountId=$account
            """))
        {
            Add(proof, "$quest", Id(questId));
            Add(proof, "$receipt", Id(receipt.ResultId));
            Add(proof, "$account", Id(accountId));

            await using SqliteDataReader reader = await proof.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Missing;

            noDispatch = reader.GetBoolean(0);
            stopped = reader.GetBoolean(1);
        }

        if (!noDispatch && !stopped) return GuildAccountingState.Uncertain;

        if (noDispatch)
        {
            List<GuildBudgetEffect> effects = [];

            await using (SqliteCommand query = Command(connection, transaction, EffectColumns + " WHERE QuestId=$quest"))
            {
                Add(query, "$quest", Id(questId));

                await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) effects.Add(ParseEffect(reader));
            }

            foreach (GuildBudgetEffect effect in effects)
            {
                byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"Guild.NoDispatch:{receipt.ResultId:D}:{effect.Request.EffectId:D}"));

                GuildBudgetEvidence evidence = new(new Guid(digest.AsSpan(0, 16)), Convert.ToHexString(digest), 0m, 0, false, false, true, true);

                Result<GuildBudgetEffect> applied = await ApplyEvidenceWithinAsync(connection, transaction,
                    effect.Request.EffectId, evidence, cancellationToken).ConfigureAwait(false);

                if (applied.IsFailure) return applied.Error;

                if (effect.Request.ReservationId is { } reservation)
                {
                    if (daily is null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "The real daily reservation owner is unavailable.");

                    Result released = await daily.ReleaseProvenNoDispatchWithinAsync(connection, transaction,
                        reservation, effect.Request.AccountingAssociationId, cancellationToken).ConfigureAwait(false);

                    if (released.IsFailure) return released.Error;
                }
            }
        }

        Result closed = await CloseAccountWithinAsync(connection, transaction, accountId, receipt.ResultId, cancellationToken).ConfigureAwait(false);

        if (closed.IsFailure) return closed.Error;

        Result<GuildBudgetSnapshot> read = await ReadWithinAsync(connection, transaction, accountId, cancellationToken).ConfigureAwait(false);

        if (read.IsFailure) return read.Error;

        if (read.Value.UnknownUsd || read.Value.UnknownTokens) return GuildAccountingState.Uncertain;

        await using SqliteCommand pending = Command(connection, transaction, Subtree + """
            SELECT EXISTS(SELECT 1 FROM guild_budget_accounts a WHERE a.AccountId IN(SELECT Id FROM subtree) AND a.Closed=0)
                OR EXISTS(SELECT 1 FROM guild_budget_effects e WHERE e.AccountId IN(SELECT Id FROM subtree) AND e.StateCode IN(0,1))
            """);

        Add(pending, "$account", Id(accountId));

        return Equals(0L, await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            ? GuildAccountingState.Settled : GuildAccountingState.Pending;
    }
}
