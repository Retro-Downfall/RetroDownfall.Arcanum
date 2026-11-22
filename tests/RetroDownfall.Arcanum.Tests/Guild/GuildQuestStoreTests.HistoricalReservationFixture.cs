using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private static async Task<Result<GuildBudgetEffect>> SeedHistoricalProviderEffectAsync(
        SqliteConnection connection, SqliteTransaction transaction, GuildBudgetLedger ledger,
        GuildBudgetEffectRequest request, bool linkedReservation)
    {
        if (!linkedReservation)
            return await ledger.PrepareEffectWithinAsync(connection, transaction, request, default);

        var prior = await ledger.ReadEffectWithinAsync(connection, transaction, request.EffectId, default);

        if (prior.IsSuccess)
            return await ledger.PrepareEffectWithinAsync(connection, transaction,
                request with { ReservationId = prior.Value.Request.ReservationId }, default);

        Guid reservation = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using SqliteCommand seed = connection.CreateCommand();

        seed.Transaction = transaction;

        seed.CommandText = """
            INSERT INTO BudgetReservations
                (Id, RunId, BudgetPeriod, Status, ExpiresAt, CreatedAt, UpdatedAt, ReservedUsd, ReconciledUsd)
            VALUES($id,$run,$period,$status,$expires,$now,$now,$usd,'0')
            """;

        seed.Parameters.AddWithValue("$id", reservation.ToString("N"));
        seed.Parameters.AddWithValue("$run", request.AccountingAssociationId.ToString("N"));
        seed.Parameters.AddWithValue("$period", BudgetReservationService.UtcBudgetPeriod(now));
        seed.Parameters.AddWithValue("$status", (int)BudgetReservationStatus.Reserved);
        seed.Parameters.AddWithValue("$expires", UtcInstantText.Format(now.AddHours(1)));
        seed.Parameters.AddWithValue("$now", UtcInstantText.Format(now));
        seed.Parameters.AddWithValue("$usd", ExactUsdText.Format(request.UsdBound!.Value));

        Assert.Equal(1, await seed.ExecuteNonQueryAsync());

        return await ledger.PrepareEffectWithinAsync(connection, transaction,
            request with { ReservationId = reservation }, default);
    }
}
