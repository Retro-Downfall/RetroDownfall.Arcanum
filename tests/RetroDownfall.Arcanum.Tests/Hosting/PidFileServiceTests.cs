using System.ComponentModel;
using System.Globalization;
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
