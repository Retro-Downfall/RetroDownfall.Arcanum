using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class UnifiedDiffParserTests
{
    [Theory]
    [InlineData("104755")]
    [InlineData("102755")]
    [InlineData("101755")]
    [InlineData("107755")]
    [InlineData("100777")]
    [InlineData("100666")]
    [InlineData("100757")]
    [InlineData("100664")]
    [InlineData("100600")]
    [InlineData("100000")]
    public void New_file_mode_with_setuid_or_world_write_is_rejected(string mode)
    {
        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            CreatePatch(mode),
            new WorkspacePatchSettings());

        Assert.False(result.Success);

        Assert.Equal("unsupported_metadata", result.Code);

        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData("100644", (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead))]
    [InlineData(
        "100755",
        (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute))]
    public void New_file_mode_of_644_or_755_is_accepted(string mode, int expectedUnixMode)
    {
        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            CreatePatch(mode),
            new WorkspacePatchSettings());

        Assert.True(result.Success, result.Message);

        Assert.Equal(
            (UnixFileMode)expectedUnixMode,
            Assert.Single(result.Manifest!.Files).NewFileUnixMode);
    }

    [Fact]
    public void New_file_without_a_mode_header_is_still_accepted_and_carries_no_mode()
    {
        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            """
            --- /dev/null
            +++ b/plain.txt
            @@ -0,0 +1 @@
            +hello
            """,
            new WorkspacePatchSettings());

        Assert.True(result.Success, result.Message);

        Assert.Null(Assert.Single(result.Manifest!.Files).NewFileUnixMode);
    }

    private static string CreatePatch(string mode) =>
        $"""
         diff --git a/created.sh b/created.sh
         new file mode {mode}
         --- /dev/null
         +++ b/created.sh
         @@ -0,0 +1 @@
         +hello
         """;
}
