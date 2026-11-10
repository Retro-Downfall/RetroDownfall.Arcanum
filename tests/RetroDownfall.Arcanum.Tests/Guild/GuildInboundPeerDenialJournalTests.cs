using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildInboundPeerDenialJournalTests
{
    [Fact]
    public void Accepted_peer_denial_survives_process_restart_and_clears_only_after_reconciliation()
    {
        string root = Directory.CreateTempSubdirectory("guild-peer-denial-").FullName;

        try
        {
            GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

            using (GuildInboundPeerDenialJournal first = new(root, gate))
            {
                Assert.True(first.Publish(["alpha"]).IsSuccess);

                Assert.True(first.IsDenied("alpha"));

                Assert.False(first.IsDenied("beta"));
            }

            using (GuildInboundPeerDenialJournal restarted = new(root, gate))
            {
                Assert.True(restarted.IsDenied("alpha"));

                Assert.True(restarted.Clear().IsSuccess);
            }

            using GuildInboundPeerDenialJournal settled = new(root, gate);

            Assert.False(settled.HasPending);

            Assert.False(settled.IsDenied("alpha"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Unsafe_journal_directory_fails_closed_without_replacing_the_operators_file()
    {
        string root = Directory.CreateTempSubdirectory("guild-peer-denial-").FullName;

        string path = Path.Combine(root, "denials");

        File.WriteAllText(path, "operator bytes");

        try
        {
            using GuildInboundPeerDenialJournal journal = new(path, new GrimoireConnectionAdmissionGate(TimeProvider.System));

            Assert.True(journal.Publish(["alpha"]).IsFailure);

            Assert.False(journal.IsHealthy);

            Assert.Equal("operator bytes", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
