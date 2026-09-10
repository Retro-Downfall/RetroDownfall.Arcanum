using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantPacerPersistenceTests
{
    [Fact]
    public async Task IntervalChangeDisposesItsScopeAsynchronously()
    {
        await using UnseenServantPacerHarness harness = new();

        Assert.True(await harness.SetAsync(15));

        Assert.Equal(1, harness.AsyncDisposals);
    }

    [Fact]
    public async Task ConcurrentIntervalChangesCannotPersistAnOlderOverrideLast()
    {
        await using UnseenServantPacerHarness harness = new();

        UnseenServantAdmissionHarness.Checkpoint firstSave = new();

        TaskCompletionSource firstSaved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Watermarks.BeforeSave = token => harness.Watermarks.Saves == 1 ? firstSave.PauseAsync(token) : Task.CompletedTask;

        harness.Watermarks.AfterSave = interval =>
        {
            if (interval == 15)
            {
                firstSaved.TrySetResult();
            }
        };

        Task<bool> first = harness.SetAsync(15);

        await firstSave.WaitAsync();

        Task<bool> second = harness.SetAsync(30);

        firstSave.Release.TrySetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        await firstSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(30, harness.Pacer.GetEffectiveInterval(harness.Job));

        Assert.Equal(30, harness.Watermarks.Row!.EffectiveIntervalMinutes);
    }

    [Fact]
    public async Task IntervalChangeOwnsItsPersistenceUntilCompletion()
    {
        await using UnseenServantPacerHarness harness = new();

        UnseenServantAdmissionHarness.Checkpoint save = new();

        harness.Watermarks.BeforeSave = save.PauseAsync;

        Task<bool> change = harness.SetAsync(15);

        try
        {
            await save.WaitAsync();

            Assert.False(change.IsCompleted);
        }
        finally
        {
            save.Release.TrySetResult();

            await change.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StartupHydrationCannotOverwriteANewerOperatorOverride()
    {
        await using UnseenServantPacerHarness harness = new();

        Assert.True(await harness.SetAsync(30));

        await harness.Pacer.HydrateAsync([new UnseenServantWatermark("watch\0patrol", DateTimeOffset.UtcNow.AddHours(-1), 10)]);

        Assert.Equal(30, harness.Pacer.GetEffectiveInterval(harness.Job));

        Assert.Equal(30, harness.Watermarks.Row!.EffectiveIntervalMinutes);
    }

    [Fact]
    public async Task RepeatingAnOverrideRetriesItsPreviouslyFailedPersistenceWithoutAnExtraEvent()
    {
        await using UnseenServantPacerHarness harness = new();

        harness.Watermarks.BeforeSave = static _ => throw new IOException("store unavailable");

        Assert.True(await harness.SetAsync(30));

        Assert.Null(harness.Watermarks.Row);

        harness.Watermarks.BeforeSave = static _ => Task.CompletedTask;

        Assert.True(await harness.SetAsync(30));

        Assert.Equal(2, harness.Watermarks.Saves);

        Assert.Equal(30, harness.Watermarks.Row!.EffectiveIntervalMinutes);

        Assert.Single(harness.Events.Published);
    }
}
