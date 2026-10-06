using System.Collections.Concurrent;
using System.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// The platform contract the workspace intake relies on, against a real temporary directory: the
/// directory-name watcher beside the file watcher says which events name a directory, so the intake
/// never probes the disk on a watcher thread to find out.
/// </summary>
/// <remarks>
/// A fake would only prove that the fake agrees with itself. Which watcher raises which event is a
/// property of the platform's notification source (FSEvents, inotify, ReadDirectoryChangesW).
/// </remarks>
public sealed class FileSystemWorkspaceFileWatcherTests : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private static readonly TimeSpan PollDeadline = TimeSpan.FromSeconds(15);

    private readonly string _root = Directory.CreateTempSubdirectory("arcanum-watcher-").FullName;

    [Fact]
    public async Task The_platform_watchers_say_which_events_name_a_directory()
    {
        ConcurrentQueue<WorkspaceFileChange> changes = new();

        ConcurrentQueue<Exception> errors = new();

        using FileSystemWorkspaceFileWatcher watcher = new(_root, changes.Enqueue, errors.Enqueue);

        string notes = Directory.CreateDirectory(Path.Combine(_root, "notes")).FullName;

        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "# Notes");

        await WaitForAsync(
            changes,
            "creating notes",
            static change => change.Kind == WorkspaceFileChangeKind.Created && Names(change, "notes"));

        await WaitForAsync(changes, "naming readme.md", static change => Names(change, "readme.md"));

        Directory.Move(notes, Path.Combine(_root, "notes-moved"));

        // A platform that does not pair the two halves of the move reports the new name as created.
        await WaitForAsync(
            changes,
            "naming notes-moved",
            static change => Names(change, "notes-moved")
                && change.Kind is WorkspaceFileChangeKind.Renamed or WorkspaceFileChangeKind.Created);

        WorkspaceFileChange[] seen = [.. changes];

        string described = Describe(seen);

        Assert.All(
            seen.Where(static change => change.Kind == WorkspaceFileChangeKind.Created && Names(change, "notes")),
            change => Assert.True(change.IsDirectory, described));

        Assert.All(
            seen.Where(static change => Names(change, "readme.md")),
            change => Assert.False(change.IsDirectory, described));

        Assert.All(
            seen.Where(static change => Names(change, "notes-moved")),
            change => Assert.True(change.IsDirectory, described));

        Assert.Empty(errors);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing the suite over.
        }
    }

    private static async Task WaitForAsync(
        ConcurrentQueue<WorkspaceFileChange> changes,
        string expectation,
        Func<WorkspaceFileChange, bool> match)
    {
        Stopwatch clock = Stopwatch.StartNew();

        while (!changes.Any(match))
        {
            if (clock.Elapsed >= PollDeadline)
            {
                Assert.Fail($"No watcher event {expectation} arrived within {PollDeadline.TotalSeconds:0} s. Seen: {Describe(changes)}");
            }

            await Task.Delay(PollInterval);
        }
    }

    private static bool Names(WorkspaceFileChange change, string name) =>
        string.Equals(Path.GetFileName(change.FullPath), name, StringComparison.Ordinal);

    private static string Describe(IEnumerable<WorkspaceFileChange> changes) =>
        string.Join("; ", changes.Select(static change => $"{change.Kind} {Path.GetFileName(change.FullPath)} directory={change.IsDirectory}"));
}
