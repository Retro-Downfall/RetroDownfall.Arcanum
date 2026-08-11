using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupRestoreCapacityPlannerTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-capacity-" + Guid.NewGuid().ToString("N"));

    public BackupRestoreCapacityPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Required_bytes_cover_both_concurrent_copies_the_safety_backup_and_headroom()
    {

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            _root,
            restoredBytes: 1_000,
            displacedBytes: 400,
            availableBytesOverride: long.MaxValue);

        Assert.Equal(
            (1_000 * 2) + 400 + BackupRestoreCapacityPlanner.HeadroomBytes,
            capacity.RequiredBytes);

        Assert.Null(capacity.Issue);

    }

    /// <summary>
    /// A restore materializes the archive twice at once — the extraction tree and the staged tree —
    /// so a volume with room for exactly one copy must be refused before staging, not discovered to
    /// be full halfway through.
    /// </summary>
    [Fact]
    public void A_volume_holding_only_one_copy_of_the_archive_is_refused()
    {

        long restored = 40_000;

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            _root,
            restored,
            displacedBytes: 0,
            availableBytesOverride: restored + BackupRestoreCapacityPlanner.HeadroomBytes + 1_000);

        BackupVerifyIssue issue = Assert.IsType<BackupVerifyIssue>(capacity.Issue);

        Assert.Equal("backup.restore_insufficient_disk", issue.Code);

    }

    [Fact]
    public void Insufficient_free_space_is_a_typed_blocker_naming_both_numbers()
    {

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            _root,
            restoredBytes: 10_000,
            displacedBytes: 0,
            availableBytesOverride: 128);

        BackupVerifyIssue issue = Assert.IsType<BackupVerifyIssue>(capacity.Issue);

        Assert.Equal("backup.restore_insufficient_disk", issue.Code);

        Assert.Contains(capacity.RequiredBytes.ToString(), issue.Message, StringComparison.Ordinal);

        Assert.Contains("128", issue.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void Exactly_enough_free_space_is_accepted()
    {

        long restored = 2_048;

        long required = (restored * 2) + BackupRestoreCapacityPlanner.HeadroomBytes;

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            _root,
            restored,
            displacedBytes: 0,
            availableBytesOverride: required);

        Assert.Null(capacity.Issue);

        Assert.Equal(required, capacity.AvailableBytes);

    }

    [Fact]
    public void A_real_destination_reports_the_volume_free_space()
    {

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            _root,
            restoredBytes: 1,
            displacedBytes: 0,
            availableBytesOverride: null);

        Assert.True(capacity.AvailableBytes > 0);

    }

    [Fact]
    public void An_unknown_destination_volume_reports_unknown_capacity_without_blocking()
    {

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            Path.Combine(_root, "absent", "deeper"),
            restoredBytes: 1,
            displacedBytes: 0,
            availableBytesOverride: null);

        Assert.True(capacity.AvailableBytes >= 0);

    }

    [Fact]
    public void Measured_directory_bytes_sum_regular_files_recursively()
    {

        string nested = Path.Combine(_root, "a", "b");

        Directory.CreateDirectory(nested);

        File.WriteAllBytes(Path.Combine(_root, "one.bin"), new byte[10]);

        File.WriteAllBytes(Path.Combine(nested, "two.bin"), new byte[25]);

        Assert.Equal(35, BackupRestoreCapacityPlanner.MeasureDirectoryBytes(_root));

        Assert.Equal(
            0,
            BackupRestoreCapacityPlanner.MeasureDirectoryBytes(
                Path.Combine(_root, "absent")));

    }

}
