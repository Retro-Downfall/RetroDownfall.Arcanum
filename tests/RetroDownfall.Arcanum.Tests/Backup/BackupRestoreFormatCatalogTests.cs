using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupRestoreFormatCatalogTests
{

    [Fact]
    public void Every_supported_format_is_named_with_its_arcanum_and_schema_expectations()
    {

        Assert.NotEmpty(BackupRestoreFormatCatalog.Supported);

        Assert.All(
            BackupRestoreFormatCatalog.Supported,
            static entry =>
            {

                Assert.InRange(entry.FormatVersion, 1, BackupArchiveFormat.CurrentVersion);

                Assert.False(string.IsNullOrWhiteSpace(entry.MinimumArcanumVersion));

                Assert.False(string.IsNullOrWhiteSpace(entry.SchemaAuthority));

                Assert.False(string.IsNullOrWhiteSpace(entry.Notes));

            });

        Assert.Equal(
            BackupRestoreFormatCatalog.Supported.Select(static entry => entry.FormatVersion).Distinct().Count(),
            BackupRestoreFormatCatalog.Supported.Count);

    }

    [Fact]
    public void Current_write_format_is_supported_for_restore()
    {

        Assert.True(
            BackupRestoreFormatCatalog.IsSupported(BackupArchiveFormat.CurrentVersion));

        Assert.Null(
            BackupRestoreFormatCatalog.Classify(BackupArchiveFormat.CurrentVersion));

    }

    [Fact]
    public void Newer_unknown_format_is_rejected_with_upgrade_guidance()
    {

        BackupVerifyIssue issue = Assert.IsType<BackupVerifyIssue>(
            BackupRestoreFormatCatalog.Classify(BackupArchiveFormat.CurrentVersion + 1));

        Assert.Equal("backup.restore_format_newer", issue.Code);

        Assert.Contains("upgrade", issue.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(
            BackupRestoreFormatCatalog.IsSupported(BackupArchiveFormat.CurrentVersion + 1));

    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_format_is_rejected_as_unsupported_rather_than_newer(int formatVersion)
    {

        BackupVerifyIssue issue = Assert.IsType<BackupVerifyIssue>(
            BackupRestoreFormatCatalog.Classify(formatVersion));

        Assert.Equal("backup.restore_format_unsupported", issue.Code);

        Assert.False(BackupRestoreFormatCatalog.IsSupported(formatVersion));

    }

}
