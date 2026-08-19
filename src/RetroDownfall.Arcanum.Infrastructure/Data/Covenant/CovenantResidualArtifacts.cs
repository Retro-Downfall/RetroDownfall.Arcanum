using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// A kind of file that may sit beside the Grimoire, and that a proven local erasure must not leave
/// behind.
/// </summary>
/// <remarks>
/// A class rather than a path, because the class is the whole of what an operator may be told. A
/// residual sidecar is reported by saying which kind of artifact survived; naming the file would put
/// the location of an installation's protected storage into an error body, a log line, and an HTTP
/// response, none of which the Covenant is allowed to disclose (§10.20.6).
///
/// <para>No member is zero. A zero would be indistinguishable from a default-initialized field, and
/// "no artifact class" is the one answer this type must never give by accident.</para>
/// </remarks>
internal enum CovenantResidualArtifactClass
{

    /// <summary>The write-ahead log. Its survival means a handle is still holding the database.</summary>
    WriteAheadLog = 1,

    /// <summary>The wal-index shared-memory file, which survives for the same reason.</summary>
    SharedMemoryIndex = 2,

    /// <summary>A rollback journal, left by a delete-mode write that did not finish.</summary>
    RollbackJournal = 3,

    /// <summary>A temporary or master-journal file the engine writes beside the database.</summary>
    TemporaryDatabase = 4,

    /// <summary>An export-and-replace staging file this erasure wrote and did not remove.</summary>
    ExportStaging = 5,

    /// <summary>A backup or quarantined copy of a replaced database.</summary>
    ReplacedOriginal = 6,

}

/// <summary>
/// The one inventory of every file class an erasure may leave beside the Grimoire, and the only
/// place their names are written down.
/// </summary>
/// <remarks>
/// The absence proof is positive: it enumerates what is actually there and classifies it, rather than
/// deleting a list of names and reporting success. A helper that only deletes proves nothing — it
/// cannot distinguish "there was nothing to remove" from "the removal failed and nobody looked", and
/// those are opposite answers to the question an erasure exists to settle.
///
/// <para>The sweep is scoped to the database's own name and to the two prefixes the shared
/// atomic-replace primitive creates, never to the whole directory. The Grimoire directory also holds
/// configuration, spells, attachments, and managed files, and a proof that refused on every unexpected
/// file in it would refuse on an installation that is working exactly as intended.</para>
/// </remarks>
internal static class CovenantResidualArtifacts
{

    /// <summary>
    /// The suffix an export staging file carries, appended to the database's own file name so it
    /// lands in the same directory and on the same filesystem the atomic rename needs.
    /// </summary>
    internal const string ExportStagingSuffix = ".arcanum-covenant-export";

    /// <summary>The suffix of the temporary file the atomic replace writes before its rename.</summary>
    internal const string ReplacementStagingSuffix = ".arcanum-covenant-replace";

    /// <summary>Every declared class, in the order a report names them.</summary>
    internal static IReadOnlyList<CovenantResidualArtifactClass> Declared { get; } =
        Array.AsReadOnly(Enum.GetValues<CovenantResidualArtifactClass>());

    /// <summary>
    /// The classes that mean a handle is still holding the database open, rather than that a previous
    /// pass left something behind.
    /// </summary>
    /// <remarks>
    /// Separated because they answer a different question and have a different remedy. A write-ahead
    /// log or a wal-index that survives a drain names a connection nobody enrolled; a staging file
    /// names a pass that stopped between writing and installing, and the second is this path's own
    /// litter to clear rather than a reason to stop.
    /// </remarks>
    internal static IReadOnlyList<CovenantResidualArtifactClass> LiveHandleClasses { get; } =
        Array.AsReadOnly<CovenantResidualArtifactClass>(
        [
            CovenantResidualArtifactClass.WriteAheadLog,
            CovenantResidualArtifactClass.SharedMemoryIndex,
            CovenantResidualArtifactClass.RollbackJournal,
        ]);

    /// <summary>
    /// The one class an interrupted export-and-replace is entitled to have left behind, and that this
    /// path may therefore clear rather than refuse on.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantResidualArtifactClass.ReplacedOriginal"/> is deliberately not in it. The
    /// shared atomic-replace primitive keeps its backup or quarantine copy exactly when it could not
    /// verify what it installed, precisely so an operator can establish which database is in place —
    /// so a proof that swept it would delete the one file that answers the question its own refusal
    /// tells the operator to go and answer. A replaced original that survives is refused, never
    /// cleared.
    /// </remarks>
    internal static IReadOnlyList<CovenantResidualArtifactClass> OwnStagingClasses { get; } =
        Array.AsReadOnly<CovenantResidualArtifactClass>(
        [
            CovenantResidualArtifactClass.ExportStaging,
        ]);

    /// <summary>The path an export writes its candidate database to.</summary>
    internal static string ExportStagingPath(string databasePath) =>
        databasePath + ExportStagingSuffix;

    /// <summary>The path the atomic replace stages the installed database at.</summary>
    internal static string ReplacementStagingPath(string databasePath) =>
        databasePath + ReplacementStagingSuffix;

