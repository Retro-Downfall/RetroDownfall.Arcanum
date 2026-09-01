using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Every backup component that opens a SQLite connection of its own installs the native provider
/// first.
/// </summary>
/// <remarks>
/// The project references a SQLCipher provider with no bundle and no auto-initialization, so
/// <c>raw.SetProvider</c> has to run explicitly before the first connection opens. Components that
/// open through <c>BackupRestoreDatabaseWorker</c> inherit that call; the ones here build their own
/// connection string and open it directly, and worked only because something earlier in the process
/// had already installed the provider. A host that reaches a backup inventory without ever opening
/// the Grimoire gets a missing-symbol failure instead of the typed unavailability the design
/// promises.
///
/// <para>An inventory rather than a behavioural case, because the failure it prevents is a new call
/// site rather than a wrong result — and scoped to the backup family, which is the family this suite
/// owns. The tree-wide version of this rule belongs to the build inventory that scans every project.</para>
/// </remarks>
public sealed class BackupSqliteNativeRuntimeCallSiteTests
{

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs", 2)]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs", 1)]
    public void Backup_components_that_open_their_own_connection_install_the_provider_first(
        string repositoryRelativePath,
        int expectedOpeners)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(repositoryRelativePath));

        int openers = source.Occurrences("SqliteConnection connection = new(")
            + source.Occurrences("new SqliteConnection(");

        // The count is pinned as well as the coverage, so an opener added in either form has to be
        // accounted for here rather than silently inheriting somebody else's initialization.
        Assert.Equal(expectedOpeners, openers);

        Assert.True(
            source.Occurrences("SqliteNativeRuntime.Instance.Initialize()") >= openers,
            $"{repositoryRelativePath} opens a SQLite connection without installing the native "
            + "provider on its own path.");

    }

}
