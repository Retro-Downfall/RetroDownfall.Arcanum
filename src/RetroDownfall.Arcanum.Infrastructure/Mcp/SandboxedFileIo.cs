using System.Diagnostics.CodeAnalysis;

using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Handle-based sandboxed file access with post-open containment revalidation.
/// </summary>
internal static class SandboxedFileIo
{

    /// <summary>
    /// Emits the UTF-8 preamble, used only to carry an existing destination BOM across an overwrite.
    /// </summary>
    private static readonly UTF8Encoding PreambleUtf8 = new(encoderShouldEmitUTF8Identifier: true);

    internal static bool TryOpenForRead(
        string workspaceRoot,
        string absolutePath,
        [NotNullWhen(true)] out FileStream? stream,
        [NotNullWhen(false)] out McpToolsCallResultWire? error)
    {

        stream = null;

        error = null;

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, absolutePath, out string? resolvedFinalPath))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        string identityPath = Path.GetFullPath(resolvedFinalPath ?? absolutePath);

        if (!FileHandleIdentityInterop.TryGetPathMetadata(
                identityPath,
                out FileHandleMetadata expectedMetadata)
            || expectedMetadata.Kind != FileSystemObjectKind.RegularFile
            || expectedMetadata.HardLinkCount != 1)
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        SecureFileOpenStatus openStatus =
            SecureFileReader.TryOpenRegularFile(
                absolutePath,
                expectedMetadata.Identity,
                out stream,
                out _);

        if (openStatus is not SecureFileOpenStatus.Success)
        {

            error = OpenError(openStatus);

            return false;

        }

        FileStream openedStream = stream!;

        if (!TryRevalidateOpenedHandle(
                workspaceRoot,
                openedStream,
                expectedMetadata.Identity,
                out error))
        {

            openedStream.Dispose();

            stream = null;

            return false;

        }

        stream = openedStream;

        return true;

    }

    internal static async Task<(bool Success, McpToolsCallResultWire? Error)> TryWriteAllTextAtomicallyAsync(
        string workspaceRoot,
        string absolutePath,
        string content,
        CancellationToken cancellationToken)
    {

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
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

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {

            return (false, ToolError(PathEscapesSandboxMessage));

        }

        string directory = parentDir ?? workspaceRoot;

        string tempPath = Path.Combine(directory, $".arcanum-{Guid.NewGuid():N}.tmp");

        // StreamWriter's implicit encoding is UTF8NoBOM, and the read side
        // (SecureFileReader.ReadUtf8TextAsync) strips the preamble before the model ever sees the
        // text, so a read_file/write_file round-trip used to drop the destination's BOM silently —
        // an unrequested re-encoding of a file the model only meant to edit. Carry it across, the
        // same way PhysicalFileSystemWriter and WorkspaceTextFile already do, but only when the
        // caller's own content does not already begin with U+FEFF, since emitting the preamble on
        // top of that would double it.
        bool preserveDestinationPreamble =
            !content.StartsWith('\uFEFF')
            && DestinationStartsWithUtf8Preamble(workspaceRoot, absolutePath);

        // The bare temp+flush+rename mechanics (and temp cleanup on failure) are shared via
        // AtomicFile.ReplaceAsync; this wrapper keeps the sandbox revalidations interleaved around
        // the write and rename. The write callback intentionally leaves the stream open so the
        // helper owns its lifetime and performs the durability flush.
        FileHandleIdentity expectedIdentity = default;

        try
        {

            AtomicReplaceStatus replaceStatus = await AtomicFile.ReplaceAsync(
                absolutePath,
                tempPath,
                async (stream, ct) =>
                {

                    await using StreamWriter writer = new(
                        stream,
                        encoding: preserveDestinationPreamble ? PreambleUtf8 : null,
                        bufferSize: -1,
                        leaveOpen: true);

                    await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);

                    await writer.FlushAsync(ct).ConfigureAwait(false);

                },
                cancellationToken,
                // W3.4 Group C #7: revalidate the destination path lexically, then capture the temp
                // file's identity BEFORE the move. The move preserves the inode on the same
                // filesystem (temp is created in the destination's parent dir), so the destination's
                // post-move identity must match this. A mismatch means the destination was swapped
                // (e.g. to a symlink) between the lexical revalidation and the move — a TOCTOU
                // sandbox escape. AtomicFile then best-effort restores/quarantines rather than
                // reporting a generic pre-move failure while leaving unverified content in place.
                beforeReplace: () =>
                    WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath)
                        && FileHandleIdentityInterop.TryGetPathIdentity(tempPath, out expectedIdentity),
                // W3.4 Group C #7: post-move handle-identity check, mirroring the read path. Open
                // the destination and verify its handle identity matches the temp file's pre-move
                // identity. TryRevalidateOpenedHandle also re-checks the opened path is under the
                // workspace (resolving symlinks), so a swapped destination is rejected here even if
                // the move followed a symlink (platform-dependent).
                afterReplace: () =>
                    TryVerifyMovedDestination(workspaceRoot, absolutePath, expectedIdentity, out _)).ConfigureAwait(false);

            if (replaceStatus == AtomicReplaceStatus.Succeeded)
            {

                return (true, null);

            }

            if (replaceStatus == AtomicReplaceStatus.ReplacedButUnverified)
            {

                return (false, ToolError(
                    "Write replaced the file but post-move verification failed; destination left unverified."));

            }

            return (false, ToolError(PathEscapesSandboxMessage));

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

    /// <summary>
    /// Reports whether an existing destination begins with a UTF-8 preamble, so an overwrite can
    /// carry it across.
    /// </summary>
    /// <remarks>
    /// The probe goes through the same handle-checked <see cref="TryOpenForRead"/> the read tools
    /// use — it proves the object is a contained, unaliased regular file before opening, so a FIFO
    /// cannot park it — and answers "no preamble" for anything it cannot open. Nothing here decides
    /// the write: the destination is revalidated again, with handle identity, inside the atomic
    /// replace. Mirrors <c>PhysicalFileSystemWriter.DestinationStartsWithUtf8Bom</c>.
    /// </remarks>
    private static bool DestinationStartsWithUtf8Preamble(string workspaceRoot, string absolutePath)
    {

        if (!File.Exists(absolutePath))
        {

            return false;

        }

        if (!TryOpenForRead(workspaceRoot, absolutePath, out FileStream? stream, out _))
        {

            return false;

        }

        using (stream)
        {

            Span<byte> head = stackalloc byte[3];

            try
            {

                return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                    && head.SequenceEqual(Encoding.UTF8.Preamble);

            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {

                return false;

            }

        }

    }

    internal static async Task<(string? Content, McpToolsCallResultWire? Error)> TryReadAllTextAsync(
        string workspaceRoot,
        string absolutePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {

        if (!TryOpenForRead(workspaceRoot, absolutePath, out FileStream? stream, out McpToolsCallResultWire? error))
        {

            return (null, error);

        }

        await using (FileStream openedStream = stream!)
        {

            SecureUtf8FileReadResult readResult =
                await SecureFileReader.ReadUtf8TextAsync(
                        openedStream,
                        maxBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (readResult.Status is not SecureFileReadStatus.Success
                || readResult.Text is null)
            {
                return (null, ReadError(readResult.Status));
            }

            if (!TryRevalidateOpenedHandle(
                    workspaceRoot,
                    openedStream,
                    readResult.Metadata.Identity,
                    out McpToolsCallResultWire? revalidationError))
            {
                return (null, revalidationError);
            }

            return (readResult.Text, null);

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

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, fullPath, out _))
        {

            error = ToolError(PathEscapesSandboxMessage);

            return false;

        }

        if (!SecureFileReader.TryValidateRegularFileHandle(
                stream.SafeFileHandle,
                expectedIdentity,
                out _))
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

    private static readonly string[] OpenErrorMessages =
    [
        PathEscapesSandboxMessage,
        "The specified file was not found.",
        PathEscapesSandboxMessage,
        "Access denied.",
        "An I/O error occurred. See server logs.",
    ];

    private static readonly string[] ReadErrorMessages =
    [
        PathEscapesSandboxMessage,
        "The specified file was not found.",
        PathEscapesSandboxMessage,
        "Access denied.",
        "An I/O error occurred. See server logs.",
        "The file exceeds the maximum read size limit.",
        "The file is not valid UTF-8 text.",
    ];

    private static McpToolsCallResultWire OpenError(
        SecureFileOpenStatus status) =>
        ToolError(OpenErrorMessages[(int)status]);

    private static McpToolsCallResultWire ReadError(
        SecureFileReadStatus status) =>
        ToolError(ReadErrorMessages[(int)status]);

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
