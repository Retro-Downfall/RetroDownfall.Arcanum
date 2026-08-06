using System.Diagnostics.CodeAnalysis;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal enum BackupRestorePlacement
{

    /// <summary>The entry becomes a file beneath the restored Grimoire root.</summary>
    Install = 0,

    /// <summary>The entry is secret material consumed by re-wrapping, not a restored file.</summary>
    SecretMaterial = 1,

    /// <summary>
    /// The entry is deliberately not installed, and the reason is reported. Never a silent skip.
    /// </summary>
    WithheldByPolicy = 2,

}

internal sealed record BackupRestorePlacementDecision(
    BackupRestorePlacement Placement,
    string? RelativeDestination,
    string? Reason);

/// <summary>
/// Translates the archive's component-oriented layout back into the installation layout.
/// </summary>
/// <remarks>
/// The archive is not a filesystem image — <c>authored/CODEX.md</c> came from the Grimoire root and
/// <c>compendium/certificates/…</c> came from <c>certs/</c>. The mapping is therefore explicit and
/// closed: an entry this table does not recognize is refused rather than dropped, because a full
/// restore that quietly skips a file is a corrupt restore that reports success.
/// </remarks>
internal static class BackupRestoreLayout
{

    /// <summary>
    /// Machine-local state that belongs to the destination, not to the archive: the Data Protection
    /// key ring that makes local secret wrapping possible at all, and the backup archives themselves
    /// — including the one being restored from.
    /// </summary>
    internal static readonly string[] PreservedFromCurrentInstallation =
    [
        "keys",
        "backups",
    ];

    public static bool TryResolve(
        string archivePath,
        [NotNullWhen(true)] out BackupRestorePlacementDecision? decision)
    {

        decision = null;

        if (string.IsNullOrWhiteSpace(archivePath))
        {

            return false;

        }

        if (IsExactly(archivePath, BackupArchivePaths.PortableRecoveryKeys)
            || IsExactly(archivePath, BackupArchivePaths.MasterApiKey))
        {

            decision = new BackupRestorePlacementDecision(
                BackupRestorePlacement.SecretMaterial,
                RelativeDestination: null,
                Reason: null);

            return true;

        }

        if (archivePath.StartsWith("mcp/", StringComparison.Ordinal))
        {

            decision = new BackupRestorePlacementDecision(
                BackupRestorePlacement.WithheldByPolicy,
                RelativeDestination: null,
                "Trusted MCP workspace metadata is machine-specific authorization and is never "
                + "inherited by a restore. Re-approve each workspace on this machine.");

            return true;

        }

        string? relative =
            Strip(archivePath, "grimoire/")
            ?? Strip(archivePath, "configuration/")
            ?? Rewrite(archivePath, "authored/spells/", "spells/")
            ?? Strip(archivePath, "authored/")
            ?? Rewrite(archivePath, "compendium/certificates/", "certs/")
            ?? Strip(archivePath, "cli/")
            ?? Strip(archivePath, "forge/")
            ?? Strip(archivePath, "logs/")
            ?? Keep(archivePath, "attachments/")
            ?? Keep(archivePath, "files/");

        if (relative is null || !IsSafeRelative(relative))
        {

            return false;

        }

        decision = new BackupRestorePlacementDecision(
            BackupRestorePlacement.Install,
            relative,
            Reason: null);

        return true;

    }

    private static string? Strip(string archivePath, string prefix) =>
        archivePath.StartsWith(prefix, StringComparison.Ordinal)
            ? archivePath[prefix.Length..]
            : null;

    private static string? Rewrite(string archivePath, string prefix, string replacement) =>
        archivePath.StartsWith(prefix, StringComparison.Ordinal)
            ? replacement + archivePath[prefix.Length..]
            : null;

    private static string? Keep(string archivePath, string prefix) =>
        archivePath.StartsWith(prefix, StringComparison.Ordinal)
            ? archivePath
            : null;

    private static bool IsSafeRelative(string relative)
    {

        if (relative.Length == 0)
        {

            return false;

        }

        foreach (string segment in relative.Split('/'))
        {

            if (segment.Length == 0
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal))
            {

                return false;

            }

        }

        return true;

    }

    private static bool IsExactly(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

}
