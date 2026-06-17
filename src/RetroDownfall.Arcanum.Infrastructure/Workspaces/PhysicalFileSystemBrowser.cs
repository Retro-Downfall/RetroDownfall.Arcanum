using System.Security;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class PhysicalFileSystemBrowser(IOptionsMonitor<ArcanumSettings> optionsMonitor) : IFileSystemBrowser
{

    public Task<Result<FileListResult>> ListAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        bool recursive,
        string? searchPattern,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        if (searchPattern is not null && (searchPattern.Contains('/') || searchPattern.Contains('\\')))
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.InvalidSearchPattern", "Search pattern cannot contain path separators."));
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<FileListResult>>(resolvedResult.Error);
        }

        string resolvedDir = resolvedResult.Value;

        if (!Directory.Exists(resolvedDir))
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.FileNotFound", "The file or directory was not found."));
        }

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        StringComparer nameComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        int maxPaths = GetListDirectoryMaxPaths();

        int maxDepth = GetListDirectoryMaxDepth();

        List<FileEntry> entries = [];

        try
        {

            if (recursive)
            {
                Queue<(string Path, int Depth)> dirs = new();

                dirs.Enqueue((resolvedDir, 0));

                while (dirs.Count > 0)
                {

                    ct.ThrowIfCancellationRequested();

                    (string dir, int depth) = dirs.Dequeue();

                    foreach (string fullPath in Directory.EnumerateFileSystemEntries(
                                 dir,
                                 searchPattern ?? "*",
                                 SearchOption.TopDirectoryOnly))
                    {

                        ct.ThrowIfCancellationRequested();

                        if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, fullPath, out _))
                        {
                            continue;
                        }

                        FileEntry? entry = TryMapToFileEntry(workspaceRoot, fullPath);

                        if (entry is not null)
                        {
                            entries.Add(entry);
                        }

                        if (entries.Count >= maxPaths)
                        {
                            break;
                        }

                        if (depth < maxDepth && Directory.Exists(fullPath))
                        {
                            dirs.Enqueue((fullPath, depth + 1));
                        }
                    }

                    if (entries.Count >= maxPaths)
                    {
                        break;
                    }
                }
            }
            else
            {

                foreach (string fullPath in Directory.EnumerateFileSystemEntries(
                             resolvedDir,
                             searchPattern ?? "*",
                             SearchOption.TopDirectoryOnly))
                {

                    ct.ThrowIfCancellationRequested();

                    if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, fullPath, out _))
                    {
                        continue;
                    }

                    FileEntry? entry = TryMapToFileEntry(workspaceRoot, fullPath);

                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }

                    if (entries.Count >= maxPaths)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<FileListResult>>(
                new Error("Workspace.AccessDenied", "Insufficient permissions to read the directory."));
        }

        FileEntry[] sorted = entries
            .OrderBy(e => e.Type == FileEntryType.Directory ? 0 : 1)
            .ThenBy(e => e.Name, nameComparer)
            .ToArray();

        string? parentPath = ComputeParentRelativePath(workspaceRoot, resolvedDir);

        return Task.FromResult<Result<FileListResult>>(new FileListResult(sorted, parentPath));
    }

    public async Task<Result<FileReadResult>> ReadAsync(
        WorkspaceInfo workspace,
        string relativePath,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return resolvedResult.Error;
        }

        string resolvedPath = resolvedResult.Value;

        if (Directory.Exists(resolvedPath))
        {
            return new Error("Workspace.FileNotFound", "The file or directory was not found.");
        }

        if (!File.Exists(resolvedPath))
        {
            return new Error("Workspace.FileNotFound", "The file or directory was not found.");
        }

        long maxBytes = GetMaxFileReadSizeBytes();

        try
        {

            FileInfo info = new(resolvedPath);

            if (info.Length > maxBytes)
            {
                return new Error("Workspace.FileTooLarge", "The file exceeds the maximum read size limit.");
            }

            string content = await File.ReadAllTextAsync(resolvedPath, ct).ConfigureAwait(false);

            string workspaceRoot = Path.GetFullPath(workspace.Path);

            string entryRelativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);

            return new FileReadResult(
                entryRelativePath,
                content,
                "utf-8",
                info.Length,
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new Error("Workspace.AccessDenied", "Insufficient permissions to read the file.");
        }
    }

    public Task<Result<FileEntry>> GetInfoAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<FileEntry>>(resolvedResult.Error);
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, resolvedPath))
        {
            DirectoryInfo rootInfo = new(workspaceRoot);

            return Task.FromResult<Result<FileEntry>>(new FileEntry(
                rootInfo.Name,
                string.Empty,
                workspaceRoot,
                FileEntryType.Directory,
                0,
                rootInfo.LastWriteTimeUtc));
        }

        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            return Task.FromResult<Result<FileEntry>>(
                new Error("Workspace.FileNotFound", "The file or directory was not found."));
        }

        try
        {

            FileEntry? entry = TryMapToFileEntry(workspaceRoot, resolvedPath);

            if (entry is null)
            {
                return Task.FromResult<Result<FileEntry>>(
                    new Error("Workspace.FileNotFound", "The file or directory was not found."));
            }

            return Task.FromResult<Result<FileEntry>>(entry);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<FileEntry>>(
                new Error("Workspace.AccessDenied", "Insufficient permissions to read the file or directory."));
        }
    }

    private int GetListDirectoryMaxPaths()
    {

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        int configured = settings.Intelligence?.ListDirectoryMaxPaths ?? new IntelligenceSettings().ListDirectoryMaxPaths;

        return ArcanumSettingClamps.ListDirectoryMaxPaths(configured);
    }

    private int GetListDirectoryMaxDepth()
    {

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        int configured = settings.Workspaces?.ListDirectoryMaxDepth ?? new WorkspaceSettings().ListDirectoryMaxDepth;

        return ArcanumSettingClamps.ListDirectoryMaxDepth(configured);
    }

    private long GetMaxFileReadSizeBytes()
    {

        ArcanumSettings settings = optionsMonitor.CurrentValue;

        long configured = settings.Workspaces?.MaxFileReadSizeBytes ?? new WorkspaceSettings().MaxFileReadSizeBytes;

        return ArcanumSettingClamps.MaxFileReadSizeBytes(configured);
    }

    private static FileEntry? TryMapToFileEntry(string workspaceRoot, string fullPath)
    {

        try
        {

            if (Directory.Exists(fullPath))
            {

                DirectoryInfo dirInfo = new(fullPath);

                string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

                return new FileEntry(
                    dirInfo.Name,
                    relativePath,
                    fullPath,
                    FileEntryType.Directory,
                    0,
                    dirInfo.LastWriteTimeUtc);
            }

            if (File.Exists(fullPath))
            {

                FileInfo fileInfo = new(fullPath);

                string relativePath = Path.GetRelativePath(workspaceRoot, fullPath);

                return new FileEntry(
                    fileInfo.Name,
                    relativePath,
                    fullPath,
                    FileEntryType.File,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string? ComputeParentRelativePath(string workspaceRoot, string resolvedDir)
    {

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, resolvedDir))
        {
            return null;
        }

        string parentFull = Path.GetDirectoryName(resolvedDir) ?? workspaceRoot;

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, parentFull))
        {
            return string.Empty;
        }

        return Path.GetRelativePath(workspaceRoot, parentFull);
    }

}
