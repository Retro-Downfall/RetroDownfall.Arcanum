using System.Globalization;
using System.Reflection;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class DaemonManagerTests
{

    [Theory]
    [InlineData("        STATE              : 4  RUNNING", 4, true)]
    [InlineData("STATE : 1", 1, true)]
    [InlineData("STATE : 0", 0, true)]
    [InlineData("no state line here", 0, false)]
    [InlineData("STATE :", 0, false)]
    [InlineData("STATE : abc", 0, false)]
    public void TryParseServiceStateCode_ParsesExpectedStateCode(string stdout, int expectedCode, bool expectedResult)
    {

        MethodInfo? method = typeof(WindowsDaemonManager).GetMethod(
            "TryParseServiceStateCode",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        object?[] args = [stdout, 0];

        bool result = (bool)method.Invoke(null, args)!;

        Assert.Equal(expectedResult, result);

        Assert.Equal(expectedCode, args[1]);

    }

    [Theory]
    [InlineData("/usr/bin/arcanum", "/usr/bin/arcanum serve")]
    [InlineData("/path with spaces/arcanum", "\"/path with spaces/arcanum\" serve")]
    [InlineData("/path\\with\\backslash/arcanum", "\"/path\\\\with\\\\backslash/arcanum\" serve")]
    [InlineData("/path\"with\"quote/arcanum", "\"/path\\\"with\\\"quote/arcanum\" serve")]
    public void FormatExecStartArgument_FormatsExpectedValue(string executablePath, string expected)
    {

        MethodInfo? method = typeof(LinuxDaemonManager).GetMethod(
            "FormatExecStartArgument",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        string result = (string)method.Invoke(null, [executablePath])!;

        Assert.Equal(expected, result);

    }

    [Fact]
    public void FormatStateMessage_RunningState_ReturnsRunningMessage()
    {

        MethodInfo? method = typeof(WindowsDaemonManager).GetMethod(
            "FormatStateMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        string result = (string)method.Invoke(null, [4])!;

        Assert.Equal("ArcanumDaemon is running.", result);

    }

    [Fact]
    public void FormatStateMessage_UnexpectedState_ReturnsUnexpectedMessage()
    {

        MethodInfo? method = typeof(WindowsDaemonManager).GetMethod(
            "FormatStateMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        string result = (string)method.Invoke(null, [99])!;

        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"ArcanumDaemon reports an unexpected service state code {99}."),
            result);

    }

}
