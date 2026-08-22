using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Whether the PID file left by <c>arcanum serve</c> still names a live process. A stale entry is
/// what makes the next <c>arcanum serve</c> refuse to start, and it is the single most common piece
/// of crash residue on a local-first installation.
/// </summary>
public sealed class PidFileCheck : IDoctorCheck
{

    public const string CheckId = "runtime.pid_file";

    public string Id => CheckId;

    public string Name => "PID file";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Runtime;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Inspect());

    internal static DoctorFinding Inspect()
    {

        PidFilePosture posture = PidFilePosture.Read();

        return posture.Kind switch
        {
            PidFileKind.Absent => new DoctorFinding(
                DoctorOutcome.Healthy,
                "No PID file is present; no host has claimed this installation."),

            PidFileKind.Live => new DoctorFinding(
                DoctorOutcome.Healthy,
                $"A host is running (PID {posture.Pid})."),

            PidFileKind.Unreadable => new DoctorFinding(
                DoctorOutcome.Degraded,
                "The PID file exists but could not be read. Check the permissions on the Arcanum directory."),

            PidFileKind.Malformed => new DoctorFinding(
                DoctorOutcome.Degraded,
                "The PID file does not contain a process id. 'arcanum serve' will refuse to start until it is removed.",
                [StaleRemedy]),

            _ => new DoctorFinding(
                DoctorOutcome.Degraded,
                $"The PID file names process {posture.Pid}, which is no longer running. "
                + "'arcanum serve' treats this as crash residue and clears it on the next start.",
                [StaleRemedy]),
        };

    }

    private static DoctorRemedy StaleRemedy =>
        new(
            "runtime.clear_stale_pid",
            RemoveStalePidRepair.RepairId,
            DoctorRemedyCommands.RepairStalePidFile,
            "Remove the PID file left behind by a host that is no longer running.");

}

internal enum PidFileKind
{

    Absent,

    Live,

    Stale,

    Malformed,

    Unreadable,

}

internal readonly record struct PidFilePosture(PidFileKind Kind, int Pid, string Path)
{

    internal static PidFilePosture Read()
    {

        string path = ArcanumRuntimeDefaults.Server.PidFilePath
            ?? System.IO.Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.pid");

        if (!File.Exists(path))
        {

            return new PidFilePosture(PidFileKind.Absent, 0, path);

        }

        string text;

        try
        {

            text = File.ReadAllText(path).Trim();

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return new PidFilePosture(PidFileKind.Unreadable, 0, path);

        }

        if (!int.TryParse(text, out int pid) || pid <= 0)
        {

            return new PidFilePosture(PidFileKind.Malformed, 0, path);

        }

        return new PidFilePosture(IsRunning(pid) ? PidFileKind.Live : PidFileKind.Stale, pid, path);

    }

    /// <summary>
    /// Liveness only, mirroring <c>PidFileService</c>. It cannot prove the process is Arcanum's — a
    /// recycled id looks identical — which is precisely why the repair refuses to act while any
    /// process holds that id, rather than trying to identify it.
    /// </summary>
    private static bool IsRunning(int pid)
    {

        try
        {

            using Process process = Process.GetProcessById(pid);

            return !process.HasExited;

        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {

            return false;

        }

    }

}

/// <summary>
/// Deletes a PID file whose process is gone. Fail-closed: it re-reads the posture at apply time and
/// refuses to act on anything except a confirmed <see cref="PidFileKind.Stale"/> or
/// <see cref="PidFileKind.Malformed"/> file, so a host that started between the plan and the apply
/// keeps its claim. Idempotent — once the file is gone the posture is <see cref="PidFileKind.Absent"/>
/// and the repair converges.
/// </summary>
public sealed class RemoveStalePidRepair : IDoctorRepair
{

    public const string RepairId = "runtime.remove_stale_pid";

    public string Id => RepairId;

    public DoctorSubsystem Subsystem => DoctorSubsystem.Runtime;

    public IReadOnlyList<string> DetectorIds => [PidFileCheck.CheckId];

    public string Description =>
        "Remove the PID file left behind by a host process that is no longer running.";

    public Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken)
    {

        PidFilePosture posture = PidFilePosture.Read();

        return Task.FromResult(posture.Kind switch
        {
            PidFileKind.Stale or PidFileKind.Malformed => new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Planned,
                "Would delete the stale PID file. Re-run with --apply to perform it.",
                [new DoctorRepairStep(posture.Path, DescribeBefore(posture), "removed")],
                null),

            PidFileKind.Absent => Converged(),

            _ => Refused(posture),
        });

    }

    public Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken)
    {

        PidFilePosture posture = PidFilePosture.Read();

        if (posture.Kind == PidFileKind.Absent)
        {

            return Task.FromResult(Converged());

        }

        if (posture.Kind is not (PidFileKind.Stale or PidFileKind.Malformed))
        {

            return Task.FromResult(Refused(posture));

        }

        try
        {

            File.Delete(posture.Path);

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Task.FromResult(new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Failed,
                "The stale PID file could not be deleted.",
                [new DoctorRepairStep(posture.Path, DescribeBefore(posture), "unchanged")],
                exception.GetType().Name));

        }

        return Task.FromResult(new DoctorRepairResult(
            RepairId,
            DoctorRepairState.Applied,
            "Removed the stale PID file.",
            [new DoctorRepairStep(posture.Path, DescribeBefore(posture), "removed")],
            null));

    }

    private static string DescribeBefore(PidFilePosture posture) =>
        posture.Kind == PidFileKind.Malformed
            ? "present, no process id"
            : $"present, names dead process {posture.Pid}";

    private static DoctorRepairResult Converged() =>
        new(
            RepairId,
            DoctorRepairState.AlreadyConverged,
            "No PID file is present; nothing to do.",
            [],
            null);

    private static DoctorRepairResult Refused(PidFilePosture posture) =>
        new(
            RepairId,
            DoctorRepairState.Skipped,
            posture.Kind == PidFileKind.Live
                ? $"A host is running as PID {posture.Pid}. Stop it before removing its PID file."
                : "The PID file could not be read, so it is not safe to remove.",
            [],
            null);

}

