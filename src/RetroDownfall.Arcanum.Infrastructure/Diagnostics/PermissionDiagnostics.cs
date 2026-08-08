using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Reports whether the files that hold Arcanum's secrets are readable by anyone but their owner
/// (DESIGN §4.4.2, §11.13.1). Read-only: it never creates a path, never widens one, and never opens
/// a file it inspects — the mode is all it needs, and a diagnostic that read the bytes would be one
/// bug away from printing them.
/// </summary>
public sealed class PermissionPostureCheck(IOptions<ArcanumSettings> options) : IDoctorCheck
{

    public const string CheckId = "permissions.posture";

    public string Id => CheckId;

    public string Name => "Permissions";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Permissions;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<SensitivePathPosture> posture =
            SecureFilePermissions.InspectOwnerOnlyPosture(
                PermissionInventory.ConfiguredProviderNames(options.Value));

        List<SensitivePathPosture> exposed =
            [.. posture.Where(static candidate => candidate.Exists && !candidate.IsOwnerOnly)];

        int inspected = posture.Count(static candidate => candidate.Exists);

        if (exposed.Count == 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                $"{inspected} sensitive path(s) present and restricted to the current user."));

        }

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Degraded,
            $"{exposed.Count} of {inspected} sensitive path(s) are reachable by another principal: "
            + string.Join(", ", exposed.Select(static candidate => Describe(candidate))),
            [
                new DoctorRemedy(
                    "permissions.restrict_to_owner",
                    PermissionApplyOwnerOnlyRepair.RepairId,
                    DoctorRemedyCommands.RepairPermissions,
                    "Restrict configuration, the Grimoire database, and every secret mirror to the current user."),
            ]));

    }

    /// <summary>
    /// The leaf name only, with a trailing separator for a directory. A finding an operator may paste
    /// into an issue has no business carrying their home-directory path.
    /// </summary>
    private static string Describe(SensitivePathPosture posture) =>
        Path.GetFileName(posture.Path.TrimEnd(Path.DirectorySeparatorChar))
        + (posture.IsDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty);

}

/// <summary>
/// Sets the owner-only mode on every inventoried path whose posture differs. Idempotent by
/// construction: the target is an absolute mode rather than a delta, so a second run finds nothing
/// to change and reports <see cref="DoctorRepairState.AlreadyConverged"/>. It only ever narrows
/// access, and it never creates a path that does not already exist.
/// </summary>
public sealed class PermissionApplyOwnerOnlyRepair(IOptions<ArcanumSettings> options) : IDoctorRepair
{

    public const string RepairId = "permissions.apply_owner_only";

    public string Id => RepairId;

    public DoctorSubsystem Subsystem => DoctorSubsystem.Permissions;

    public IReadOnlyList<string> DetectorIds => [PermissionPostureCheck.CheckId];

    public string Description =>
        "Restrict configuration, preset sidecars, the Grimoire database, and every secret mirror to the current user.";

    public Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Plan(Exposed()));

    public Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<SensitivePathPosture> before = Exposed();

        if (before.Count == 0)
        {

            return Task.FromResult(new DoctorRepairResult(
                RepairId,
                DoctorRepairState.AlreadyConverged,
                "Every sensitive path was already restricted to the current user.",
                [],
                null));

        }

        // Deliberately not SecureFilePermissions.ApplyOwnerOnlyToSensitivePaths(): that method
        // creates the Grimoire directory as a side effect, which would make this repair silently
        // change the answer of paths.managed_directories and report a change it never listed. This
        // repair only ever narrows paths its own plan named.
        foreach (SensitivePathPosture posture in before)
        {

            if (posture.IsDirectory)
            {

                SecureFilePermissions.ApplyOwnerOnlyDirectory(posture.Path);

                continue;

            }

            SecureFilePermissions.ApplyOwnerOnlyFile(posture.Path);

        }

        IReadOnlyList<SensitivePathPosture> after = Exposed();

        List<DoctorRepairStep> steps =
        [
            .. before.Select(posture => new DoctorRepairStep(
                posture.Path,
                "reachable by another principal",
                after.Any(remaining => remaining.Path == posture.Path)
                    ? "still reachable — the filesystem refused the change"
                    : "owner-only")),
        ];

        return Task.FromResult(after.Count == 0
            ? new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Applied,
                $"Restricted {steps.Count} path(s) to the current user.",
                steps,
                null)
            : new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Failed,
                $"{after.Count} path(s) could not be restricted.",
                steps,
                "The filesystem refused the permission change. Check ownership of the Arcanum directory."));

    }

    private DoctorRepairResult Plan(IReadOnlyList<SensitivePathPosture> exposed) =>
        exposed.Count == 0
            ? new DoctorRepairResult(
                RepairId,
                DoctorRepairState.AlreadyConverged,
                "Every sensitive path is already restricted to the current user; nothing to do.",
                [],
                null)
            : new DoctorRepairResult(
                RepairId,
                DoctorRepairState.Planned,
                $"Would restrict {exposed.Count} path(s) to the current user. Re-run with --apply to perform it.",
                [
                    .. exposed.Select(static posture => new DoctorRepairStep(
                        posture.Path,
                        "reachable by another principal",
                        "owner-only")),
                ],
                null);

    private IReadOnlyList<SensitivePathPosture> Exposed() =>
        [
            .. SecureFilePermissions
                .InspectOwnerOnlyPosture(PermissionInventory.ConfiguredProviderNames(options.Value))
                .Where(static candidate => candidate.Exists && !candidate.IsOwnerOnly),
        ];

}

internal static class PermissionInventory
{

    /// <summary>
    /// The configured inference-provider names, which decide which per-provider credential mirrors
    /// exist on this installation. Nothing here reads a credential value.
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredProviderNames(ArcanumSettings settings) =>
        [
            .. (settings.Providers ?? [])
                .Select(static provider => provider.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!),
        ];

}
