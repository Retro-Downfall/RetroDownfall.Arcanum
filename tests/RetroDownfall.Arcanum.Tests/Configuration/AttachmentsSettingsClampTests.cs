using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class AttachmentsSettingsClampTests
{

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(100, 32)]
    public void AttachmentsMaxReferencesPerTurn_clamps_to_1_through_32(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    public void AttachmentsMaxVersionsPerLogicalKey_clamps_to_1_through_100(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsMaxVersionsPerLogicalKey(value));

    }

    [Theory]
    [InlineData(0L, 1024L * 1024L)]
    [InlineData(1024L * 1024L, 1024L * 1024L)]
    [InlineData(256L * 1024L * 1024L, 256L * 1024L * 1024L)]
    [InlineData(10L * 1024L * 1024L * 1024L, 10L * 1024L * 1024L * 1024L)]
    [InlineData(20L * 1024L * 1024L * 1024L, 10L * 1024L * 1024L * 1024L)]
    public void AttachmentsMaxBytesPerSession_clamps_to_1_MiB_through_10_GiB(long value, long expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsMaxBytesPerSession(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(24, 24)]
    [InlineData(168, 168)]
    [InlineData(200, 168)]
    public void AttachmentsPendingRetentionHours_clamps_to_1_through_168(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsPendingRetentionHours(value));

    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(40, 40)]
    [InlineData(200, 200)]
    [InlineData(500, 200)]
    public void AttachmentsMaxIndexItemsInPrompt_clamps_to_1_through_200(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(value));

    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(256, 256)]
    [InlineData(4096, 4096)]
    [InlineData(64_000, 64_000)]
    [InlineData(100_000, 64_000)]
    public void AttachmentsMaxIndexBytesInPrompt_clamps_to_256_through_64000(int value, int expected)
    {

        Assert.Equal(expected, ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt(value));

    }

    [Fact]
    public void AttachmentsSettings_has_expected_defaults()
    {

        AttachmentsSettings settings = ArcanumRuntimeDefaults.Attachments;

        Assert.True(settings.Enabled);

        Assert.Equal(8, settings.MaxReferencesPerTurn);

        Assert.Equal(20, settings.MaxVersionsPerLogicalKey);

        Assert.Equal(256L * 1024L * 1024L, settings.MaxBytesPerSession);

        Assert.Equal(24, settings.PendingRetentionHours);

        Assert.Equal(40, settings.MaxIndexItemsInPrompt);

        Assert.Equal(4_096, settings.MaxIndexBytesInPrompt);

        Assert.True(settings.EnableModelAttachTool);

    }

    [Fact]
    public void ArcanumSettings_projects_attachment_feature()
    {
        ArcanumSettings settings = new();
        AttachmentsSettings attachments = settings.ResolveAttachments();

        Assert.True(attachments.Enabled);

    }

}
