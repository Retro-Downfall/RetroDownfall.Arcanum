using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class TurnAccountingHandleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TapestryUnknownEffect_FailsTheRunWithoutInventingUsageOrReservation(bool nested, bool earlierKnown)
    {
        using GrimoireFixture fixture = new();

        await using var db = fixture.CreateContext(fixture.CopyDatabase());

        var writer = new TurnRunWriter(db);

        PricingSettings pricing = new() { DefaultPricing = new() { InputPer1M = 1_000_000m } };

        TurnAccountingHandle root = await TurnAccountingHandle.BeginAsync(writer, null,
            nested ? "batch" : "hub", nested ? "batch" : "chat", "uncertain", default);

        if (earlierKnown) await root.RecordChatUsageAsync(writer, "provider", "model", 1, 0, 0, 0, pricing.DefaultPricing, default);

        TurnAccountingHandle reporting = nested ? root.CreateNestedOperationHandle() : root;

        reporting.RecordUncertainProviderEffect();

        await root.CompleteAsync(writer, InferenceRunStatus.Completed, default);

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT count(*) FROM BudgetReservations WHERE RunId=@run";

        var run = command.CreateParameter();

        run.ParameterName = "@run";

        run.Value = root.RunId!.Value.ToString("N");

        command.Parameters.Add(run);

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT Status FROM InferenceRuns WHERE Id=@run";

        Assert.Equal((long)InferenceRunStatus.Failed, Convert.ToInt64(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT count(*) FROM BillableOperations WHERE RunId=@run";

        Assert.Equal(earlierKnown ? 1L : 0L, Convert.ToInt64(await command.ExecuteScalarAsync()));

    }

    [Fact]
    public async Task TapestryUnknownEffect_CannotChangeAnAlreadyFrozenCompletion()
    {
        RecordingTurnRunWriter writer = new();

        TurnAccountingHandle root = await TurnAccountingHandle.BeginAsync(writer, null, "test", "chat", "frozen", default);

        await root.CompleteAsync(writer, InferenceRunStatus.Completed, default);

        Assert.Throws<InvalidOperationException>(root.RecordUncertainProviderEffect);

        Assert.Equal(InferenceRunStatus.Completed, writer.CompletedStatus);


    }
}
