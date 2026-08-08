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
    string? OldFullPath = null);

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
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
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

        _watcher.EnableRaisingEvents = true;

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

    public void Dispose() => _watcher.Dispose();

}