/// <summary>
/// Whether another process holds the exclusive installation-maintenance lock a restore takes.
/// Strictly read-only, unlike <c>ArcanumMaintenanceLock.CannotAcquireSafely</c>, which attempts an
/// acquisition and therefore creates the lock file as a side effect when the topology is admissible.
/// A diagnostic must not create the artifact it reports on, so this opens the existing file without
/// <c>FileMode.OpenOrCreate</c> and treats a sharing violation as "held".
/// </summary>
public sealed class MaintenanceLockCheck : IDoctorCheck
{

    public const string CheckId = "runtime.maintenance_lock";

    public string Id => CheckId;

    public string Name => "Maintenance lock";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Runtime;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
        => Task.FromResult(Inspect(ArcanumPaths.GrimoireDirectory));

    internal static DoctorFinding Inspect(string guardedRoot)
    {

        string path = ArcanumMaintenanceLock.LockPathFor(guardedRoot);

        string parent = Path.GetDirectoryName(path)!;

        Result<NoFollowPathTopologyKind> parentTopology =
            NoFollowPathTopology.Classify(parent);

        if (parentTopology.IsSuccess
            && parentTopology.Value is NoFollowPathTopologyKind.Absent)
        {

            return new DoctorFinding(
                DoctorOutcome.Healthy,
                "No maintenance lock file is present.");

        }

        if (parentTopology.IsFailure
            || parentTopology.Value is not NoFollowPathTopologyKind.Directory
            || !SecureFilePermissions.HasOwnerOnlyPosture(parent, isDirectory: true))
        {

            return UnsafeFinding();

        }

        Result<NoFollowPathTopologyKind> leafTopology =
            NoFollowPathTopology.Classify(path);

        if (leafTopology.IsSuccess
            && leafTopology.Value is NoFollowPathTopologyKind.Absent)
        {

            return new DoctorFinding(
                DoctorOutcome.Healthy,
                "No maintenance lock file is present.");

        }

        if (leafTopology.IsFailure
            || leafTopology.Value is not NoFollowPathTopologyKind.RegularFile
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata named)
            || named.Kind is not FileSystemObjectKind.RegularFile
            || named.HardLinkCount != 1
            || !SecureFilePermissions.HasOwnerOnlyPosture(path, isDirectory: false))
        {

            return UnsafeFinding();

        }

