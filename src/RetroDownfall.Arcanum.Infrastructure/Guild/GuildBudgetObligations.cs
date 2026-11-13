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
          FROM guild_budget_effects WHERE AccountId IN(SELECT Id FROM subtree) AND ReservationId IS NOT NULL)
        SELECT l.EffectId,l.ReservationId,l.AccountingAssociationId,l.StateCode,l.UsdBound,l.KnownUsd,l.UnknownUsd,
          r.Id,r.RunId,r.Status,r.ReservedUsd,r.ReconciledUsd,
          typeof(r.Status)='integer' AND typeof(r.ReservedUsd)='text' AND typeof(r.ReconciledUsd)='text'
        FROM linked l LEFT JOIN BudgetReservations r INDEXED BY IX_BudgetReservations_Id_Norm
          ON lower(replace(r.Id,'-',''))=lower(replace(l.ReservationId,'-',''))
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

        // The normalized identity index detects aliases across all runs without a global census.
        command.CommandText = ReservationValidationSql;

        try
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            Guid? previous = null;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid effect) || effect == Guid.Empty || effect == previous) return false;

                previous = effect;

                if (!ValidReservationRow(reader, 0)) return false;
            }

            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    internal static bool ValidReservationRow(SqliteDataReader reader, int offset)
    {
        if (reader.IsDBNull(offset + 7) || reader.IsDBNull(offset + 8) || reader.IsDBNull(offset + 9)
            || reader.IsDBNull(offset + 10) || reader.IsDBNull(offset + 11) || reader.GetInt64(offset + 12) != 1
            || !Guid.TryParse(reader.GetString(offset + 1), out Guid reservation) || reservation == Guid.Empty
            || !Guid.TryParse(reader.GetString(offset + 2), out Guid run) || run == Guid.Empty
            || !Guid.TryParse(reader.GetString(offset + 7), out Guid actualReservation) || actualReservation != reservation
            || !Guid.TryParse(reader.GetString(offset + 8), out Guid actualRun) || actualRun != run || reader.IsDBNull(offset + 4)) return false;

        decimal bound = ExactUsdText.Read(reader, offset + 4), known = ExactUsdText.Read(reader, offset + 5);

        decimal reserved = ExactUsdText.Read(reader, offset + 10), reconciled = ExactUsdText.Read(reader, offset + 11);

        if (bound < 0 || known < 0 || reserved < bound || reconciled < 0) return false;

        GuildBudgetEffectState state = (GuildBudgetEffectState)reader.GetInt32(offset + 3);

        BudgetReservationStatus status = (BudgetReservationStatus)reader.GetInt32(offset + 9);

        return state switch
        {
            GuildBudgetEffectState.Complete => !reader.GetBoolean(offset + 6)
                && status == BudgetReservationStatus.Reconciled && reconciled == known,
            GuildBudgetEffectState.ProvenNoDispatch => known == 0m && reconciled == 0m
                && status is BudgetReservationStatus.Released or BudgetReservationStatus.Reconciled,
            _ => false,
        };
    }

}
