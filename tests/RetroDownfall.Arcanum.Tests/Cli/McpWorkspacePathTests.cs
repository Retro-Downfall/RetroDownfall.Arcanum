using RetroDownfall.Arcanum.Cli.Commands.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// <c>mcp --workspace</c> compares a path the operator typed with the working directory a server was
/// configured in. The comparison is on the canonical spelling, so a relative spelling, a dot segment, a
/// doubled or trailing separator, and (for a Windows path) the other separator or another letter case do
/// not make the same location look like two.
/// </summary>
public sealed class McpWorkspacePathTests
{
    [Theory]
    [InlineData("/srv/proj/", "/work", "/srv/proj")]
    [InlineData("/srv//proj/./sub/..", "/work", "/srv/proj")]
    [InlineData("./proj", "/work", "/work/proj")]
    [InlineData("../other/./proj//", "/work/a", "/work/other/proj")]
    [InlineData("sub/proj", "/work", "/work/sub/proj")]
    [InlineData("/", "/work", "/")]
    [InlineData("/../..", "/work", "/")]
    public void A_posix_path_is_absolute_dot_free_and_has_no_trailing_separator(string path, string currentDirectory, string expected) =>
        Assert.Equal(expected, McpWorkspacePath.Canonicalise(path, currentDirectory, windowsHost: false));

    [Fact]
    public void A_posix_path_keeps_its_case_and_its_backslashes()
    {
        Assert.False(McpWorkspacePath.Same("/srv/Proj", "/srv/proj", "/work", windowsHost: false));

        Assert.Equal("/srv/a\\b", McpWorkspacePath.Canonicalise("/srv/a\\b/", "/work", windowsHost: false));
    }

    /// <summary>
    /// A server-owned Windows path is read in Windows terms on every platform, so the same location in
    /// either separator and any case is one location. These are the cases a Windows operator hits.
    /// </summary>
    [Theory]
    [InlineData(@"C:/srv\proj\", "/work", @"C:\srv\proj")]
    [InlineData(@"C:\srv\.\proj\sub\..", "/work", @"C:\srv\proj")]
    [InlineData(@"C:\", "/work", @"C:\")]
    [InlineData(@"\\server\share\a\..\b", "/work", @"\\server\share\b")]
    [InlineData(@"\\server\share\..\..\x", "/work", @"\\server\share\x")]
    public void A_windows_spelled_path_is_canonicalised_on_any_host(string path, string currentDirectory, string expected) =>
        Assert.Equal(expected, McpWorkspacePath.Canonicalise(path, currentDirectory, windowsHost: false));

    /// <summary>
    /// A root spelled shorter than its canonical form (a bare drive, a drive-relative path, a UNC share with
    /// no trailing separator, doubled separators inside a UNC prefix) must neither throw nor lose a
    /// character: the host stores a server's working directory as configured, and every <c>mcp</c>
    /// listing filtered by <c>--workspace</c> canonicalises it.
    /// </summary>
    [Theory]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"D:proj", @"D:\proj")]
    [InlineData(@"D:proj\sub", @"D:\proj\sub")]
    [InlineData(@"\\server", @"\\server\")]
    [InlineData(@"\\server\share", @"\\server\share\")]
    [InlineData(@"\\server\\share\proj", @"\\server\share\proj")]
    [InlineData(@"\\\server\share\proj", @"\\server\share\proj")]
    public void A_root_spelled_shorter_than_its_canonical_form_is_canonicalised_without_losing_a_character(
        string path,
        string expected)
    {
        Assert.Equal(expected, McpWorkspacePath.Canonicalise(path, "/work", windowsHost: false));

        Assert.True(McpWorkspacePath.Same(path, expected, "/work", windowsHost: false));
    }

    [Theory]
    [InlineData(@"C:\Srv\Proj", "c:/srv/proj/")]
    [InlineData(@"c:\srv\proj", @"C:\SRV\PROJ\")]
    [InlineData(@"\\Server\Share\Proj", @"\\server\SHARE\proj")]
    public void A_windows_spelled_path_matches_whatever_the_separator_or_case(string left, string right)
    {
        Assert.True(McpWorkspacePath.Same(left, right, "/work", windowsHost: false));

        Assert.True(McpWorkspacePath.Same(right, left, "/work", windowsHost: false));
    }

    [Fact]
    public void Different_windows_locations_do_not_match()
    {
        Assert.False(McpWorkspacePath.Same(@"C:\srv\proj", @"D:\srv\proj", "/work", windowsHost: false));

        Assert.False(McpWorkspacePath.Same(@"C:\srv\proj", @"C:\srv\proj2", "/work", windowsHost: false));

        Assert.False(McpWorkspacePath.Same(@"\\server\share\proj", @"\\server\other\proj", "/work", windowsHost: false));
    }

    [Theory]
    [InlineData(@"proj\..\other", @"C:\work", @"C:\work\other")]
    [InlineData("sub/proj", @"C:\work\", @"C:\work\sub\proj")]
    [InlineData("/srv/x", @"C:\work", @"\srv\x")]
    public void On_a_windows_host_every_path_is_read_in_windows_terms(string path, string currentDirectory, string expected) =>
        Assert.Equal(expected, McpWorkspacePath.Canonicalise(path, currentDirectory, windowsHost: true));

    [Fact]
    public void On_a_windows_host_case_never_tells_two_locations_apart()
    {
        Assert.True(McpWorkspacePath.Same("/Srv/Proj", @"\srv\proj\", @"C:\work", windowsHost: true));
    }

    /// <summary>
    /// The helper agrees with the operating system's own resolution of a relative path, whichever
    /// platform this runs on: the check that matters on the Windows lane, where the separators, the drive
    /// and the current directory are real.
    /// </summary>
    [Fact]
    public void A_relative_path_resolves_the_way_the_operating_system_resolves_it()
    {
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine("..", "mcp-workspace-probe", "sub")));

        Assert.Equal(expected, McpWorkspacePath.Canonicalise(Path.Combine("..", "mcp-workspace-probe", "sub") + Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The Windows lane: letter case and the drive letter's case are not significant to the file system,
    /// so a path typed in another case is the same location. Not run on macOS, where case is significant;
    /// <see cref="A_windows_spelled_path_matches_whatever_the_separator_or_case"/> pins the same rule there.
    /// </summary>
    [SkippableFact]
    public void On_Windows_a_real_path_in_another_case_and_separator_is_the_same_location()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows paths are case-insensitive and take either separator.");

        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        Assert.True(McpWorkspacePath.Same(directory, directory.ToUpperInvariant().Replace('\\', '/') + "/"));

        Assert.True(McpWorkspacePath.Same(directory.ToLowerInvariant(), directory + @"\.\"));
    }
}
