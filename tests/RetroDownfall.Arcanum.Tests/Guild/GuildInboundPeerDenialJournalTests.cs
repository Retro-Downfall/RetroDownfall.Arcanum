using System.Security.AccessControl;
using System.Security.Principal;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildInboundPeerDenialJournalTests
{
    [Fact]
    public void Accepted_peer_denial_survives_process_restart_and_clears_only_after_reconciliation()
    {
        string root = Directory.CreateTempSubdirectory("guild-peer-denial-").FullName;

        string path = Path.Combine(root, "denials");

        try
        {
            GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

            Assert.False(Directory.Exists(path));

            using (GuildInboundPeerDenialJournal first = new(path, gate))
            {
                Assert.True(first.IsHealthy);

                Result published = first.Publish(["alpha"]);

                Assert.True(published.IsSuccess, published.IsFailure ? published.Error.ToString() : null);

                Assert.True(Directory.Exists(path));

                Assert.True(first.IsDenied("alpha"));

                Assert.False(first.IsDenied("beta"));
            }

            using (GuildInboundPeerDenialJournal restarted = new(path, gate))
            {
                Assert.True(restarted.IsDenied("alpha"));

                Assert.True(restarted.Clear().IsSuccess);
            }

            using GuildInboundPeerDenialJournal settled = new(path, gate);

            Assert.False(settled.HasPending);

            Assert.False(settled.IsDenied("alpha"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Existing_nonprivate_journal_directory_fails_closed_without_permission_repair()
    {
        string root = Directory.CreateTempSubdirectory("guild-peer-denial-").FullName;

        string path = Path.Combine(root, "denials");

        try
        {
            SecureFilePermissions.RequireOwnerOnlyDirectory(path);

            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo directory = new(path);

                DirectorySecurity permissions = directory.GetAccessControl();

                permissions.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
                    FileSystemRights.Read,
                    AccessControlType.Allow));

                directory.SetAccessControl(permissions);
            }
            else
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.GroupRead);
            }

            string before = PermissionSnapshot(path);

            using GuildInboundPeerDenialJournal journal = new(path, new GrimoireConnectionAdmissionGate(TimeProvider.System));

            Assert.False(journal.IsHealthy);

            Assert.True(journal.Publish(["alpha"]).IsFailure);

            Assert.True(journal.IsDenied("alpha"));

            Assert.True(journal.IsDenied("beta"));

            Assert.Empty(Directory.EnumerateFileSystemEntries(path));

            Assert.Equal(before, PermissionSnapshot(path));
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

    private static string PermissionSnapshot(string path) => OperatingSystem.IsWindows()
        ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All)
        : File.GetUnixFileMode(path).ToString();
}
