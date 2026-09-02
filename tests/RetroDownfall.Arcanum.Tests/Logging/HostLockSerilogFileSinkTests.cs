using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;

using Serilog;
using Serilog.Core;

namespace RetroDownfall.Arcanum.Tests.Logging;

/// <summary>
/// W8-6: the rolling file sink must never silently drop an event past its per-file size cap.
/// </summary>
public sealed class HostLockSerilogFileSinkTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    /// <summary>
    /// Before this fix, activating with no explicit size limit left Serilog's own 1 GiB default in
    /// place with rolling off, so an event past that ceiling is dropped with no diagnostic. A 4 KB
    /// limit makes the same failure mode reachable in a fast test: emit past it and confirm every
    /// event lands somewhere on disk, in a rolled sibling file, rather than disappearing once the
    /// first file fills.
    /// </summary>
    [Fact]
    public void Events_past_the_size_limit_roll_into_a_sibling_file_instead_of_disappearing()
    {

        string guardedRoot = _workspace.CreateSubdir("guarded");

        string logDirectory = _workspace.CreateSubdir("logs");

        const int eventCount = 200;

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(guardedRoot);

        Assert.NotNull(held);

        using HostLockSerilogFileSink sink = new(
            guardedRoot,
            logDirectory,
            retainedFileCountLimit: 100,
            enabled: true,
            fileSizeLimitBytes: 4096);

        sink.Activate(held, guardedRoot);

        using (Logger emitter = new LoggerConfiguration()
                   .MinimumLevel.Verbose()
                   .WriteTo.Sink(sink)
                   .CreateLogger())
        {

            for (int index = 0; index < eventCount; index++)
            {

                emitter.Information(
                    "Synthetic host log line {Index} padded to exceed the compact size limit quickly {Padding}",
                    index,
                    new string('x', 64));

            }

        }

        sink.Deactivate();

        string[] logFiles = Directory.GetFiles(logDirectory, "arcanum-api-*.json");

        Assert.True(
            logFiles.Length > 1,
            $"Expected the 4 KB size limit to force a roll into more than one file; found {logFiles.Length}.");

        int totalLines = logFiles.Sum(path => File.ReadAllLines(path).Length);

        Assert.Equal(eventCount, totalLines);

    }

}
