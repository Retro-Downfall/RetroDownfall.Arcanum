using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

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

        try
        {

            await using (FileStream tempStream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {

                await using StreamWriter writer = new(tempStream);

                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            if (!ToolHelpers.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
            {

                return (false, ToolError(PathEscapesSandboxMessage));

            }

            File.Move(tempPath, absolutePath, overwrite: true);

            tempPath = string.Empty;

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
        finally
        {

            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
            {

                try
                {

                    File.Delete(tempPath);

                }
                catch (IOException)
                {

                    // Best effort cleanup.

                }

            }

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
