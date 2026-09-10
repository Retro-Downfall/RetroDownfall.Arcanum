using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class UnseenServantWatermarkStoreTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionAndOperatorChangePreserveEachOthersFields(bool completionFirst)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string key = "watch\0patrol";

        DateTimeOffset earlier = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset completed = earlier.AddDays(1);

        await _store!.SaveAsync(key, earlier, 5);

        if (completionFirst)
        {
            await SaveCompletionAsync(key, completed, 5);

            await SaveOperatorIntervalAsync(key, earlier, 30);
        }
        else
        {
            await SaveOperatorIntervalAsync(key, earlier, 30);

            await SaveCompletionAsync(key, completed, 5);
        }

        UnseenServantWatermark row = Assert.IsType<UnseenServantWatermark>(await _store.GetAsync(key));

        Assert.Equal(completed, row.LastRunAt);

        Assert.Equal(30, row.EffectiveIntervalMinutes);

        Assert.Equal(key, row.JobKey);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldSpecificFirstInsertPreservesTheExistingInitialValues(bool intervalOnly)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        DateTimeOffset now = new(2026, 7, 1, 9, 30, 0, TimeSpan.Zero);

        if (intervalOnly)
        {
            await SaveOperatorIntervalAsync("watch\0patrol", now, 30);
        }
        else
        {
            await SaveCompletionAsync("watch\0patrol", now, 5);
        }

        UnseenServantWatermark row = Assert.IsType<UnseenServantWatermark>(await _store!.GetAsync("watch\0patrol"));

        Assert.Equal(now, row.LastRunAt);

        Assert.Equal(intervalOnly ? 30 : 5, row.EffectiveIntervalMinutes);
    }

    private Task SaveCompletionAsync(string key, DateTimeOffset completed, int initialInterval) =>
        _store!.SaveLastRunAsync(key, completed, initialInterval);

    private Task SaveOperatorIntervalAsync(string key, DateTimeOffset initialTime, int interval) =>
        _store!.SaveIntervalAsync(key, initialTime, interval);
}