        try
        {

            using FileStream stream = new(
                path,
                new FileStreamOptions
                {

                    Mode = FileMode.Open,

                    Access = FileAccess.ReadWrite,

                    Share = FileShare.None,

                });

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    stream.SafeFileHandle,
                    out FileHandleMetadata opened)
                || opened.Kind is not FileSystemObjectKind.RegularFile
                || opened.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    named.Identity,
                    opened.Identity)
                || (!OperatingSystem.IsWindows()
                    && (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                            path,
                            out FileHandleMetadata namedAfterOpen)
                        || namedAfterOpen.Kind is not FileSystemObjectKind.RegularFile
                        || namedAfterOpen.HardLinkCount != 1
                        || !FileHandleIdentity.IdentitiesMatch(
                            opened.Identity,
                            namedAfterOpen.Identity))))
            {

                return UnsafeFinding();

            }

            // The file exists but nothing holds it. That is not a fault: the lock is advisory and a
            // leftover file never wedges recovery, by design.
            return new DoctorFinding(
                DoctorOutcome.Healthy,
                "A maintenance lock file exists but no process holds it; it will be reused as-is.");

        }
        catch (IOException exception)
        {

            return ArcanumMaintenanceLock.IsVerifiedSharingViolation(exception)
                ? new DoctorFinding(
                    DoctorOutcome.Degraded,
                    "Another process holds the installation maintenance lock. A running host, an "
                    + "in-flight 'arcanum backup restore', or an installation reset will hold it; "
                    + "wait for it to finish before "
                    + "changing installation state.")
                : UnsafeFinding();

        }
        catch (UnauthorizedAccessException)
        {

            return UnsafeFinding();

        }

    }

    private static DoctorFinding UnsafeFinding() =>
        new(
            DoctorOutcome.Degraded,
            "The maintenance lock topology, identity, or owner-only permissions could not be inspected safely. Check the Arcanum installation path and its permissions.");

}

/// <summary>
/// Free space on the volume holding the installation. Local-first storage fails in ugly ways when
/// the disk fills — a partially written encrypted blob, a SQLite write that cannot checkpoint — so
/// this is reported before those symptoms appear rather than after.
/// </summary>
public sealed class DiskSpaceCheck : IDoctorCheck
{

    public const string CheckId = "runtime.disk_space";

    /// <summary>Below this, SQLite checkpoints and blob writes start failing in practice.</summary>
    internal const long UnhealthyBytes = 256L * 1024 * 1024;

    internal const long DegradedBytes = 2L * 1024 * 1024 * 1024;

    public string Id => CheckId;

    public string Name => "Disk space";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Runtime;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Inspect(ArcanumPaths.GrimoireDirectory));

    internal static DoctorFinding Inspect(string directory)
    {

        long available;

        try
        {

            available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory)
                .AvailableFreeSpace;

        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {

            return new DoctorFinding(
                DoctorOutcome.Unavailable,
                "Free space on the Arcanum volume could not be measured.");

        }

        string readable = $"{available / (1024.0 * 1024 * 1024):F1} GiB";

        if (available < UnhealthyBytes)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                $"Only {readable} free on the Arcanum volume. Writes to the Grimoire and to encrypted "
                + "blob storage will start failing.",
                [FreeSpaceRemedy]);

        }

        if (available < DegradedBytes)
        {

            return new DoctorFinding(
                DoctorOutcome.Degraded,
                $"{readable} free on the Arcanum volume, which leaves little headroom for a backup or a restore.",
                [FreeSpaceRemedy]);

        }

        return new DoctorFinding(DoctorOutcome.Healthy, $"{readable} free on the Arcanum volume.");

    }

    private static DoctorRemedy FreeSpaceRemedy =>
        new(
            "runtime.free_disk_space",
            null,
            DoctorRemedyCommands.DataPrunePreview,
            "Review what retention would reclaim, then free space on the volume holding the Arcanum installation.");

}
