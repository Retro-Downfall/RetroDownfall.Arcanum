using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Tests.Process;

public sealed class WindowsAppContainerPolicyTests
{
    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\workspace")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\workspace:file")]
    [InlineData(@"C:\workspace\..\outside")]
    public void Rejects_unsafe_or_noncanonical_roots(string path)
    {
        Assert.False(WindowsAppContainerPolicy.IsSafeRoot(path));
    }

    [Theory]
    [InlineData(@"C:\workspace")]
    [InlineData(@"D:\spell\scripts")]
    public void Accepts_canonical_local_drive_roots(string path)
    {
        Assert.True(WindowsAppContainerPolicy.IsSafeRoot(path));
    }

    [Fact]
    public void Profile_name_is_bounded_and_contains_no_sensitive_path()
    {
        string name = WindowsAppContainerPolicy.CreateProfileName();

        Assert.StartsWith("RetroDownfall.Arcanum.Tool.", name, StringComparison.Ordinal);
        Assert.True(name.Length <= 64);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Broker_command_line_preserves_spaces_quotes_and_trailing_backslashes()
    {
        string commandLine = WindowsAppContainerLauncher.BuildCommandLine(
            @"C:\Program Files\tool.exe",
            [@"plain", @"two words", "say\"hello", @"C:\trailing\"]);

        Assert.Equal(
            "\"C:\\Program Files\\tool.exe\" plain \"two words\" \"say\\\"hello\" C:\\trailing\\",
            commandLine);
    }
}
