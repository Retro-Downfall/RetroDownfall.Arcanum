using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class LegacyBudgetFixture
{
    public static ArcanumSettings ReadWithOldDailyLimit(ArcanumSettings original, decimal limit)
    {
        JsonObject document = JsonSerializer.SerializeToNode(original, ConfigurationJsonContext.Default.ArcanumSettings)!.AsObject();

        JsonObject cost = document["cost"]?.AsObject() ?? new JsonObject();

        if (document["cost"] is null) document["cost"] = cost;

        cost["budget"] = new JsonObject { ["enabled"] = true, ["dailyLimitUsd"] = limit };

        return document.Deserialize(ConfigurationJsonContext.Default.ArcanumSettings)!;
    }

    public static async Task<BudgetReservation> SeedAsync(
        ArcanumDbContext db,
        BudgetReservationRequest request,
        BudgetReservationStatus status = BudgetReservationStatus.Reserved)
    {
        await db.Database.OpenConnectionAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservation row = new(Guid.NewGuid(), request.RunId, request.BudgetPeriod,
            request.ReservedUsd, 0m, status, request.ExpiresAt, now);

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO BudgetReservations
                (Id, RunId, BudgetPeriod, Status, ExpiresAt, CreatedAt, UpdatedAt, ReservedUsd, ReconciledUsd)
            VALUES (@id, @run, @period, @status, @expires, @now, @now, @amount, '0');
            """;

        AddParameter(command, "@id", row.Id.ToString("N"));

        AddParameter(command, "@run", row.RunId.ToString("N"));

        AddParameter(command, "@period", row.BudgetPeriod);

        AddParameter(command, "@status", (int)row.Status);

        AddParameter(command, "@expires", UtcInstantText.Format(row.ExpiresAt));

        AddParameter(command, "@now", UtcInstantText.Format(now));

        ExactUsdText.AddParameter(command, "@amount", row.ReservedUsd);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        return row;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}
