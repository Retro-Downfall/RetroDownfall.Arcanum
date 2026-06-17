using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public interface IFileSystemBrowser
{

    Task<Result<FileListResult>> ListAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        bool recursive,
        string? searchPattern,
        CancellationToken ct);

    Task<Result<FileReadResult>> ReadAsync(
        WorkspaceInfo workspace,
        string relativePath,
        CancellationToken ct);

    Task<Result<FileEntry>> GetInfoAsync(
        WorkspaceInfo workspace,
        string? relativePath,
        CancellationToken ct);

}
