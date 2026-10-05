using System.Globalization;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class DaemonManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-daemon-manager-" + Guid.NewGuid().ToString("N"));

    public DaemonManagerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(3, "Boot-out failed: 3: No such process")]
    [InlineData(5, "Boot-out failed: 5: Input/output error")]
    [InlineData(36, "")]
    [InlineData(113, "Could not find specified service")]
    [InlineData(1, "Boot-out failed: No such process")]
    public async Task MacOs_uninstall_deletes_plist_when_bootout_reports_service_not_found(int exitCode, string stderr)
    {
        string plist = Path.Combine(_directory, "com.retrodownfall.arcanum.plist");

        await File.WriteAllTextAsync(plist, "<plist/>");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] == "bootout"
                ? ScriptedDaemonProcessRunner.Exit(exitCode, stderr: stderr)
                : ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"));

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(File.Exists(plist));

        Assert.Contains(runner.Calls, static call => call.Contains("bootout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MacOs_uninstall_keeps_the_plist_when_bootout_fails_for_another_reason()
    {
        string plist = Path.Combine(_directory, "com.retrodownfall.arcanum.plist");

        await File.WriteAllTextAsync(plist, "<plist/>");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] == "bootout"
                ? ScriptedDaemonProcessRunner.Exit(1, stderr: "Operation not permitted")
                : ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"));

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonBootout", result.Error.Code);

        Assert.True(File.Exists(plist));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(113)]
    public async Task MacOs_install_boots_out_the_label_before_bootstrapping(int bootoutExitCode)
    {
        string plist = Path.Combine(_directory, "LaunchAgents", "com.retrodownfall.arcanum.plist");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] switch
            {
                "bootout" => ScriptedDaemonProcessRunner.Exit(
                    bootoutExitCode,
                    stderr: bootoutExitCode == 0 ? string.Empty : "Could not find specified service"),
                "bootstrap" => ScriptedDaemonProcessRunner.Exit(0),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.InstallAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        int bootout = runner.Calls.FindIndex(static call => call.Contains("bootout", StringComparison.Ordinal));

        int bootstrap = runner.Calls.FindIndex(static call => call.Contains("bootstrap", StringComparison.Ordinal));

        Assert.True(bootout >= 0, "A loaded label must be booted out before bootstrap.");

        Assert.True(bootout < bootstrap, string.Join(" | ", runner.Calls));

        Assert.True(File.Exists(plist));
    }

    [Fact]
    public async Task MacOs_install_fails_when_the_pre_bootstrap_bootout_fails_for_another_reason()
    {
        string plist = Path.Combine(_directory, "LaunchAgents", "com.retrodownfall.arcanum.plist");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] switch
            {
                "bootout" => ScriptedDaemonProcessRunner.Exit(1, stderr: "Operation not permitted"),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.InstallAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonBootout", result.Error.Code);

        Assert.DoesNotContain(runner.Calls, static call => call.Contains("bootstrap", StringComparison.Ordinal));
    }

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
