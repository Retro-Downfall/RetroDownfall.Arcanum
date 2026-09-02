using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Every backup component that opens a SQLite connection of its own installs the native provider
/// first.
/// </summary>
/// <remarks>
/// The project references a SQLCipher provider with no bundle and no auto-initialization, so
/// <c>raw.SetProvider</c> has to run explicitly before the first connection opens. Components that
/// open through <c>BackupRestoreDatabaseWorker</c> inherit that call; the four files here build their
/// own connection string and open it directly. Two of them worked only because something earlier in
/// the process had already installed the provider — a host that reached a backup inventory or a
/// snapshot rewrite without ever opening the Grimoire would fail on a missing symbol rather than as
/// the typed unavailability the design promises.
///
/// <para>An inventory rather than a behavioural case, because the failure it prevents is a new call
/// site rather than a wrong result — and scoped to the backup family, which is the family this suite
/// owns. The tree-wide version of this rule belongs to the build inventory that scans every project.</para>
///
/// <para>Both counts are pinned per file rather than only their relation. One installation can serve
/// several openers, but only when it is on the same file's entry path — the snapshotter opens three
/// connections beneath one entry point that installs the provider first — and that is a claim about a
/// file's shape, so a change to either number has to be looked at rather than absorbed.</para>
/// </remarks>
public sealed class BackupSqliteNativeRuntimeCallSiteTests
{

    /// <summary>
    /// Both spellings of a provider connection construction, whatever the variable is called.
    /// </summary>
    /// <remarks>
    /// Matched by shape rather than by a fixed list of declarations: the snapshotter names two of its
    /// connections <c>source</c> and <c>destination</c>, and an inventory keyed to
    /// <c>connection</c> would report a file with three openers as having one.
    /// </remarks>
    private static readonly Regex Opener = new(
        @"SqliteConnection\s+\w+\s*=\s*new\(|new\s+SqliteConnection\(",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs", 2, 2)]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupInventoryPlanner.cs", 1, 1)]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupArchiveCodec.cs", 1, 1)]
    [InlineData("src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupDatabaseSnapshotter.cs", 3, 1)]
    public void Backup_components_that_open_their_own_connection_install_the_provider_first(
        string repositoryRelativePath,
        int expectedOpeners,
        int expectedInstallations)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(repositoryRelativePath));

        Assert.Equal(expectedOpeners, Opener.Matches(source.Text).Count);

        int installations = source.Occurrences("SqliteNativeRuntime.Instance.Initialize()");

        Assert.True(
            installations > 0,
            $"{repositoryRelativePath} opens a SQLite connection without installing the native "
            + "provider on its own path.");

        Assert.Equal(expectedInstallations, installations);

    }

}
