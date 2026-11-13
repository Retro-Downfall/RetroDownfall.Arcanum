using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Separates physical and finite financial obligations from unknown reporting.</summary>
internal static class GuildBudgetObligations
{
    internal readonly record struct Classification(bool PhysicalPending, bool UsdBlocked, bool TokensBlocked,
        decimal? HoldUsd, long? HoldTokens);

    internal static Classification Classify(GuildBudgetEffectState state, bool finiteUsd, bool finiteTokens,
        bool hasReservation, bool unknownUsd, bool unknownTokens, decimal? usdBound, long? tokenBound,
        decimal knownUsd, long knownTokens)
    {
        bool physical = state is GuildBudgetEffectState.Prepared or GuildBudgetEffectState.PossiblyDispatched;

        bool usd = physical || state == GuildBudgetEffectState.Complete && unknownUsd && (finiteUsd || hasReservation);

        bool tokens = physical || state == GuildBudgetEffectState.Complete && unknownTokens && finiteTokens;

        decimal? holdUsd = usd ? usdBound is { } amount
            ? knownUsd >= amount ? 0m : ExactUsdText.CheckedAdd(amount, -knownUsd) : null : 0m;

        long? holdTokens = tokens ? tokenBound is { } bound
            ? knownTokens >= bound ? 0L : checked(bound - knownTokens) : null : 0L;

        return new(physical, usd, tokens, holdUsd, holdTokens);
    }
    internal const string ReservationLinksSql = """
        WITH RECURSIVE subtree(Id) AS (
          SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$account
          UNION SELECT child.AccountId FROM guild_budget_accounts child JOIN subtree p ON child.ParentAccountId=p.Id)
        SELECT EXISTS(SELECT 1 FROM guild_budget_effects WHERE AccountId IN(SELECT Id FROM subtree) AND ReservationId IS NOT NULL)
        """;

    internal const string ReservationValidationSql = """
        WITH RECURSIVE subtree(Id) AS (
          SELECT AccountId FROM guild_budget_accounts WHERE AccountId=$account
          UNION SELECT child.AccountId FROM guild_budget_accounts child JOIN subtree p ON child.ParentAccountId=p.Id),
        linked AS MATERIALIZED (
          SELECT EffectId,ReservationId,AccountingAssociationId,StateCode,UsdBound,KnownUsd,UnknownUsd
          FROM guild_budget_effects WHERE AccountId IN(SELECT Id FROM subtree) AND ReservationId IS NOT NULL),
        reservations AS MATERIALIZED (
          SELECT lower(replace(Id,'-','')) AS NormalId,Id,RunId,Status,ReservedUsd,ReconciledUsd FROM BudgetReservations)
        SELECT l.EffectId,l.ReservationId,l.AccountingAssociationId,l.StateCode,l.UsdBound,l.KnownUsd,l.UnknownUsd,
          r.Id,r.RunId,r.Status,r.ReservedUsd,r.ReconciledUsd,
          typeof(r.Status)='integer' AND typeof(r.ReservedUsd)='text' AND typeof(r.ReconciledUsd)='text'
        FROM linked l LEFT JOIN reservations r ON r.NormalId=lower(replace(l.ReservationId,'-',''))
        ORDER BY l.EffectId
        """;

    internal static async Task<bool> ReservationsClearWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = ReservationLinksSql;

        command.Parameters.AddWithValue("$account", accountId.ToString("D").ToUpperInvariant());

        // The effective all-off path never reads or materializes reservations.
        if (Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) return true;

        // One database-owned census detects normalized aliases across all runs.
        command.CommandText = ReservationValidationSql;

        try
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            Guid? previous = null;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid effect) || effect == Guid.Empty || effect == previous) return false;

                previous = effect;

                if (reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9) || reader.IsDBNull(10) || reader.IsDBNull(11)
                    || reader.GetInt64(12) != 1 || !Guid.TryParse(reader.GetString(1), out Guid reservation) || reservation == Guid.Empty
                    || !Guid.TryParse(reader.GetString(2), out Guid run) || run == Guid.Empty
                    || !Guid.TryParse(reader.GetString(7), out Guid actualReservation) || actualReservation != reservation
                    || !Guid.TryParse(reader.GetString(8), out Guid actualRun) || actualRun != run || reader.IsDBNull(4)) return false;

                decimal bound = ExactUsdText.Read(reader, 4), known = ExactUsdText.Read(reader, 5);

                decimal reserved = ExactUsdText.Read(reader, 10), reconciled = ExactUsdText.Read(reader, 11);

                if (bound < 0 || known < 0 || reserved < bound || reconciled < 0) return false;

                GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(3);

                BudgetReservationStatus status = (BudgetReservationStatus)reader.GetInt32(9);

                if (state == GuildBudgetEffectState.Complete)
                {
                    if (reader.GetBoolean(6) || status != BudgetReservationStatus.Reconciled || reconciled != known) return false;
                }
                else if (state == GuildBudgetEffectState.ProvenNoDispatch)
                {
                    if (known != 0m || reconciled != 0m || status is not (BudgetReservationStatus.Released or BudgetReservationStatus.Reconciled)) return false;
                }
                else return false;
            }

            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

}
