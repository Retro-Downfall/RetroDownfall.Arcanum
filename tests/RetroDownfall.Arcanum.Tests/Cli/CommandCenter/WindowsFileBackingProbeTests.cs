using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The reparse-tag vocabulary the Windows lane's real-volume attachment case reports and asserts with, and the
/// fact that case leans on: product code does not read a reparse tag. Pure, so it runs on every host.
/// </summary>
public sealed class WindowsFileBackingProbeTests
{
    /// <summary>
    /// The real-volume case fails when a tag the volume shows is a name surrogate, so the rule has to classify
    /// the tags a file can carry without being a link as not one, and the two link tags as one.
    /// </summary>
    [Theory]
    [InlineData(WindowsFileBackingProbe.ReparseTagWof, false)]
    [InlineData(WindowsFileBackingProbe.ReparseTagDedup, false)]
    [InlineData(WindowsFileBackingProbe.ReparseTagCloud, false)]
    [InlineData(WindowsFileBackingProbe.ReparseTagHsm, false)]
    [InlineData(WindowsFileBackingProbe.ReparseTagSymbolicLink, true)]
    [InlineData(WindowsFileBackingProbe.ReparseTagMountPoint, true)]
    [InlineData(0u, false)]
    public void Only_a_tag_that_names_another_location_is_a_name_surrogate(uint tag, bool expected)
    {
        Assert.Equal(expected, WindowsFileBackingProbe.IsNameSurrogate(tag));
    }

    [Theory]
    [InlineData(WindowsFileBackingProbe.ReparseTagWof, "0x80000017 (IO_REPARSE_TAG_WOF)")]
    [InlineData(WindowsFileBackingProbe.ReparseTagMountPoint, "0xA0000003 (IO_REPARSE_TAG_MOUNT_POINT)")]
    [InlineData(0x1234u, "0x00001234")]
    public void A_tag_is_described_by_its_documented_name_when_it_has_one(uint tag, string expected)
    {
        Assert.Equal(expected, WindowsFileBackingProbe.DescribeTag(tag));
    }

    /// <summary>
    /// The attachment gate decides from the attribute word and from whether the framework reports a link
    /// target, never from a reparse tag, and the host-independent cases of
    /// <see cref="CommandCenterAttachmentFileKindTests"/> pin exactly that. If product code starts to read a
    /// tag, those cases no longer describe the decision, so this fails until they and the attachment paragraph
    /// of <c>docs/Arcanum.DESIGN.md</c> are brought in line.
    /// </summary>
    [Fact]
    public void Product_code_does_not_read_a_reparse_tag()
    {
        string[] tagReaders =
        [
            "IO_REPARSE_TAG",
            "ReparseTag",
            "FSCTL_GET_REPARSE_POINT",
            "FileAttributeTagInfo",
            "FILE_ATTRIBUTE_TAG_INFO",
            "IsReparseTagNameSurrogate",
        ];

        string[] offenders = ProductionSourceInventory.Sources()
            .SelectMany(source => tagReaders
                .Where(reader => source.Text.Contains(reader, StringComparison.Ordinal))
                .Select(reader => $"{source.RelativePath} names {reader}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Product code reads a reparse tag, so the attachment gate no longer decides from the attribute "
            + "word and the link target alone. Update CommandCenterAttachmentFileKindTests and the attachment "
            + "paragraph of docs/Arcanum.DESIGN.md together with this test:\n" + string.Join("\n", offenders));
    }
}