    /// <summary>
    /// Classifies one file name against the database's own, or reports that it is none of these.
    /// </summary>
    /// <remarks>
    /// Ordinal comparison throughout, and the two staging suffixes are tested before the generic
    /// sidecar suffixes, because <c>arcanum.db.arcanum-covenant-export</c> and
    /// <c>arcanum.db-wal</c> are both "the database's name plus something" and only an ordered test
    /// keeps them apart.
    /// </remarks>
    internal static CovenantResidualArtifactClass? Classify(string databaseFileName, string candidateFileName)
    {

        ArgumentException.ThrowIfNullOrEmpty(databaseFileName);

        ArgumentException.ThrowIfNullOrEmpty(candidateFileName);

        if (candidateFileName.StartsWith(AtomicFile.BackupPrefix, StringComparison.Ordinal)
            || candidateFileName.StartsWith(AtomicFile.QuarantinePrefix, StringComparison.Ordinal))
        {

            return CovenantResidualArtifactClass.ReplacedOriginal;

        }

        // SQLite's own scratch files are named for the connection rather than for the database, so
        // they are matched by their engine prefix rather than against this database's name.
        if (candidateFileName.StartsWith("etilqs_", StringComparison.Ordinal))
        {

            return CovenantResidualArtifactClass.TemporaryDatabase;

        }

        if (!candidateFileName.StartsWith(databaseFileName, StringComparison.Ordinal)
            || candidateFileName.Length == databaseFileName.Length)
        {

            return null;

        }

        string remainder = candidateFileName[databaseFileName.Length..];

        if (remainder.StartsWith(ExportStagingSuffix, StringComparison.Ordinal)
            || remainder.StartsWith(ReplacementStagingSuffix, StringComparison.Ordinal))
        {

            return CovenantResidualArtifactClass.ExportStaging;

        }

        return remainder switch
        {

            "-wal" => CovenantResidualArtifactClass.WriteAheadLog,

            "-shm" => CovenantResidualArtifactClass.SharedMemoryIndex,

            "-journal" => CovenantResidualArtifactClass.RollbackJournal,

            // A master journal is "-mj" plus eight hexadecimal digits, and a vacuum or statement
            // scratch file is "-tmp" plus a counter. Both are matched by prefix because the tail is
            // the engine's own and this proof has no business predicting it.
            _ when remainder.StartsWith("-mj", StringComparison.Ordinal)
                || remainder.StartsWith("-tmp", StringComparison.Ordinal) =>
                CovenantResidualArtifactClass.TemporaryDatabase,

            _ => null,

        };

    }

    /// <summary>
    /// Enumerates the database's directory and reports every residual class that currently exists.
    /// </summary>
    /// <remarks>
    /// A missing directory reports nothing rather than throwing. There is no installation to prove
    /// anything about at that point, and the caller's own database-absent checks are the ones that
    /// should say so.
    /// </remarks>
    internal static IReadOnlyList<CovenantResidualArtifactClass> Survivors(string databasePath)
    {

        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? string.Empty;

        string databaseFileName = Path.GetFileName(databasePath);

        if (directory.Length == 0 || !Directory.Exists(directory))
        {

            return [];

        }

        HashSet<CovenantResidualArtifactClass> found = [];

        foreach (string file in Directory.EnumerateFiles(directory))
        {

            if (Classify(databaseFileName, Path.GetFileName(file)) is { } artifact)
            {

                _ = found.Add(artifact);

            }

        }

        return [.. Declared.Where(found.Contains)];

    }

    /// <summary>
    /// Names the surviving classes for an operator, and nothing else.
    /// </summary>
    internal static string Describe(IReadOnlyList<CovenantResidualArtifactClass> survivors)
    {

        ArgumentNullException.ThrowIfNull(survivors);

        return string.Join(", ", survivors.Select(static survivor => Name(survivor)));

    }

    /// <summary>
    /// Removes the artifacts an interrupted pass of this path is entitled to have left behind.
    /// </summary>
    /// <remarks>
    /// Only this path's own class, and only by the names this file owns. A staging file is a complete
    /// encrypted copy of a database this erasure is in the middle of replacing, so leaving one behind
    /// would leave a second copy of state the erasure has promised to compact — while deleting
    /// anything else would make a proof into a cleaner, which is the one thing the absence proof must
    /// never become.
    /// </remarks>
    internal static Result RemoveOwnStaging(string databasePath)
    {

        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        string directory = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? string.Empty;

        string databaseFileName = Path.GetFileName(databasePath);

        if (directory.Length == 0 || !Directory.Exists(directory))
        {

            return Result.Success();

        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {

            if (Classify(databaseFileName, Path.GetFileName(file)) is not { } artifact
                || !OwnStagingClasses.Contains(artifact))
            {

                continue;

            }

            try
            {

                File.Delete(file);

            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {

                return new Error(
                    ErrorCodes.Covenant.ErasureIncomplete,
                    $"A Covenant erasure could not remove a residual artifact of class {Name(artifact)}, "
                    + "so local erasure is incomplete.");

            }

        }

        return Result.Success();

    }

    /// <summary>The operator-facing name of one class, which never contains a path.</summary>
    private static string Name(CovenantResidualArtifactClass artifact) =>
        artifact switch
        {

            CovenantResidualArtifactClass.WriteAheadLog => "write-ahead log",

            CovenantResidualArtifactClass.SharedMemoryIndex => "shared-memory index",

            CovenantResidualArtifactClass.RollbackJournal => "rollback journal",

            CovenantResidualArtifactClass.TemporaryDatabase => "temporary database",

            CovenantResidualArtifactClass.ExportStaging => "export staging",

            _ => "replaced database",

        };

}
