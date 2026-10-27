using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableFact]
    public async Task Reserve_refusal_names_exact_current_requested_projection_and_utc_period()
    {
        RequireSqlCipher();

        const string day = "2035-02-03";

        DateTimeOffset now = new(2035, 2, 3, 12, 0, 0, TimeSpan.Zero);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 4.9899m });

        service.UtcNowForTesting = () => now;

        Assert.True((await service.ReserveAsync(new(Guid.NewGuid(), 4.9899m, now.AddHours(1), day))).IsSuccess);

        Result<BudgetReservation> result = await service.ReserveAsync(new(Guid.NewGuid(), 0.0001m, now.AddHours(1), day));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        Assert.Contains("$4.9899", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("requested: $0.0001", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("projected: $4.99", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("UTC day 2035-02-03", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Arcanum:Cost:Budget:DailyLimitUsd", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(1L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task Adjust_refusal_distinguishes_the_requested_increase_from_the_existing_reservation()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 0.0050m });

        DateTimeOffset now = new(2035, 2, 3, 12, 0, 0, TimeSpan.Zero);

        service.UtcNowForTesting = () => now;

        string day = BudgetReservationService.UtcBudgetPeriod(now);

        Result<BudgetReservation> reservation = await service.ReserveAsync(new(Guid.NewGuid(), 0.0049m, now.AddHours(1), day));

        Assert.True(reservation.IsSuccess);

        Result result = await service.AdjustAsync(reservation.Value.Id, 0.0051m);

        Assert.True(result.IsFailure);

        Assert.Contains("committed+reserved: $0.0049", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("requested increase: $0.0002", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("projected: $0.0051", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("UTC day " + day, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0.0049m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task Recheck_refusal_separates_delegated_spend_and_keeps_subcent_precision()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 0.0050m });

        DateTimeOffset now = new(2035, 2, 3, 12, 0, 0, TimeSpan.Zero);

        service.UtcNowForTesting = () => now;

        string day = BudgetReservationService.UtcBudgetPeriod(now);

        Result<BudgetReservation> reservation = await service.ReserveAsync(new(Guid.NewGuid(), 0.0049m, now.AddHours(1), day));

        Assert.True(reservation.IsSuccess);

        Result result = await service.RecheckDailyLimitAsync(reservation.Value.Id, 0.0002m);

        Assert.True(result.IsFailure);

        Assert.Contains("committed+reserved: $0.0049", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("delegated: $0.0002", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("projected: $0.0051", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("UTC day " + day, result.Error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_prior_day_adjustment_reports_the_day_it_would_exceed_without_counting_its_old_reservation_twice()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 2, 3, 12, 0, 0, TimeSpan.Zero);

        string today = BudgetReservationService.UtcBudgetPeriod(now);

        string yesterday = BudgetReservationService.UtcBudgetPeriod(now.AddDays(-1));

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 0.01m });

        service.UtcNowForTesting = () => now;

        Assert.True((await service.ReserveAsync(new(Guid.NewGuid(), 0.0099m, now.AddHours(1), today))).IsSuccess);

        Result<BudgetReservation> reservation = await service.ReserveAsync(new(Guid.NewGuid(), 0.0001m, now.AddHours(1), yesterday));

        Assert.True(reservation.IsSuccess);

        Result result = await service.AdjustAsync(reservation.Value.Id, 0.0002m);

        Assert.True(result.IsFailure);

        Assert.Contains("committed+reserved: $0.0099", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("requested reservation: $0.0002", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("projected: $0.0101", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("UTC day " + today, result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("UTC day " + yesterday, result.Error.Message, StringComparison.Ordinal);
    }
}
