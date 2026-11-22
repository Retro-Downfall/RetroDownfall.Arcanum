using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Copies individual durable bills into Guild evidence; it never creates aggregate bills.</summary>
internal static class GuildBudgetPhysicalAccounting
{
    internal static async Task<Result<GuildBudgetEffect>> ApplyLocalBillWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildBudgetLedger ledger, Guid effectId, Guid receiptId, CancellationToken cancellationToken,
        BudgetReservationService? daily = null)
    {
        if (receiptId == Guid.Empty) return Invalid;

        Result<GuildBudgetEffect> found = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildBudgetEffectRequest request = found.Value.Request;

        if (request.Kind != GuildBudgetEffectKind.LocalProvider) return Invalid;

        await using (SqliteCommand replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;

            replay.CommandText = "SELECT EffectId FROM guild_budget_evidence WHERE ReceiptId=$receipt";

            replay.Parameters.AddWithValue("$receipt", receiptId.ToString("D").ToUpperInvariant());

            if (await replay.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string applied)
                return Guid.Parse(applied) == effectId ? found.Value
                    : new Error(ErrorCodes.Guild.IdempotencyConflict, "The physical receipt belongs to another effect.");
        }

        if (request.ReservationId is not null && daily is null)
            return new Error(ErrorCodes.Guild.AuthorityUnavailable, "The real daily reservation owner is unavailable.");

        try
        {
            decimal usd;

            long tokens;

            bool complete;

            await using (SqliteCommand bill = connection.CreateCommand())
            {
                bill.Transaction = transaction;

                bill.CommandText = """
                    SELECT RunId,ActualCostUsd,InputTokens,OutputTokens,Status FROM BillableOperations
                    WHERE Id=$compact COLLATE NOCASE OR Id=$dashed COLLATE NOCASE
                    """;

                bill.Parameters.AddWithValue("$compact", request.SourceId.ToString("N"));

                bill.Parameters.AddWithValue("$dashed", request.SourceId.ToString("D"));

                await using SqliteDataReader reader = await bill.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Missing;

                if (Guid.Parse(reader.GetString(0)) != request.AccountingAssociationId)
                    return new Error(ErrorCodes.Guild.Forbidden, "The physical bill belongs to another accounting run.");

                usd = ExactUsdText.Read(reader, 1);

                // SQLite affinity permits REAL/TEXT values; typed getters coerce them.
                // Physical evidence must retain its stored integer identity exactly.
                if (reader.GetValue(2) is not long input || reader.GetValue(3) is not long output
                    || reader.GetValue(4) is not long status) return Invalid;

                if (usd < 0 || input < 0 || output < 0 || status is not (0 or 1)) return Invalid;

                tokens = checked(input + output);

                complete = status == (int)BillableOperationStatus.Completed;

                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return Invalid;
            }

            // Cached input and reasoning are subsets already present in these two token columns.
            // An absent qualified price remains unknown even if a fallback bill says zero.
            bool unknownUsd = !complete || request.UsdBound is null;

            bool unknownTokens = !complete;

            string identity = string.Join("|", request.SourceId.ToString("N"), request.AccountingAssociationId.ToString("N"),
                ExactUsdText.Format(usd), tokens.ToString(CultureInfo.InvariantCulture), complete, unknownUsd, unknownTokens);

            GuildBudgetEvidence evidence = new(receiptId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
                usd, tokens, unknownUsd, unknownTokens, complete);

            Result<GuildBudgetEffect> applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction,
                effectId, evidence, cancellationToken).ConfigureAwait(false);

            if (applied.IsFailure) return applied.Error;

            if (request.ReservationId is { } reservation)
            {
                Result disposition = complete && !unknownUsd
                    ? await daily!.ReconcileWithinAsync(connection, transaction, reservation,
                        request.AccountingAssociationId, usd, true, cancellationToken).ConfigureAwait(false)
                    : await daily!.RecordPartialUsageWithinAsync(connection, transaction, reservation,
                        request.AccountingAssociationId, usd, cancellationToken).ConfigureAwait(false);

                if (disposition.IsFailure) return disposition.Error;
            }

            return applied;
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException)
        {
            return Invalid;
        }
    }

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The physical billing identity or exact usage is invalid.");

    private static Error Missing => new(ErrorCodes.Guild.ReconciliationRequired, "The physical billing source is not available; its exposure remains held.");
}
