using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.LongRest;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestProviderRequiredBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_reservation_port_refuses_before_run_disclosure_or_send(bool enforceBudget)
    {
        await using LongRestProviderFixture f = await LongRestProviderFixture.CreateAsync();

        f.Options.CurrentValue.Cost.Budget = new() { Enabled = enforceBudget, DailyLimitUsd = 100m };

        LongRestProvider provider = new(f.ClientFactory, new ModelCallExecutor(f.Estimator), f.Epoch,
            new TurnRunWriter(f.H.Saga.Context), reservations: null!);

        int callbacks = 0;

        ArgumentNullException refused = await Assert.ThrowsAsync<ArgumentNullException>(() => provider.ExecuteAsync(
            f.Payload, UnrestrictedTurnBudget.Instance,
            _ => { callbacks++; return Task.CompletedTask; }, f.Admission(), CancellationToken.None));

        Assert.Equal("reservations", refused.ParamName);

        Assert.Equal(0, callbacks);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM InferenceRuns;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));
    }
}
