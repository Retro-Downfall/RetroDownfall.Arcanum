using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public interface IFileSystemWriter
{

    Task<Result<FileWriteResult>> WriteFileAsync(
        WorkspaceInfo workspace,
        string relativePath,
        string content,
        CancellationToken ct);

    Task<Result<TextBlockReplaceResult>> ReplaceTextBlockAsync(
        WorkspaceInfo workspace,
        string relativePath,
        string oldString,
        string newString,
        int? expectedReplacements,
        CancellationToken ct);

    Task<Result<FileDeleteResult>> DeleteAsync(
        WorkspaceInfo workspace,
        string relativePath,
        bool recursive,
        CancellationToken ct);

    Task<Result<DirectoryCreateResult>> CreateDirectoryAsync(
        WorkspaceInfo workspace,
        string relativePath,
        CancellationToken ct);

}
