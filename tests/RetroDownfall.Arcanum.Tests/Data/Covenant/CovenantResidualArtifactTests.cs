using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The classification an erasure's absence proof is made of: which files count as residue, which of
/// them this path is entitled to clear, and what an operator is told about them.
/// </summary>
/// <remarks>
/// Unit-level and file-system-level rather than database-level, because the failure these assertions
/// prevent is a file class nobody looked for. The Grimoire directory also holds configuration,
/// spells, attachments, and managed files, so a sweep that is too wide refuses on a working
/// installation and a sweep that is too narrow reports an absence it never checked.
/// </remarks>
public sealed class CovenantResidualArtifactTests : IDisposable
{

    private readonly string _root =
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"covenant-residual-{Guid.NewGuid():N}")).FullName;

    private string DatabasePath => Path.Combine(_root, "arcanum.db");

    /// <summary>
    /// The transition every staging file in this suite is written on behalf of.
    /// </summary>
    /// <remarks>
    /// Fixed rather than fresh per test, because none of these assertions is about which operation
    /// owns a file — they are about whether the classifier and the sweep still recognise a staging
    /// file whose name now carries one.
    /// </remarks>
    private static Guid StagingOperation { get; } = new("7fa1f6b4-2c39-4de1-9a70-6cf0a1d3e845");

    /// <summary>
    /// The expected class travels as its numeric code because the classification is internal to the
    /// persistence assembly and a public theory signature cannot name it. The cast back is what is
    /// actually asserted.
    /// </summary>
    [Theory]
    [InlineData("arcanum.db-wal", (int)CovenantResidualArtifactClass.WriteAheadLog)]
    [InlineData("arcanum.db-shm", (int)CovenantResidualArtifactClass.SharedMemoryIndex)]
    [InlineData("arcanum.db-journal", (int)CovenantResidualArtifactClass.RollbackJournal)]
    [InlineData("arcanum.db-mj0A1B2C3D", (int)CovenantResidualArtifactClass.TemporaryDatabase)]
    [InlineData("arcanum.db-tmp7", (int)CovenantResidualArtifactClass.TemporaryDatabase)]
    [InlineData("etilqs_9f2c1b", (int)CovenantResidualArtifactClass.TemporaryDatabase)]
    [InlineData("arcanum.db.arcanum-covenant-export", (int)CovenantResidualArtifactClass.ExportStaging)]
    [InlineData("arcanum.db.arcanum-covenant-replace", (int)CovenantResidualArtifactClass.ExportStaging)]
    [InlineData(".arcanum-bak-0123456789abcdef", (int)CovenantResidualArtifactClass.ReplacedOriginal)]
    [InlineData(".arcanum-quarantine-0123456789abcdef", (int)CovenantResidualArtifactClass.ReplacedOriginal)]
    public void Every_residual_file_shape_is_classified(string fileName, int expected) =>
        Assert.Equal(
            (CovenantResidualArtifactClass)expected,
            CovenantResidualArtifacts.Classify("arcanum.db", fileName));

    [Theory]

    // The database itself is not residue, and neither is anything else the Grimoire directory holds.
    // A proof that refused on these would refuse on an installation working exactly as intended.
    [InlineData("arcanum.db")]
    [InlineData("arcanum.json")]
    [InlineData("mcp.json")]
    [InlineData("arcanum.dbx")]
    [InlineData("arcanum.db.backup")]
    [InlineData("notes.md")]
    public void Nothing_else_in_the_grimoire_directory_is_residue(string fileName) =>
        Assert.Null(CovenantResidualArtifacts.Classify("arcanum.db", fileName));

    /// <summary>
    /// The replace primitive's own prefixes are read from it rather than copied, so a rename there
    /// cannot silently leave this proof looking for a file that no longer has that name.
    /// </summary>
    [Fact]
    public void The_replaced_original_prefixes_come_from_the_replace_primitive()
    {

        Assert.Equal(
            CovenantResidualArtifactClass.ReplacedOriginal,
            CovenantResidualArtifacts.Classify("arcanum.db", AtomicFile.BackupPrefix + "abc"));

        Assert.Equal(
            CovenantResidualArtifactClass.ReplacedOriginal,
            CovenantResidualArtifacts.Classify("arcanum.db", AtomicFile.QuarantinePrefix + "abc"));

    }

    [Fact]
    public void Survivors_report_every_class_present_and_nothing_else()
    {

        File.WriteAllText(DatabasePath, "database");

        File.WriteAllText(DatabasePath + "-wal", "wal");

        File.WriteAllText(DatabasePath + "-shm", "shm");

        File.WriteAllText(DatabasePath + "-journal", "journal");

        File.WriteAllText(Path.Combine(_root, "etilqs_1"), "temp");

        File.WriteAllText(CovenantResidualArtifacts.ExportStagingPath(DatabasePath, StagingOperation), "staging");

        File.WriteAllText(Path.Combine(_root, AtomicFile.BackupPrefix + "1"), "backup");

        File.WriteAllText(Path.Combine(_root, "arcanum.json"), "{}");

        Assert.Equal(
            CovenantResidualArtifacts.Declared,
            CovenantResidualArtifacts.Survivors(DatabasePath));

    }

    [Fact]
    public void A_clean_directory_reports_no_survivor()
    {

        File.WriteAllText(DatabasePath, "database");

        File.WriteAllText(Path.Combine(_root, "arcanum.json"), "{}");

        Assert.Empty(CovenantResidualArtifacts.Survivors(DatabasePath));

    }

    /// <summary>
    /// The report names classes and never a location. It is written into an error body, a log line,
    /// and an HTTP response, none of which may disclose where an installation's protected storage is.
    /// </summary>
    [Fact]
    public void The_report_names_classes_and_never_a_path()
    {

        string described = CovenantResidualArtifacts.Describe(CovenantResidualArtifacts.Declared);

        Assert.Contains("write-ahead log", described, StringComparison.Ordinal);

        Assert.Contains("export staging", described, StringComparison.Ordinal);

        Assert.Contains("replaced database", described, StringComparison.Ordinal);

        Assert.DoesNotContain(_root, described, StringComparison.Ordinal);

        Assert.DoesNotContain("arcanum.db", described, StringComparison.Ordinal);

        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), described, StringComparison.Ordinal);

    }

    /// <summary>
    /// Only this path's own litter. A proof that also deleted a write-ahead log would be clearing the
    /// one piece of evidence that says a handle is still holding the database open, and one that
    /// deleted a backup or a quarantined copy would be deleting the file an operator is told to go
    /// and read when a replace could not verify what it installed.
    /// </summary>
    [Fact]
    public void Removing_own_staging_clears_this_paths_artifacts_and_touches_nothing_else()
    {

        File.WriteAllText(DatabasePath, "database");

        File.WriteAllText(DatabasePath + "-wal", "wal");

        File.WriteAllText(DatabasePath + "-shm", "shm");

        File.WriteAllText(CovenantResidualArtifacts.ExportStagingPath(DatabasePath, StagingOperation), "staging");

        File.WriteAllText(CovenantResidualArtifacts.ReplacementStagingPath(DatabasePath), "replacement");

        File.WriteAllText(Path.Combine(_root, AtomicFile.BackupPrefix + "1"), "backup");

        File.WriteAllText(Path.Combine(_root, AtomicFile.QuarantinePrefix + "1"), "quarantine");

        Result removed = CovenantResidualArtifacts.RemoveOwnStaging(DatabasePath);

        Assert.True(removed.IsSuccess, removed.IsFailure ? removed.Error.Message : null);

        Assert.Equal(
            [
                CovenantResidualArtifactClass.WriteAheadLog,
                CovenantResidualArtifactClass.SharedMemoryIndex,
                CovenantResidualArtifactClass.ReplacedOriginal,
            ],
            CovenantResidualArtifacts.Survivors(DatabasePath));

        Assert.True(File.Exists(DatabasePath));

        Assert.True(
            File.Exists(Path.Combine(_root, AtomicFile.BackupPrefix + "1")),
            "the backup an unverifiable replace keeps for operator recovery was deleted.");

        Assert.True(
            File.Exists(Path.Combine(_root, AtomicFile.QuarantinePrefix + "1")),
            "the quarantined copy an unverifiable replace keeps for operator recovery was deleted.");

    }

    [Fact]
    public void A_directory_that_does_not_exist_reports_nothing_rather_than_throwing()
    {

        string absent = Path.Combine(_root, "missing", "arcanum.db");

        Assert.Empty(CovenantResidualArtifacts.Survivors(absent));

        Assert.True(CovenantResidualArtifacts.RemoveOwnStaging(absent).IsSuccess);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_root, recursive: true);

        }
        catch (IOException)
        {

            // Scratch under the OS temp root; a scanner still holding a handle must not fail a test
            // that has already made its assertions.

        }

    }

}
