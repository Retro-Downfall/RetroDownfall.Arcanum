using System.Globalization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(DataRetentionOperation.Prune, BudgetReservationStatus.UncertainPaid)]
    [InlineData(DataRetentionOperation.Prune, BudgetReservationStatus.Reserved)]
    [InlineData(DataRetentionOperation.FactoryReset, BudgetReservationStatus.UncertainPaid)]
    [InlineData(DataRetentionOperation.FactoryReset, BudgetReservationStatus.Reserved)]
    public async Task OutstandingReservation_ProtectsFailedRunFromAccountingPruneAndGlobalReset(DataRetentionOperation operation, BudgetReservationStatus status)
    {
        RequireSqlCipher();

        Guid run = Guid.NewGuid();

        Guid reservation = Guid.NewGuid();

        string old = DateTimeOffset.UtcNow.AddYears(-2).ToString("o", CultureInfo.InvariantCulture);

        await ExecuteAsync("""
            INSERT INTO InferenceRuns (Id, RequestId, Surface, Purpose, StartedAt, CompletedAt, Status)
            VALUES (@id, 'uncertain-paid', 'memory-evaluation', 'counterfactual-arm', @old, @old, @failed)
            """, ("@id", run.ToString("N")), ("@old", old), ("@failed", (int)InferenceRunStatus.Failed));

        await ExecuteAsync("""
            INSERT INTO BudgetReservations (Id, RunId, BudgetPeriod, ReservedUsd, ReconciledUsd, Status, ExpiresAt, CreatedAt, UpdatedAt)
            VALUES (@id, @run, '2020-01-01', '5', '0', @status, @old, @old, @old)
            """, ("@id", reservation.ToString("N")), ("@run", run.ToString("N")), ("@old", old), ("@status", (int)status));

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.Accounting = new() { Enabled = true, Days = 30 };

        var plan = await CreateService(settings).PlanAsync(new(operation), CancellationToken.None);

        Assert.Contains(plan.Conflicts, conflict => conflict.Code == "Data.BudgetReservationOutstanding");
    }
}
