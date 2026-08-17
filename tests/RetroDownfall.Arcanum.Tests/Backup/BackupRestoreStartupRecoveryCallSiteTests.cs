using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// What startup restore recovery is structurally incapable of doing.
/// </summary>
/// <remarks>
/// Two of the guarantees this slice owes are properties of the call graph rather than of any single
/// result. Physical topology recovery runs before a live database has been classified, so it must not
/// be able to open one; and a restore that already closed a scope must be resumed rather than acquired,
/// because initial acquisition for an existing owner creates a second operation that also believes it
/// may reopen admission. Both fail as a new call site rather than as a wrong answer, which is why they
/// are inventory assertions over production source (§10.19.8).
/// </remarks>
public sealed class BackupRestoreStartupRecoveryCallSiteTests
{

    private const string RecoveryFileName = "BackupRestoreRecovery.cs";

    private const string TopologyFileName = "BackupRestorePhysicalTopology.cs";

    [Fact]
    public void Startup_restore_recovery_resumes_its_owner_and_never_acquires_a_new_one()
    {

        ProductionSource recovery = Source(RecoveryFileName);

        Assert.True(
            recovery.Names("ResumeExclusiveAsync"),
            "Authority recovery has to resume the exact journaled owner.");

        Assert.False(
            recovery.Names("AcquireExclusiveAsync"),
            "Initial acquisition for a journaled owner would create a second operation for a scope "
            + "that is already closed, and the gate would then hold two owners each believing it may "
            + "reopen admission.");

    }

    [Fact]
    public void Startup_restore_recovery_opens_no_sqlcipher_handle_of_its_own()
    {

        foreach (ProductionSource source in (ProductionSource[])[Source(RecoveryFileName), Source(TopologyFileName)])
        {

            Assert.False(
                source.Names("SqliteConnection") || source.Names("SqliteCommand"),
                "Physical topology recovery runs before any live database is opened or classified, and "
                + "the database-dependent half delegates to the marker lifecycle that owns the live "
                + "connection: " + source.RelativePath);

        }

    }

    private static ProductionSource Source(string fileName) =>
        Assert.Single(ProductionSourceInventory.Sources(), source => source.Is(fileName));

}
