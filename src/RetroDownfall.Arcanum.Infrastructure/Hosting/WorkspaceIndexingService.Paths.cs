namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class WorkspaceIndexingService
{
    public string ResolveIndexedWorkspacePath(string workspacePath)
    {
        string normalized = Path.GetFullPath(workspacePath.Trim());

        lock (_schedulerGate)
        {
            return _entries.TryGetValue(SchedulerKey(normalized), out WorkspaceEntry? entry) && IsCurrentLocked(entry)
                ? entry.Path
                : normalized;
        }
    }
}
