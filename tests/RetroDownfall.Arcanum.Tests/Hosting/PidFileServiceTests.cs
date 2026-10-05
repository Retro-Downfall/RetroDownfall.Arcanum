using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class PidFileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-pid-file-" + Guid.NewGuid().ToString("N"));

    private string PidPath => Path.Combine(_directory, "run", "arcanum.pid");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Refusal_message_names_the_pid_file_path()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        PidFileService service = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Contains("4242", refusal.Message, StringComparison.Ordinal);

        Assert.Contains(PidPath, refusal.Message, StringComparison.Ordinal);

        Assert.Contains("no Arcanum process owns", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recorded_owner_the_caller_cannot_open_is_refused_with_the_remedy_not_thrown_as_an_access_error()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        PidFileService service = Service(
            static _ => PidFileOwnership.Observe(
                static () => throw new Win32Exception(5, "Access is denied."),
                static () => DateTime.UtcNow));

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Contains("4242", refusal.Message, StringComparison.Ordinal);

        Assert.Contains(PidPath, refusal.Message, StringComparison.Ordinal);

        Assert.Equal("4242", (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task Second_start_does_not_overwrite_a_live_owner()
    {
        PidFileService first = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        await first.StartAsync(CancellationToken.None);

        string owner = await File.ReadAllTextAsync(PidPath);

        Assert.Equal(global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), owner.Trim());

        PidFileService second = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.StartAsync(CancellationToken.None));

        Assert.Equal(owner, await File.ReadAllTextAsync(PidPath));
    }

    [Fact]
    public async Task A_pid_now_held_by_a_process_that_started_after_the_file_was_written_is_stale()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        // The live process with that id began after the file existed, so it cannot be the host that wrote it.
        PidFileService service = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task A_live_owner_with_an_unreadable_start_time_is_never_replaced()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        PidFileService service = Service(static _ => new PidFileOwnerProcess(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Equal("4242", (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task A_dead_owner_is_replaced()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        PidFileService service = Service(static _ => null);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task A_malformed_file_is_replaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PidPath)!);

        await File.WriteAllTextAsync(PidPath, "not-a-pid");

        PidFileService service = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task Concurrent_claims_on_a_fresh_path_admit_exactly_one_owner()
    {
        PidFileService[] services =
        [
            .. Enumerable.Range(0, 8)
                .Select(_ => Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)))),
        ];

        using Barrier barrier = new(services.Length);

        Task<bool>[] claims =
        [
            .. services.Select(service => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                try
                {
                    await service.StartAsync(CancellationToken.None);

                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })),
        ];

        bool[] outcomes = await Task.WhenAll(claims);

        Assert.Single(outcomes, static claimed => claimed);
    }

    [Fact]
    public async Task A_starter_that_read_a_stale_file_before_another_replaced_it_does_not_delete_the_new_claim()
    {
        const int StalePid = 4242;

        WriteOwner(StalePid, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        using ManualResetEventSlim slowHasReadTheStaleFile = new();

        using ManualResetEventSlim releaseSlow = new();

        PidFileService slow = Service(
            pid =>
            {
                if (pid != StalePid)
                {
                    return new PidFileOwnerProcess(DateTimeOffset.UtcNow.AddHours(-1));
                }

                // This starter has now read the stale file and decided it is replaceable; it goes no further until
                // the other starter has replaced the file and claimed it.
                slowHasReadTheStaleFile.Set();

                Assert.True(releaseSlow.Wait(TimeSpan.FromSeconds(30)));

                return null;
            });

        PidFileService fast = Service(
            static pid => pid == StalePid ? null : new PidFileOwnerProcess(DateTimeOffset.UtcNow.AddHours(-1)));

        Task<Exception?> slowStart = Task.Run(
            async () =>
            {
                try
                {
                    await slow.StartAsync(CancellationToken.None);

                    return (Exception?)null;
                }
                catch (InvalidOperationException exception)
                {
                    return exception;
                }
            });

        Assert.True(slowHasReadTheStaleFile.Wait(TimeSpan.FromSeconds(30)));

        await fast.StartAsync(CancellationToken.None);

        releaseSlow.Set();

        Exception? refusal = await slowStart;

        Assert.NotNull(refusal);

        Assert.Contains("already running", refusal.Message, StringComparison.Ordinal);

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task Concurrent_claims_on_a_stale_path_admit_exactly_one_owner()
    {
        const int StalePid = 4242;

        WriteOwner(StalePid, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        PidFileService[] services =
        [
            .. Enumerable.Range(0, 8)
                .Select(_ => Service(
                    static pid => pid == StalePid ? null : new PidFileOwnerProcess(DateTimeOffset.UtcNow.AddHours(-1)))),
        ];

        using Barrier barrier = new(services.Length);

        Task<bool>[] claims =
        [
            .. services.Select(service => Task.Run(async () =>
            {
                barrier.SignalAndWait();

                try
                {
                    await service.StartAsync(CancellationToken.None);

                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })),
        ];

        bool[] outcomes = await Task.WhenAll(claims);

        Assert.Single(outcomes, static claimed => claimed);
    }

    [Fact]
    public async Task A_starter_waits_for_the_replacement_gate_and_then_replaces_the_stale_file()
    {
        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        // Another starter is in the middle of replacing the same stale file.
        FileStream held = new(
            PidPath + PidFileService.ReplacementGateSuffix,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        using ManualResetEventSlim lookedAgain = new();

        int lookUps = 0;

        PidFileService service = Service(
            _ =>
            {
                // The second look means the first attempt found the gate busy and waited.
                if (Interlocked.Increment(ref lookUps) >= 2)
                {
                    lookedAgain.Set();
                }

                return null;
            });

        Task start = service.StartAsync(CancellationToken.None);

        Assert.True(lookedAgain.Wait(TimeSpan.FromSeconds(30)));

        Assert.False(start.IsCompleted, "The starter replaced the file while another held the gate.");

        await held.DisposeAsync();

        await start;

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_stale_file_that_cannot_be_removed_is_reported_with_its_reason_not_as_contention()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses Unix directory permissions.");

        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        // The gate already exists, so only the delete is refused.
        await File.WriteAllTextAsync(PidPath + PidFileService.ReplacementGateSuffix, string.Empty);

        string directory = Path.GetDirectoryName(PidPath)!;

        PidFileService service = Service(static _ => null);

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartAsync(CancellationToken.None));

            Assert.Contains("Could not remove the stale PID file", failure.Message, StringComparison.Ordinal);

            Assert.Contains(PidPath, failure.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("starting at the same time", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_pid_file_the_account_may_not_read_is_reported_as_access_denied_not_contention()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses Unix file permissions.");

        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        File.SetUnixFileMode(PidPath, UnixFileMode.None);

        try
        {
            PidFileService service = Service(static _ => null);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartAsync(CancellationToken.None));

            Assert.Contains("access is denied", failure.Message, StringComparison.Ordinal);

            Assert.Contains(PidPath, failure.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("starting at the same time", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(PidPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_replacement_gate_is_created_owner_only()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses Unix file permissions.");

        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        await Service(static _ => null).StartAsync(CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(PidPath + PidFileService.ReplacementGateSuffix));
    }

    /// <summary>
    /// Windows answers a create over a file whose delete is still pending with "access denied", the same error as a
    /// real permission problem. The claim must retry through it rather than throw an access error out of start-up.
    /// </summary>
    [SkippableFact]
    public async Task Windows_a_claim_over_a_file_with_a_pending_delete_is_retried_not_thrown()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Delete-pending semantics are Windows-only.");

        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow.AddHours(-3));

        using FileStream pending = new(
            PidPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        File.Delete(PidPath);

        PidFileService service = Service(static _ => null);

        Task start = service.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        await pending.DisposeAsync();

        await start;

        Assert.Equal(
            global::System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            (await File.ReadAllTextAsync(PidPath)).Trim());
    }

    [Fact]
    public async Task Stop_removes_only_a_file_that_still_names_this_process()
    {
        PidFileService service = Service(LiveSince(DateTimeOffset.UtcNow.AddHours(-1)));

        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(PidPath));

        WriteOwner(4242, writtenAt: DateTimeOffset.UtcNow);

        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(PidPath));
    }

    private PidFileService Service(Func<int, PidFileOwnerProcess?> lookUp) =>
        new(PidPath, NullLogger<PidFileService>.Instance, lookUp);

    private static Func<int, PidFileOwnerProcess?> LiveSince(DateTimeOffset startedAt) =>
        _ => new PidFileOwnerProcess(startedAt);

    private void WriteOwner(int pid, DateTimeOffset writtenAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PidPath)!);

        File.WriteAllText(PidPath, pid.ToString(CultureInfo.InvariantCulture));

        File.SetLastWriteTimeUtc(PidPath, writtenAt.UtcDateTime);
    }
}
