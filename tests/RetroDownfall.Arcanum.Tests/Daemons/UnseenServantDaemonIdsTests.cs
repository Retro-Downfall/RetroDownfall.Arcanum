using RetroDownfall.Arcanum.Infrastructure.Daemons;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class UnseenServantDaemonIdsTests
{

    [Fact]
    public void ForJobName_prefixes_job_name()
    {

        string id = UnseenServantDaemonIds.ForJobName("watchtower");

        Assert.Equal("unseen-servant:watchtower", id);

    }

    [Theory]
    [InlineData("unseen-servant:watchtower", "watchtower")]
    [InlineData("other:job", null)]
    [InlineData("unseen-servant:", null)]
    public void JobNameFromId_round_trips_valid_ids(string daemonId, string? expected)
    {

        string? jobName = UnseenServantDaemonIds.JobNameFromId(daemonId);

        Assert.Equal(expected, jobName);

    }

}
