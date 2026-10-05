using System.Globalization;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Hosting;
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
            (_, arguments) => arguments[0] switch
            {
                "bootout" => ScriptedDaemonProcessRunner.Exit(exitCode, stderr: stderr),
                "list" => ScriptedDaemonProcessRunner.Exit(113, stderr: "Could not find service"),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(File.Exists(plist));

        Assert.Contains(runner.Calls, static call => call.Contains("bootout", StringComparison.Ordinal));

        Assert.Contains(runner.Calls, static call => call.Contains("list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MacOs_uninstall_keeps_the_plist_when_bootout_says_not_loaded_but_the_agent_is_still_loaded()
    {
        string plist = Path.Combine(_directory, "com.retrodownfall.arcanum.plist");

        await File.WriteAllTextAsync(plist, "<plist/>");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] switch
            {
                "bootout" => ScriptedDaemonProcessRunner.Exit(5, stderr: "Boot-out failed: 5: Input/output error"),
                "list" => ScriptedDaemonProcessRunner.Exit(0, stdout: "-\t0\tcom.retrodownfall.arcanum\n"),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonBootout", result.Error.Code);

        Assert.Contains("still loaded", result.Error.Message, StringComparison.Ordinal);

        Assert.True(File.Exists(plist), "A loaded agent must not lose the plist that describes it.");
    }

    [Fact]
    public async Task MacOs_install_does_not_bootstrap_when_bootout_says_not_loaded_but_the_agent_is_still_loaded()
    {
        string plist = Path.Combine(_directory, "LaunchAgents", "com.retrodownfall.arcanum.plist");

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) => arguments[0] switch
            {
                "bootout" => ScriptedDaemonProcessRunner.Exit(5, stderr: "Boot-out failed: 5: Input/output error"),
                "list" => ScriptedDaemonProcessRunner.Exit(0, stdout: "-\t0\tcom.retrodownfall.arcanum\n"),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonBootout", result.Error.Code);

        Assert.DoesNotContain(runner.Calls, static call => call.Contains("bootstrap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MacOs_install_refuses_a_service_account_without_running_launchctl()
    {
        string plist = Path.Combine(_directory, "LaunchAgents", "com.retrodownfall.arcanum.plist");

        ScriptedDaemonProcessRunner runner = new(static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.InstallAsync(
            new DaemonInstallRequest(new DaemonServiceCredential("someone", "secret")),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountUnsupported", result.Error.Code);

        Assert.Empty(runner.Calls);

        Assert.False(File.Exists(plist));

        Assert.DoesNotContain("secret", result.Error.Message, StringComparison.Ordinal);
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
                "list" => ScriptedDaemonProcessRunner.Exit(113, stderr: "Could not find service"),
                "bootstrap" => ScriptedDaemonProcessRunner.Exit(0),
                _ => ScriptedDaemonProcessRunner.Exit(0, stdout: "501\n"),
            });

        MacOsDaemonManager manager = new(runner, plist);

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        int bootout = runner.Calls.FindIndex(static call => call.Contains("bootout", StringComparison.Ordinal));

        int bootstrap = runner.Calls.FindIndex(static call => call.Contains("bootstrap", StringComparison.Ordinal));

        Assert.True(bootout >= 0, "A loaded label must be booted out before bootstrap.");

        Assert.True(bootout < bootstrap, string.Join(" | ", runner.Calls));

        Assert.True(File.Exists(plist));
    }

    private const string ServicePassword = "S3cret pass \"word\"";

    private static readonly DaemonInstallRequest ServiceAccountRequest =
        new(new DaemonServiceCredential(@".\arcanum", ServicePassword));

    [Fact]
    public async Task Windows_install_without_an_account_refuses_and_directs_to_a_per_user_scheduled_task()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Assert.True(manager.RequiresServiceAccount);

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountRequired", result.Error.Code);

        Assert.Empty(runner.Calls);

        Assert.Contains("LocalSystem", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("Task Scheduler", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("schtasks /Create", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains(" serve", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_passes_an_explicit_service_account()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(2, runner.Invocations.Count);

        ScriptedInvocation create = runner.Invocations[0];

        Assert.Equal("sc.exe", create.FileName);

        // Each sc.exe option and its value are separate arguments, the way sc.exe reads "obj= <account>".
        Assert.Equal(
            [
                "create",
                "ArcanumDaemon",
                "binPath=",
                $"\"{(global::System.Environment.ProcessPath)}\" serve",
                "start=",
                "auto",
                "obj=",
                @".\arcanum",
                "password=",
                ServicePassword,
            ],
            create.Arguments);

        Assert.Equal(["start", "ArcanumDaemon"], runner.Invocations[1].Arguments);
    }

    [Theory]
    [InlineData("LocalSystem")]
    [InlineData("SYSTEM")]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    [InlineData(@".\LocalSystem")]
    [InlineData("  localsystem  ")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Windows_install_refuses_an_account_that_would_run_as_localsystem_or_is_empty(string account)
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(
            new DaemonInstallRequest(new DaemonServiceCredential(account, ServicePassword)),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountInvalid", result.Error.Code);

        Assert.Empty(runner.Calls);

        Assert.DoesNotContain(ServicePassword, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_refuses_an_empty_password_without_running_sc_exe()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(
            new DaemonInstallRequest(new DaemonServiceCredential(@".\arcanum", string.Empty)),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountInvalid", result.Error.Code);

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Windows_install_never_echoes_the_password_in_a_failure_message()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, arguments) => arguments[0] == "create"
                ? ScriptedDaemonProcessRunner.Exit(87, stderr: $"[SC] CreateService FAILED 87: bad value {ServicePassword}")
                : ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonScCreate", result.Error.Code);

        Assert.DoesNotContain(ServicePassword, result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("<redacted>", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(ServicePassword, ServiceAccountRequest.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_reports_an_existing_service_without_starting_or_deleting_it()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, arguments) => arguments[0] == "create"
                ? ScriptedDaemonProcessRunner.Exit(1073)
                : ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonScCreate", result.Error.Code);

        Assert.Contains("arcanum daemon uninstall", result.Error.Message, StringComparison.Ordinal);

        _ = Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Windows_install_reports_elevation_when_sc_create_is_denied()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => new DaemonProcessOutcome(-1, string.Empty, string.Empty, null, AccessDenied: true));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonElevationRequired", result.Error.Code);

        _ = Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Windows_install_removes_the_service_again_when_it_does_not_start()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, arguments) => arguments[0] == "start"
                ? ScriptedDaemonProcessRunner.Exit(1069, stderr: "[SC] StartService FAILED 1069")
                : ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        using CancellationTokenSource cancellation = new();

        Result result = await manager.InstallAsync(ServiceAccountRequest, cancellation.Token);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonScStart", result.Error.Code);

        Assert.Contains("log on as a service", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("removed again", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(
            ["create", "start", "delete"],
            runner.Invocations.Select(static call => call.Arguments[0]));

        Assert.Equal(["delete", "ArcanumDaemon"], runner.Invocations[2].Arguments);

        Assert.DoesNotContain(ServicePassword, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_says_so_when_the_service_that_did_not_start_cannot_be_removed()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, arguments) => arguments[0] switch
            {
                "start" => ScriptedDaemonProcessRunner.Exit(2, stderr: "[SC] StartService FAILED 2"),
                "delete" => ScriptedDaemonProcessRunner.Exit(5, stderr: "Access is denied"),
                _ => ScriptedDaemonProcessRunner.Exit(0),
            });

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonScStart", result.Error.Code);

        Assert.Contains("could not be removed", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("arcanum daemon uninstall", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_removes_the_service_on_an_uncancelled_token_when_cancelled_while_starting()
    {
        using CancellationTokenSource cancellation = new();

        ScriptedDaemonProcessRunner runner = new(
            (_, arguments) =>
            {
                if (arguments[0] == "start")
                {
                    cancellation.Cancel();

                    throw new OperationCanceledException(cancellation.Token);
                }

                return ScriptedDaemonProcessRunner.Exit(0);
            });

        WindowsDaemonManager manager = new(runner, "sc.exe");

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.InstallAsync(ServiceAccountRequest, cancellation.Token));

        Assert.Equal(
            ["create", "start", "delete"],
            runner.Invocations.Select(static call => call.Arguments[0]));

        Assert.False(
            runner.Invocations[2].Token.CanBeCanceled,
            "The rollback after a cancelled install must not be cancellable.");
    }

    [SkippableFact]
    public async Task Windows_real_runner_install_without_an_account_refuses_without_starting_sc_exe()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Exercises the real Windows process start path.");

        WindowsDaemonManager manager = new(
            DaemonProcessRunner.Default,
            Path.Combine(_directory, "missing-sc.exe"));

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountRequired", result.Error.Code);
    }

    [SkippableFact]
    public async Task Windows_real_runner_install_reports_a_missing_sc_exe_as_a_start_failure_not_an_elevation_problem()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Exercises the real Windows process start path.");

        WindowsDaemonManager manager = new(
            DaemonProcessRunner.Default,
            Path.Combine(_directory, "missing-sc.exe"));

        Result result = await manager.InstallAsync(ServiceAccountRequest, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonProcessRunner.StartErrorCode, result.Error.Code);

        Assert.DoesNotContain(ServicePassword, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_manager_reports_elevation_when_the_runner_reports_access_denied()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => new DaemonProcessOutcome(-1, string.Empty, string.Empty, null, AccessDenied: true));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonElevationRequired", result.Error.Code);

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Windows_uninstall_continues_to_delete_when_the_service_is_not_active()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, arguments) => arguments[0] == "stop"
                ? ScriptedDaemonProcessRunner.Exit(1062)
                : ScriptedDaemonProcessRunner.Exit(0));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(["sc.exe stop ArcanumDaemon", "sc.exe delete ArcanumDaemon"], runner.Calls);
    }

    [Fact]
    public async Task Windows_status_reports_a_missing_service_as_not_installed()
    {
        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => ScriptedDaemonProcessRunner.Exit(1060));

        WindowsDaemonManager manager = new(runner, "sc.exe");

        Result<string> result = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(WindowsDaemonManager.NotInstalledMessage, result.Value);
    }

    [SkippableFact]
    public async Task Windows_real_runner_reports_a_start_failure_for_a_missing_sc_exe_as_a_fatal_error()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Exercises the real Windows process start path.");

        WindowsDaemonManager manager = new(
            DaemonProcessRunner.Default,
            Path.Combine(_directory, "missing-sc.exe"));

        Result<string> result = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonProcessRunner.StartErrorCode, result.Error.Code);

        Assert.NotEqual("DaemonElevationRequired", result.Error.Code);
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

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonBootout", result.Error.Code);

        Assert.DoesNotContain(runner.Calls, static call => call.Contains("bootstrap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linux_install_refuses_a_service_account_without_writing_a_unit()
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        ScriptedDaemonProcessRunner runner = new(static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        LinuxDaemonManager manager = new(runner, unit, static () => false);

        Assert.False(manager.RequiresServiceAccount);

        Result result = await manager.InstallAsync(
            new DaemonInstallRequest(new DaemonServiceCredential("someone", "secret")),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("DaemonServiceAccountUnsupported", result.Error.Code);

        Assert.Empty(runner.Calls);

        Assert.False(File.Exists(unit));
    }

    [Fact]
    public async Task Linux_install_writes_the_user_unit_then_reloads_and_enables_it()
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        ScriptedDaemonProcessRunner runner = new(static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        LinuxDaemonManager manager = new(runner, unit, static () => false);

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Contains("ExecStart=", await File.ReadAllTextAsync(unit), StringComparison.Ordinal);

        Assert.Equal(
            ["systemctl --user daemon-reload", "systemctl --user enable --now arcanum.service"],
            runner.Calls);
    }

    [Fact]
    public async Task Linux_install_names_a_missing_systemd_when_systemctl_cannot_be_started()
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        ScriptedDaemonProcessRunner runner = new(
            static (_, _) => new DaemonProcessOutcome(
                -1,
                string.Empty,
                string.Empty,
                new Error(DaemonProcessRunner.StartErrorCode, "Could not start 'systemctl'. No such file or directory")));

        LinuxDaemonManager manager = new(runner, unit, static () => false);

        Result result = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonProcessRunner.StartErrorCode, result.Error.Code);

        Assert.Contains("systemd may not be available on this host", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_uninstall_disables_the_unit_removes_it_and_reloads()
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        _ = Directory.CreateDirectory(Path.GetDirectoryName(unit)!);

        await File.WriteAllTextAsync(unit, "[Unit]");

        ScriptedDaemonProcessRunner runner = new(static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        LinuxDaemonManager manager = new(runner, unit, static () => false);

        Result result = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(File.Exists(unit));

        Assert.Equal(
            ["systemctl --user disable --now arcanum.service", "systemctl --user daemon-reload"],
            runner.Calls);
    }

    [Theory]
    [InlineData(false, "active\n", "Arcanum daemon is running.")]
    [InlineData(false, "inactive\n", "Daemon is not currently loaded.")]
    [InlineData(true, "", "Daemon is not currently loaded.")]
    public async Task Linux_status_reads_the_unit_active_state(bool unitMissing, string activeState, string expected)
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        if (!unitMissing)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(unit)!);

            await File.WriteAllTextAsync(unit, "[Unit]");
        }

        ScriptedDaemonProcessRunner runner = new(
            (_, _) => ScriptedDaemonProcessRunner.Exit(0, stdout: activeState));

        LinuxDaemonManager manager = new(runner, unit, static () => false);

        Result<string> result = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task Linux_manager_refuses_every_verb_inside_a_container()
    {
        string unit = Path.Combine(_directory, "systemd", "user", "arcanum.service");

        ScriptedDaemonProcessRunner runner = new(static (_, _) => ScriptedDaemonProcessRunner.Exit(0));

        LinuxDaemonManager manager = new(runner, unit, static () => true);

        Result install = await manager.InstallAsync(DaemonInstallRequest.ForInvokingUser, CancellationToken.None);

        Result uninstall = await manager.UninstallAsync(CancellationToken.None);

        Result<string> status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal("ContainerUnsupported", install.Error.Code);

        Assert.Equal("ContainerUnsupported", uninstall.Error.Code);

        Assert.Equal("ContainerUnsupported", status.Error.Code);

        Assert.Empty(runner.Calls);
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
