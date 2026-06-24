using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class ScryingPoolTests
{

    [Fact]
    public async Task Publish_delivers_to_all_subscribers()
    {

        ScryingPool<string> pool = new(capacity: 4);

        ChannelReader<string> readerA = pool.Subscribe(out Guid subA);

        ChannelReader<string> readerB = pool.Subscribe(out Guid subB);

        pool.Publish("alpha");

        string a = await readerA.ReadAsync();

        string b = await readerB.ReadAsync();

        Assert.Equal("alpha", a);

        Assert.Equal("alpha", b);

        Assert.False(pool.Unsubscribe(subA));

        Assert.False(pool.Unsubscribe(subA));

        Assert.True(pool.Unsubscribe(subB));

    }

    [Fact]
    public void SubscriberCount_tracks_active_subscriptions()
    {

        ScryingPool<int> pool = new(capacity: 2);

        Assert.Equal(0, pool.SubscriberCount);

        _ = pool.Subscribe(out Guid sub);

        Assert.Equal(1, pool.SubscriberCount);

        pool.Unsubscribe(sub);

        Assert.Equal(0, pool.SubscriberCount);

    }

}
