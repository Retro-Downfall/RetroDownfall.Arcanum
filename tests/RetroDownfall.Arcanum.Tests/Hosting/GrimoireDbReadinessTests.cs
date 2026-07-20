using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class GrimoireDbReadinessTests
{

    [Fact]
    public void MarkReady_sets_IsReady_true()
    {

        GrimoireDbReadiness readiness = new();

        Assert.False(readiness.IsReady);

        readiness.MarkReady();

        Assert.True(readiness.IsReady);

    }

    [Fact]
    public async Task WaitUntilReadyAsync_Completes_After_MarkReady()
    {
        GrimoireDbReadiness readiness = new();

        Task wait = readiness.WaitUntilReadyAsync();
        Assert.False(wait.IsCompleted);

        readiness.MarkReady();

        await wait;
        Assert.True(readiness.IsReady);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_Faults_After_MarkFailed()
    {
        GrimoireDbReadiness readiness = new();
        InvalidOperationException boom = new("bootstrap failed");

        Task wait = readiness.WaitUntilReadyAsync();
        readiness.MarkFailed(boom);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Same(boom, ex);
        Assert.False(readiness.IsReady);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_Cancels_When_Token_Cancelled()
    {
        GrimoireDbReadiness readiness = new();
        using CancellationTokenSource cts = new();

        Task wait = readiness.WaitUntilReadyAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_Is_Immediate_When_Already_Ready()
    {
        GrimoireDbReadiness readiness = new();
        readiness.MarkReady();

        await readiness.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

}
