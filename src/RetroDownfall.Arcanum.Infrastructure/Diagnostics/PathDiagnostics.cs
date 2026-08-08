using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// The managed directories Arcanum owns under its installation root. Kept in one place because the
/// detector and its repair must agree exactly: a repair that created a directory the detector does
/// not watch would never converge, and one that watched a directory it cannot create would report a
/// fault with no remedy.
/// </summary>
public static class ManagedDirectories
{

    /// <summary>Directories Arcanum creates on demand and can safely re-create when missing.</summary>
    public static IReadOnlyList<string> All =>
        [
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.SecretStoreDirectory,
            ArcanumPaths.LogDirectory,
            ArcanumPaths.FilesDirectory,
            ArcanumPaths.AttachmentsDirectory,
            ArcanumPaths.GlobalSpellsDirectory,
        ];

    internal static IReadOnlyList<string> Missing() =>
        [.. All.Distinct(StringComparer.Ordinal).Where(static path => !Directory.Exists(path))];

}

/// <summary>
/// Reports which managed directories are absent. Absence is <see cref="DoctorOutcome.Degraded"/>
/// rather than <see cref="DoctorOutcome.Unhealthy"/>: Arcanum creates them lazily, so a fresh
/// installation is legitimately missing most of them, and the finding exists to explain a later
/// permission or write failure rather than to raise an alarm on first run.
/// </summary>
public sealed class ManagedDirectoriesCheck : IDoctorCheck
{

    public const string CheckId = "paths.managed_directories";

    public string Id => CheckId;

    public string Name => "Managed directories";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Paths;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<string> missing = ManagedDirectories.Missing();

        if (missing.Count == 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                $"All {ManagedDirectories.All.Distinct(StringComparer.Ordinal).Count()} managed directories are present."));

        }

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Degraded,
            $"{missing.Count} managed directory/directories are missing: "
            + string.Join(", ", missing.Select(static path => Path.GetFileName(path))),
            [
                new DoctorRemedy(
                    "paths.recreate_managed_directories",
                    ManagedDirectoriesRepair.RepairId,
                    DoctorRemedyCommands.RepairManagedDirectories,
                    "Re-create the missing directories with owner-only permissions. No file is written or removed."),
            ]));

    }

}

/// <summary>
/// Creates the missing managed directories with owner-only permissions. Creation-only and
/// idempotent: it never deletes, never moves, and never touches a directory that already exists, so
/// the second run of a successful repair finds nothing missing.
/// </summary>
public sealed class ManagedDirectoriesRepair : IDoctorRepair
{

    public const string RepairId = "paths.create_managed_directories";

    public string Id => RepairId;

    public DoctorSubsystem Subsystem => DoctorSubsystem.Paths;

    public IReadOnlyList<string> DetectorIds => [ManagedDirectoriesCheck.CheckId];

    public string Description =>
        "Create the managed Arcanum directories that are missing, with owner-only permissions.";

    public Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<string> missing = ManagedDirectories.Missing();

        return Task.FromResult(missing.Count == 0
            ? Converged()
            : new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Planned,
                $"Would create {missing.Count} missing directory/directories. Re-run with --apply to perform it.",
                [.. missing.Select(static path => new DoctorRepairStep(path, "missing", "created, owner-only"))],
                null));

    }

    public Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<string> missing = ManagedDirectories.Missing();

        if (missing.Count == 0)
        {

            return Task.FromResult(Converged());

        }

        List<DoctorRepairStep> steps = [];

        List<string> failures = [];

        foreach (string path in missing)
        {

            try
            {

                SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

                steps.Add(new DoctorRepairStep(path, "missing", "created, owner-only"));

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                failures.Add(Path.GetFileName(path));

                steps.Add(new DoctorRepairStep(path, "missing", "could not be created"));

            }

        }

        return Task.FromResult(failures.Count == 0
            ? new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Applied,
                $"Created {steps.Count} directory/directories.",
                steps,
                null)
            : new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Failed,
                $"{failures.Count} directory/directories could not be created.",
                steps,
                "The filesystem refused the change. Check ownership of the Arcanum installation root."));

    }

    private static DoctorRepairResult Converged() =>
        new(
            RepairId,
            DoctorRepairState.AlreadyConverged,
            "Every managed directory already exists; nothing to do.",
            [],
            null);

}
