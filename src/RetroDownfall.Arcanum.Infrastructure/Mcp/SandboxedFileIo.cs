using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Handle-based sandboxed file access with post-open containment revalidation.
/// </summary>
internal static class SandboxedFileIo
{

    internal static bool TryOpenForRead(
        string workspaceRoot,
        string absolutePath,
        [NotNullWhen(true)] out FileStream? stream,
        [NotNullWhen(false)] out McpToolsCallResultWire? error)
    {

        stream = null;

        error = null;

        if (!ToolHelpers.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, absolutePath, out string? resolvedFinalPath))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        string identityPath = Path.GetFullPath(resolvedFinalPath ?? absolutePath);

        if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        try
        {

            stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        }
        catch (FileNotFoundException)
        {

            error = ToolError("The specified file was not found.");

            return false;

        }
        catch (DirectoryNotFoundException)
        {

            error = ToolError("The specified directory was not found.");

            return false;

        }
        catch (UnauthorizedAccessException)
        {

            error = ToolError("Access denied.");

            return false;

        }
        catch (IOException)
        {

            error = ToolError("An I/O error occurred. See server logs.");

            return false;

        }

        if (!TryRevalidateOpenedHandle(workspaceRoot, stream, expectedIdentity, out error))
        {

            stream.Dispose();

            stream = null;

            return false;

        }

        return true;

    }

    internal static async Task<(bool Success, McpToolsCallResultWire? Error)> TryWriteAllTextAtomicallyAsync(
        string workspaceRoot,
        string absolutePath,
        string content,
        CancellationToken cancellationToken)
    {

        if (!ToolHelpers.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {

            return (false, ToolError(PathEscapesSandboxMessage));

        }

        string? parentDir = Path.GetDirectoryName(absolutePath);

        if (!string.IsNullOrEmpty(parentDir))
        {

            try
            {

                Directory.CreateDirectory(parentDir);

            }
            catch (UnauthorizedAccessException)
            {

                return (false, ToolError("Access denied creating directory."));

            }
            catch (IOException)
            {

                return (false, ToolError("An I/O error occurred creating directory. See server logs."));

            }

        }

        if (!ToolHelpers.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {

            return (false, ToolError(PathEscapesSandboxMessage));

        }

        string directory = parentDir ?? workspaceRoot;

        string tempPath = Path.Combine(directory, $".arcanum-{Guid.NewGuid():N}.tmp");

        // The bare temp+flush+rename mechanics (and temp cleanup on failure) are shared via
        // AtomicFile.ReplaceAsync; this wrapper keeps the sandbox revalidations interleaved around
        // the write and rename. The write callback intentionally leaves the stream open so the
        // helper owns its lifetime and performs the durability flush.
        FileHandleIdentity expectedIdentity = default;

        try
        {

            bool replaced = await AtomicFile.ReplaceAsync(
                absolutePath,
                tempPath,
                async (stream, ct) =>
                {

                    await using StreamWriter writer = new(stream, encoding: null, bufferSize: -1, leaveOpen: true);

                    await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);

                    await writer.FlushAsync(ct).ConfigureAwait(false);

                },
                cancellationToken,
                // W3.4 Group C #7: revalidate the destination path lexically, then capture the temp
                // file's identity BEFORE the move. The move preserves the inode on the same
                // filesystem (temp is created in the destination's parent dir), so the destination's
                // post-move identity must match this. A mismatch means the destination was swapped
                // (e.g. to a symlink) between the lexical revalidation and the move — a TOCTOU
                // sandbox escape, which fails the replace closed (temp is removed, destination kept).
                beforeReplace: () =>
                    ToolHelpers.RevalidatePathBeforeIo(workspaceRoot, absolutePath)
                        && FileHandleIdentityInterop.TryGetPathIdentity(tempPath, out expectedIdentity),
                // W3.4 Group C #7: post-move handle-identity check, mirroring the read path. Open
                // the destination and verify its handle identity matches the temp file's pre-move
                // identity. TryRevalidateOpenedHandle also re-checks the opened path is under the
                // workspace (resolving symlinks), so a swapped destination is rejected here even if
                // the move followed a symlink (platform-dependent).
                afterReplace: () =>
                    TryVerifyMovedDestination(workspaceRoot, absolutePath, expectedIdentity, out _)).ConfigureAwait(false);

            if (!replaced)
            {

                return (false, ToolError(PathEscapesSandboxMessage));

            }

            return (true, null);

        }
        catch (UnauthorizedAccessException)
        {

            return (false, ToolError("Access denied writing."));

        }
        catch (IOException)
        {

            return (false, ToolError("An I/O error occurred writing. See server logs."));

        }

    }

    internal static async Task<(string? Content, McpToolsCallResultWire? Error)> TryReadAllTextAsync(
        string workspaceRoot,
        string absolutePath,
        CancellationToken cancellationToken)
    {

        if (!TryOpenForRead(workspaceRoot, absolutePath, out FileStream? stream, out McpToolsCallResultWire? error))
        {

            return (null, error);

        }

        await using (stream)
        {

            using StreamReader reader = new(stream);

            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            return (content, null);

        }

    }

    internal static bool TryRevalidateOpenedHandle(
        string workspaceRoot,
        FileStream stream,
        FileHandleIdentity expectedIdentity,
        [NotNullWhen(false)] out McpToolsCallResultWire? error)
    {

        error = null;

        string openedPath = stream.Name;

        if (string.IsNullOrWhiteSpace(openedPath))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        string fullPath;

        try
        {

            fullPath = Path.GetFullPath(openedPath);

        }
        catch (Exception)
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, fullPath, out _))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actualIdentity))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        return true;

    }

    // W3.4 Group C #7: opens the just-moved destination and reuses TryRevalidateOpenedHandle
    // to confirm its handle identity matches the temp file's pre-move identity (and that the
    // opened path is still under the workspace). A mismatch indicates the destination was
    // swapped between validation and File.Move. Best-effort I/O failures are treated as a
    // sandbox escape (fail-closed) because the write's destination could not be confirmed.
    private static bool TryVerifyMovedDestination(
        string workspaceRoot,
        string absolutePath,
        FileHandleIdentity expectedIdentity,
        [NotNullWhen(false)] out McpToolsCallResultWire? error)
    {

        error = null;

        FileStream verifyStream;

        try
        {

            verifyStream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        }

        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        using (verifyStream)
        {

            if (!TryRevalidateOpenedHandle(workspaceRoot, verifyStream, expectedIdentity, out error))
            {

                return false;

            }

        }

        return true;

    }

    private const string PathEscapesSandboxMessage =
        "That path would leave the workspace sandbox, so the operation was not performed. Please use a path relative to the workspace root.";

    private static McpToolsCallResultWire ToolError(string text) =>
        new()
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = true,
        };

}
