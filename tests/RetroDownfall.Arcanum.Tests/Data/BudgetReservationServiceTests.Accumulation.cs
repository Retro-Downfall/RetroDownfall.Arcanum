using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableTheory]
    [InlineData(10000)]
    [InlineData(30000)]
    public async Task Large_day_preserves_exact_committed_reservation_and_adjustment_boundaries(int count)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 4, 7, 12, 0, 0, TimeSpan.Zero);

        Guid runId = await StartRunAsync("exact-budget-fold", now);

        decimal committed = count / 4 * (0.00000001m + 0.00003597m + 0.03125000m + 1.75000000m);

        await using (DbCommand command = _db!.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE bills(n) AS (
                    SELECT 1
                    UNION ALL SELECT n + 1 FROM bills WHERE n < @count
                )
                INSERT INTO "BillableOperations"
                    ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose",
                     "StartedAt", "CompletedAt", "InputTokens", "OutputTokens", "ReasoningTokens",
                     "CachedTokens", "PricingSnapshotJson", "ActualCostUsd", "Status", "ProviderRequestId")
                SELECT printf('%032x', n), @runId, @operationType, 'test', 'model', 'exact-fold',
                    @now, @now, 1, 1, 0, 0, '{}',
                    CASE n % 4 WHEN 0 THEN '0.00000001' WHEN 1 THEN '0.00003597'
                        WHEN 2 THEN '0.03125000' ELSE '1.75000000' END,
                    @status, NULL FROM bills;
                """;

            AddParameter(command, "@count", count);

            AddParameter(command, "@runId", runId.ToString("N"));

            AddParameter(command, "@operationType", (int)BillableOperationType.Chat);

            AddParameter(command, "@now", UtcInstantText.Format(now));

            AddParameter(command, "@status", (int)BillableOperationStatus.Completed);

            Assert.Equal(count, await command.ExecuteNonQueryAsync());
        }

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = committed + 1m,
        });

        service.UtcNowForTesting = () => now;

        Assert.Equal(committed, await service.GetTodayCommittedSpendAsync());

        BudgetReservation reservation = await ReserveAsync(
            service, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        Result raised = await service.AdjustAsync(reservation.Id, 1m);

        Assert.True(raised.IsSuccess, raised.IsFailure ? raised.Error.Message : null);

        Result refused = await service.AdjustAsync(reservation.Id, 1.00000001m);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, refused.Error.Code);

        Result<BudgetReservation> second = await service.ReserveAsync(new(
            Guid.NewGuid(), 0.00000001m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, second.Error.Code);

        Assert.Equal(1m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(committed, await service.GetTodayCommittedSpendAsync());

        Assert.Equal(1L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }
}
