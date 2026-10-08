namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal enum WorkspaceFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

internal sealed record WorkspaceFileChange(
    string WorkspacePath,
    WorkspaceFileChangeKind Kind,
    string FullPath,
    string? OldFullPath = null,
    bool IsDirectory = false);

internal interface IWorkspaceFileWatcher : IDisposable
{
}

internal interface IWorkspaceFileWatcherFactory
{
    IWorkspaceFileWatcher Create(
        string workspacePath,
        Action<WorkspaceFileChange> onChange,
        Action<Exception> onError);
}

internal sealed class WorkspaceFileWatcherFactory : IWorkspaceFileWatcherFactory
{
    public IWorkspaceFileWatcher Create(
        string workspacePath,
        Action<WorkspaceFileChange> onChange,
        Action<Exception> onError) =>
        new FileSystemWorkspaceFileWatcher(workspacePath, onChange, onError);
}

internal sealed class FileSystemWorkspaceFileWatcher : IWorkspaceFileWatcher
{
    private readonly FileSystemWatcher _watcher;

    private readonly FileSystemWatcher _directoryWatcher;

    public FileSystemWorkspaceFileWatcher(
        string workspacePath,
        Action<WorkspaceFileChange> onChange,
        Action<Exception> onError)
    {
        _watcher = new FileSystemWatcher(workspacePath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 16 * 1024,
            NotifyFilter =
                NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };

        // Directory names come from their own watcher, so an event says whether it names a directory
        // without a filesystem probe on the dispatch thread.
        _directoryWatcher = new FileSystemWatcher(workspacePath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 16 * 1024,
            NotifyFilter = NotifyFilters.DirectoryName,
        };

        _watcher.Created += (_, args) =>
            Dispatch(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Created, args.FullPath));

        _watcher.Changed += (_, args) =>
            Dispatch(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Changed, args.FullPath));

        _watcher.Deleted += (_, args) =>
            Dispatch(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Deleted, args.FullPath));

        _watcher.Renamed += (_, args) =>
            Dispatch(new WorkspaceFileChange(
                workspacePath,
                WorkspaceFileChangeKind.Renamed,
                args.FullPath,
                args.OldFullPath));

        _watcher.Error += (_, args) =>
            Report(args.GetException() ?? new IOException("Workspace file watcher reported an unknown error."));

        _directoryWatcher.Created += (_, args) =>
            Dispatch(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Created, args.FullPath, IsDirectory: true));

        _directoryWatcher.Deleted += (_, args) =>
            Dispatch(new WorkspaceFileChange(workspacePath, WorkspaceFileChangeKind.Deleted, args.FullPath, IsDirectory: true));

        _directoryWatcher.Renamed += (_, args) =>
            Dispatch(new WorkspaceFileChange(
                workspacePath,
                WorkspaceFileChangeKind.Renamed,
                args.FullPath,
                args.OldFullPath,
                IsDirectory: true));

        _directoryWatcher.Error += (_, args) =>
            Report(args.GetException() ?? new IOException("Workspace directory watcher reported an unknown error."));

        // Both watchers are built before either starts, and one that cannot start stops the other, so a
        // failed construction never leaves half a pair raising events for a registration nobody owns.
        _watcher.EnableRaisingEvents = true;

        try
        {
            _directoryWatcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            _watcher.Dispose();

            throw;
        }

        // These handlers run on the platform's watcher-dispatch threads (inotify/FSEvents/ReadDirectoryChangesW),
        // which have no ambient exception handler: anything that escapes becomes an unhandled exception that
        // terminates the process on Windows and silently kills the notification loop elsewhere. Every callback
        // is therefore funnelled through a guard so no consumer bug can take the host down from here.
        void Dispatch(WorkspaceFileChange change)
        {
            try
            {
                onChange(change);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        void Report(Exception exception)
        {
            try
            {
                onError(exception);
            }
            catch (Exception)
            {
                // The error sink itself failed; there is nowhere left to report to and throwing from a
                // watcher-dispatch thread is strictly worse than dropping the notification.
            }
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();

        _directoryWatcher.Dispose();
    }
}
