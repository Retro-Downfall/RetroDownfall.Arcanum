using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class SseConnectionCounterTests
{

    [Fact]
    public void Increment_returns_new_count()
    {

        SseConnectionCounter counter = new();

        Assert.Equal(1, counter.Increment("DaemonEvent"));

        Assert.Equal(2, counter.Increment("DaemonEvent"));

        Assert.Equal(3, counter.Increment("DaemonEvent"));

    }

    [Fact]
    public void Decrement_decreases_count()
    {

        SseConnectionCounter counter = new();

        counter.Increment("LogEntry");

        counter.Increment("LogEntry");

        counter.Decrement("LogEntry");

        Assert.Equal(1, counter.GetCount("LogEntry"));

    }

    [Fact]
    public void Decrement_does_not_go_below_zero()
    {

        SseConnectionCounter counter = new();

        counter.Decrement("McpServerEvent");

        counter.Decrement("McpServerEvent");

        Assert.Equal(0, counter.GetCount("McpServerEvent"));

    }

    [Fact]
    public void GetCount_returns_zero_for_unknown_type()
    {

        SseConnectionCounter counter = new();

        Assert.Equal(0, counter.GetCount("Chronicle"));

    }

    [Fact]
    public void Concurrent_access_is_thread_safe()
    {

        SseConnectionCounter counter = new();

        const string eventType = "SessionStream";

        const int threadCount = 16;

        const int iterationsPerThread = 500;

        Thread[] threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {

            threads[i] = new Thread(() =>
            {

                for (int j = 0; j < iterationsPerThread; j++)
                {

                    counter.Increment(eventType);

                    counter.Decrement(eventType);

                }

            });

        }

        foreach (Thread thread in threads)
        {

            thread.Start();

        }

        foreach (Thread thread in threads)
        {

            thread.Join();

        }

        Assert.Equal(0, counter.GetCount(eventType));

    }

}
