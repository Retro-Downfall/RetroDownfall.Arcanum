using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class TurnAccountingHandleTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task TapestryUnknownEffect_RootUncertaintySurvivesKnownRowsAndPersistenceFault(bool nested, bool earlierKnown, bool persistenceFault)
    {
        RecordingTurnRunWriter writer = new();

        IOException fault = new("private-uncertainty-write-fault");

        RecordingBudgetReservationService reservations = new() { UncertainException = persistenceFault ? fault : null };

        PricingSettings pricing = new() { DefaultPricing = new() { InputPer1M = 1_000_000m } };

        TurnAccountingHandle root = (await TurnAccountingHandle.BeginAsync(writer, reservations, pricing,
            "model", null, "test", "chat", "uncertain", default, reservedUsdOverride: 3m)).Value;

        if (earlierKnown) await root.RecordChatUsageAsync(writer, "provider", "model", 1, 0, 0, 0, pricing.DefaultPricing, default);

        TurnAccountingHandle reporting = nested ? root.CreateNestedOperationHandle() : root;

        Exception? failure = await Record.ExceptionAsync(() => reporting.RecordUncertainProviderEffectAsync(reservations, default));

        await root.CompleteAsync(writer, reservations, InferenceRunStatus.Completed, default);

        Assert.Equal(persistenceFault, failure is not null);

        if (persistenceFault) Assert.Same(fault, failure);

        Assert.Equal(1, reservations.UncertainCalls);

        Assert.Equal(root.RunId, reservations.UncertainRun);

        Assert.Equal(InferenceRunStatus.Failed, writer.CompletedStatus);

        Assert.False(reservations.WasReleased);

        Assert.Equal(0, reservations.ReconcileCalls);

        Assert.Equal(earlierKnown ? 1m : 0m, writer.RecordedCostUsd);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TapestryUnknownEffect_DurableOrdinaryOrBatchReservationSurvivesSweepAndRecovery(bool nested, bool earlierKnown)
    {
        using GrimoireFixture fixture = new();

        await using var db = fixture.CreateContext(fixture.CopyDatabase());

        var settings = new ArcanumSettings { Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 100m } } };

        var reservations = new BudgetReservationService(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        var writer = new TurnRunWriter(db);

        PricingSettings pricing = new() { DefaultPricing = new() { InputPer1M = 1_000_000m } };

        TurnAccountingHandle root = (await TurnAccountingHandle.BeginAsync(writer, reservations, pricing,
            "model", null, nested ? "batch" : "hub", nested ? "batch" : "chat", "uncertain", default, reservedUsdOverride: 3m)).Value;

        if (earlierKnown) await root.RecordChatUsageAsync(writer, "provider", "model", 1, 0, 0, 0, pricing.DefaultPricing, default);

        TurnAccountingHandle reporting = nested ? root.CreateNestedOperationHandle() : root;

        await reporting.RecordUncertainProviderEffectAsync(reservations, default);

        await root.CompleteAsync(writer, reservations, InferenceRunStatus.Completed, default);

        await reservations.ReleaseAsync(root.ReservationId!.Value);

        _ = await reservations.SweepExpiredAsync(DateTimeOffset.UtcNow.AddYears(2));

        await reservations.ReconcileRecordedUsageAsync(root.RunId!.Value);

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT Status FROM BudgetReservations WHERE RunId=@run";

        var run = command.CreateParameter();

        run.ParameterName = "@run";

        run.Value = root.RunId.Value.ToString("N");

        command.Parameters.Add(run);

        Assert.Equal((long)BudgetReservationStatus.UncertainPaid, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT Status FROM InferenceRuns WHERE Id=@run";

        Assert.Equal((long)InferenceRunStatus.Failed, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT count(*) FROM BillableOperations WHERE RunId=@run";

        Assert.Equal(earlierKnown ? 1L : 0L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        Assert.Equal(3m, await reservations.GetTodayOutstandingReservationsAsync());
    }

    [Fact]
    public async Task TapestryUnknownEffect_CannotChangeAnAlreadyFrozenCompletion()
    {
        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new();

        TurnAccountingHandle root = (await TurnAccountingHandle.BeginAsync(writer, reservations, new(),
            "model", null, "test", "chat", "frozen", default)).Value;

        await root.CompleteAsync(writer, reservations, InferenceRunStatus.Completed, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => root.RecordUncertainProviderEffectAsync(reservations, default));

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);

        Assert.Equal(1, reservations.ReconcileCalls);

        Assert.Equal(0, reservations.UncertainCalls);
    }
}
