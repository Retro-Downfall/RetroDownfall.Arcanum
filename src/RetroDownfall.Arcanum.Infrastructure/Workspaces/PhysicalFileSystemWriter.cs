using System.Security;
using System.Text;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public sealed class PhysicalFileSystemWriter(IOptionsSnapshot<ArcanumSettings> options) : IFileSystemWriter
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly AsyncLocal<Action<string>?> AfterReplaceTextBlockReadSeam = new();

    /// <summary>
    /// Deterministic test seam invoked with the absolute target path after <see cref="ReplaceTextBlockAsync"/>
    /// has read the file and before it writes, so a test can change the destination at exactly that point.
    /// Scoped to the current async flow, so it cannot leak into a concurrently running test.
    /// </summary>
    internal static Action<string>? AfterReplaceTextBlockReadForTests
    {
        get => AfterReplaceTextBlockReadSeam.Value;
        set => AfterReplaceTextBlockReadSeam.Value = value;
    }

    public async Task<Result<FileWriteResult>> WriteFileAsync(
        WorkspaceInfo workspace,
        string relativePath,
        string content,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsFileWriteEnabled())
        {
            return new Error(ErrorCodes.Workspace.FileWriteDisabled, FileWriteDisabledMessage);
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return resolvedResult.Error;
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (WorkspaceProtectedPaths.IsProtectedPath(workspaceRoot, resolvedPath))
        {
            return new Error(ErrorCodes.Workspace.PathNotAllowed, ProtectedPathMessage);
        }

        if (Directory.Exists(resolvedPath))
        {
            return new Error(ErrorCodes.Workspace.PathIsDirectory, PathIsDirectoryMessage);
        }

        byte[] contentBytes = Encoding.UTF8.GetBytes(content);

        // Encoding.UTF8.GetBytes never emits the preamble, and the read side strips it before handing the
        // caller FileReadResult.Content, so a read-modify-write through GET then PUT used to drop the
        // destination's BOM silently. Re-apply it, exactly as ReplaceTextBlockAsync already does — but only
        // when the caller's own content does not already start with U+FEFF, which GetBytes has itself
        // encoded as EF BB BF, since prefixing unconditionally would double the preamble.
        if (!contentBytes.AsSpan().StartsWith(Utf8Bom) && DestinationStartsWithUtf8Bom(workspaceRoot, resolvedPath))
        {
            contentBytes = [.. Utf8Bom, .. contentBytes];
        }

        long maxWriteBytes = GetMaxFileWriteSizeBytes();

        if (contentBytes.LongLength > maxWriteBytes)
        {
            return new Error(ErrorCodes.Workspace.FileTooLarge, FileTooLargeMessage);
        }

        Result writeResult = await WriteAtomicallyAsync(workspaceRoot, resolvedPath, contentBytes, ct).ConfigureAwait(false);

        if (writeResult.IsFailure)
        {
            return writeResult.Error;
        }

        string entryRelativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);

        return new FileWriteResult(entryRelativePath, contentBytes.LongLength, GetLastWriteTimeUtcSafe(resolvedPath));
    }

    public async Task<Result<TextBlockReplaceResult>> ReplaceTextBlockAsync(
        WorkspaceInfo workspace,
        string relativePath,
        string oldString,
        string newString,
        int? expectedReplacements,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsFileWriteEnabled())
        {
            return new Error(ErrorCodes.Workspace.FileWriteDisabled, FileWriteDisabledMessage);
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return resolvedResult.Error;
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (WorkspaceProtectedPaths.IsProtectedPath(workspaceRoot, resolvedPath))
        {
            return new Error(ErrorCodes.Workspace.PathNotAllowed, ProtectedPathMessage);
        }

        if (!File.Exists(resolvedPath))
        {
            return new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage);
        }

        long newStringBytes = Encoding.UTF8.GetByteCount(newString);

        if (newStringBytes > GetMaxFileWriteSizeBytes())
        {
            return new Error(ErrorCodes.Workspace.FileTooLarge, FileTooLargeMessage);
        }

        long combinedBytes = Encoding.UTF8.GetByteCount(oldString) + newStringBytes;

        if (combinedBytes > GetMaxReplaceTextBlockBytes())
        {
            return new Error(ErrorCodes.Workspace.FileTooLarge, ReplaceTextBlockTooLargeMessage);
        }

        (FileStream? readStream, Error? openError) = TryOpenForHandleCheckedRead(workspaceRoot, resolvedPath);

        if (readStream is null)
        {
            return openError!.Value;
        }

        string text;

        bool hadBom;

        FileContentBaseline readBaseline = default;

        try
        {
            await using (readStream)
            {
                using SecureFileReadResult readResult = await SecureFileReader
                    .ReadBytesAsync(
                        readStream,
                        checked((int)GetMaxFileReadSizeBytes()),
                        ct)
                    .ConfigureAwait(false);

                if (readResult.Status is not SecureFileReadStatus.Success)
                {
                    return MapSecureReadError(readResult.Status);
                }

                ReadOnlySpan<byte> bytes = readResult.Bytes.Span;

                // The exact bytes the edit is computed from, taken from this read (BOM included); the
                // replace aborts if the destination is no longer byte-for-byte this.
                readBaseline = FileContentBaseline.Of(bytes);

                hadBom = bytes.StartsWith(Utf8Bom);

                ReadOnlySpan<byte> textBytes = hadBom ? bytes[Utf8Bom.Length..] : bytes;

                // A NUL byte marks the payload as binary. The ordinal search can still match an ASCII
                // run inside it, and the rewrite below would then persist the decoded text over the
                // original bytes. WorkspaceTextFile.Decode rejects binary payloads on the MCP coding-tool
                // path for the same reason; this path fails closed rather than mangling the file.
                if (textBytes.IndexOf((byte)0) >= 0)
                {
                    return new Error(ErrorCodes.Workspace.PathNotAllowed, BinaryTargetMessage);
                }

                try
                {
                    // Decode strictly. The static Encoding.UTF8 singleton uses a REPLACEMENT decoder
                    // fallback, so every invalid byte became U+FFFD and the atomic rewrite persisted
                    // EF BF BD over the original bytes with no warning and no recoverable backup. The
                    // sibling read paths (PhysicalFileSystemBrowser.ReadAsync, SandboxedFileIo) already
                    // decode with StrictUtf8 and fail closed; this one is the path that persists.
                    text = StrictUtf8.GetString(textBytes);
                }
                catch (DecoderFallbackException)
                {
                    return new Error(ErrorCodes.Workspace.PathNotAllowed, InvalidUtf8Message);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage);
        }
        catch (IOException)
        {
            return new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage);
        }

        int occurrences = CountOccurrences(text, oldString);

        if (occurrences == 0)
        {
            return new Error(ErrorCodes.Workspace.ReplacementNotFound, ReplacementNotFoundMessage);
        }

        if (expectedReplacements is null ? occurrences > 1 : expectedReplacements.Value != occurrences)
        {
            return new Error(ErrorCodes.Workspace.ReplacementAmbiguous, ReplacementAmbiguousMessage);
        }

        string replacedText = text.Replace(oldString, newString, StringComparison.Ordinal);

        byte[] replacedTextBytes = Encoding.UTF8.GetBytes(replacedText);

        byte[] outputBytes = hadBom ? [.. Utf8Bom, .. replacedTextBytes] : replacedTextBytes;

        AfterReplaceTextBlockReadForTests?.Invoke(resolvedPath);

        Result writeResult = await WriteAtomicallyAsync(workspaceRoot, resolvedPath, outputBytes, ct, readBaseline).ConfigureAwait(false);

        if (writeResult.IsFailure)
        {
            return writeResult.Error;
        }

        string entryRelativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);

        return new TextBlockReplaceResult(entryRelativePath, occurrences, outputBytes.LongLength, GetLastWriteTimeUtcSafe(resolvedPath));
    }

    public Task<Result<FileDeleteResult>> DeleteAsync(
        WorkspaceInfo workspace,
        string relativePath,
        bool recursive,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsFileWriteEnabled())
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.FileWriteDisabled, FileWriteDisabledMessage));
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<FileDeleteResult>>(resolvedResult.Error);
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        // The resolver maps an empty, blank, or "." path to the root on purpose, because the listing
        // routes ask for exactly that. Delete is the one caller for which it is never a legitimate
        // target, and the containment check below cannot catch it: root-equals-root satisfies
        // every containment rule there is.
        if (WorkspaceRootPolicy.IsSamePath(resolvedPath, workspaceRoot))
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(
                    ErrorCodes.Workspace.PathNotAllowed,
                    "The workspace root cannot be deleted. Name a path inside the workspace."));
        }

        if (WorkspaceProtectedPaths.IsProtectedPath(workspaceRoot, resolvedPath))
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.PathNotAllowed, ProtectedPathMessage));
        }

        bool isDirectory = Directory.Exists(resolvedPath);

        bool isFile = !isDirectory && File.Exists(resolvedPath);

        if (!isDirectory && !isFile)
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage));
        }

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, resolvedPath))
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        try
        {
            if (isFile)
            {
                File.Delete(resolvedPath);
            }
            else if (!recursive)
            {
                if (Directory.EnumerateFileSystemEntries(resolvedPath).Any())
                {
                    return Task.FromResult<Result<FileDeleteResult>>(
                        new Error(ErrorCodes.Workspace.DirectoryNotEmpty, DirectoryNotEmptyMessage));
                }

                Directory.Delete(resolvedPath, recursive: false);
            }
            else
            {
                // Refuse before deleting anything: an escaping link inside the tree is never followed or
                // removed, so deleting around it used to empty the directory, then fail on the final
                // directory removal with a generic I/O error that named nothing.
                string? escapingEntry = FindEscapingEntry(workspaceRoot, resolvedPath, ct);

                if (escapingEntry is not null)
                {
                    return Task.FromResult<Result<FileDeleteResult>>(
                        new Error(
                            ErrorCodes.Workspace.SymbolicLinkEscape,
                            $"The directory contains a symbolic link that resolves outside the workspace ('{Path.GetRelativePath(workspaceRoot, escapingEntry)}'), so nothing was deleted. Remove or retarget that link first."));
                }

                DeleteRecursive(workspaceRoot, resolvedPath, ct);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage));
        }
        catch (IOException)
        {
            return Task.FromResult<Result<FileDeleteResult>>(
                new Error(ErrorCodes.Workspace.DeleteFailed, IoDeleteErrorMessage));
        }

        string entryRelativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);

        return Task.FromResult<Result<FileDeleteResult>>(
            new FileDeleteResult(entryRelativePath, isDirectory, DateTimeOffset.UtcNow));
    }

    public Task<Result<DirectoryCreateResult>> CreateDirectoryAsync(
        WorkspaceInfo workspace,
        string relativePath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsFileWriteEnabled())
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.FileWriteDisabled, FileWriteDisabledMessage));
        }

        Result<string> resolvedResult = WorkspacePathResolver.ResolveRelativePath(workspace, relativePath);

        if (resolvedResult.IsFailure)
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(resolvedResult.Error);
        }

        string resolvedPath = resolvedResult.Value;

        string workspaceRoot = Path.GetFullPath(workspace.Path);

        if (WorkspaceProtectedPaths.IsProtectedPath(workspaceRoot, resolvedPath))
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.PathNotAllowed, ProtectedPathMessage));
        }

        if (File.Exists(resolvedPath))
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.PathIsFile, PathIsFileMessage));
        }

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, resolvedPath))
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        try
        {
            Directory.CreateDirectory(resolvedPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage));
        }
        catch (IOException)
        {
            return Task.FromResult<Result<DirectoryCreateResult>>(
                new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage));
        }

        string entryRelativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);

        return Task.FromResult<Result<DirectoryCreateResult>>(
            new DirectoryCreateResult(entryRelativePath, DateTimeOffset.UtcNow));
    }

    private bool IsFileWriteEnabled()
    {
        ArcanumSettings settings = options.Value;

        return settings.Workspaces?.EnableFileWrite ?? new WorkspaceSettings().EnableFileWrite;
    }

    private long GetMaxFileWriteSizeBytes()
    {
        long configured = ArcanumRuntimeDefaults.WorkspaceMaxFileWriteSizeBytes;

        return ArcanumSettingClamps.MaxFileWriteSizeBytes(configured);
    }

    private long GetMaxReplaceTextBlockBytes()
    {
        long configured = ArcanumRuntimeDefaults.WorkspaceMaxReplaceTextBlockBytes;

        return ArcanumSettingClamps.MaxReplaceTextBlockBytes(configured);
    }

    private long GetMaxFileReadSizeBytes()
    {
        long configured = ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes;

        return ArcanumSettingClamps.MaxFileReadSizeBytes(configured);
    }

    private static Error MapSecureReadError(SecureFileReadStatus status) =>
        status switch
        {
            SecureFileReadStatus.NotFound =>
                new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage),
            SecureFileReadStatus.TooLarge =>
                new Error(ErrorCodes.Workspace.FileTooLarge, FileTooLargeMessage),
            SecureFileReadStatus.Rejected =>
                new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage),
            SecureFileReadStatus.IoError =>
                new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage),
            _ =>
                new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage),
        };

    /// <summary>
    /// Atomically replaces <paramref name="absolutePath"/> with <paramref name="contentBytes"/>, creating parent
    /// directories as needed. Mirrors the tier-2 handle-identity TOCTOU pattern used by the MCP sandboxed file
    /// tools (<c>SandboxedFileIo.TryWriteAllTextAtomicallyAsync</c>): the destination is revalidated for workspace
    /// containment before the parent directories are created (mkdir follows symlinks in the path prefix, so a
    /// symlinked ancestor would otherwise let a rejected write leave directories outside the root), again
    /// immediately before the temp file is created, and again (via post-move handle identity) after the atomic
    /// rename, closing the window between path resolution and the actual write.
    /// </summary>
    private static async Task<Result> WriteAtomicallyAsync(
        string workspaceRoot,
        string absolutePath,
        byte[] contentBytes,
        CancellationToken ct,
        FileContentBaseline? expectedExistingContent = null)
    {
        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {
            return new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage);
        }

        string? parentDir = Path.GetDirectoryName(absolutePath);

        if (!string.IsNullOrEmpty(parentDir))
        {
            try
            {
                Directory.CreateDirectory(parentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                return new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage);
            }
            catch (IOException)
            {
                return new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage);
            }
        }

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {
            return new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage);
        }

        string directory = parentDir ?? workspaceRoot;

        string tempPath = Path.Combine(directory, $".arcanum-{Guid.NewGuid():N}.tmp");

        FileHandleIdentity expectedIdentity = default;

        AtomicReplaceStatus replaceStatus;

        try
        {
            replaceStatus = await AtomicFile.ReplaceAsync(
                absolutePath,
                tempPath,
                async (stream, cancellationToken) =>
                {
                    await stream.WriteAsync(contentBytes, cancellationToken).ConfigureAwait(false);
                },
                ct,
                beforeReplace: () =>
                    WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath)
                        && FileHandleIdentityInterop.TryGetPathIdentity(tempPath, out expectedIdentity),
                afterReplace: () =>
                    TryVerifyMovedDestination(workspaceRoot, absolutePath, expectedIdentity),
                expectedDestinationContent: expectedExistingContent).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage);
        }
        catch (IOException)
        {
            return new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage);
        }

        return replaceStatus switch
        {
            AtomicReplaceStatus.Succeeded => Result.Success(),
            AtomicReplaceStatus.ReplacedButUnverified => new Error(
                ErrorCodes.Workspace.WriteFailed,
                "The file was replaced but post-move verification failed; the destination was left in an unverified state."),
            AtomicReplaceStatus.Aborted when IsLinkedDestination(absolutePath) => new Error(
                ErrorCodes.Workspace.SymbolicLinkEscape,
                LinkedDestinationMessage),
            AtomicReplaceStatus.Aborted when expectedExistingContent is not null => new Error(
                ErrorCodes.Workspace.WriteFailed,
                FileChangedDuringEditMessage),
            _ => new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage),
        };
    }

    /// <summary>
    /// Whether the destination is a symbolic link or a file with more than one hard link, the two states under
    /// which <see cref="AtomicFile.ReplaceAsync"/> refuses to touch it. Used only to give that refusal an
    /// accurate error instead of a generic I/O failure.
    /// </summary>
    private static bool IsLinkedDestination(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }

        return FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            && metadata.Kind == FileSystemObjectKind.RegularFile
            && metadata.HardLinkCount > 1;
    }

    /// <summary>
    /// Reports whether an existing destination begins with a UTF-8 preamble, so an overwrite can carry it
    /// across. The probe reuses the same handle-checked open as <c>ReplaceTextBlockAsync</c> — it proves the
    /// object is an unaliased regular file before opening, so a FIFO cannot park it — and answers "no BOM"
    /// for anything it cannot open. Nothing here decides the write: the destination is revalidated again,
    /// with handle identity, inside <see cref="WriteAtomicallyAsync"/>.
    /// </summary>
    private static bool DestinationStartsWithUtf8Bom(string workspaceRoot, string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return false;
        }

        (FileStream? probeStream, Error? _) = TryOpenForHandleCheckedRead(workspaceRoot, absolutePath);

        if (probeStream is null)
        {
            return false;
        }

        using (probeStream)
        {
            Span<byte> preamble = stackalloc byte[3];

            try
            {
                return probeStream.ReadAtLeast(preamble, preamble.Length, throwOnEndOfStream: false) == preamble.Length
                    && preamble.SequenceEqual(Utf8Bom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Confirms the just-moved destination's handle identity matches the temp file's pre-move identity and that
    /// the opened path is still under the workspace, closing the TOCTOU window between the atomic rename and this
    /// post-move check (mirrors <c>SandboxedFileIo</c>'s post-move verification).
    /// </summary>
    private static bool TryVerifyMovedDestination(string workspaceRoot, string absolutePath, FileHandleIdentity expectedIdentity)
    {
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
            return false;
        }

        using (verifyStream)
        {
            if (!WorkspacePathPolicy.IsOpenedHandleUnderWorkspace(workspaceRoot, verifyStream.SafeFileHandle))
            {
                return false;
            }

            if (!FileHandleIdentityInterop.TryGetHandleIdentity(verifyStream.SafeFileHandle, out FileHandleIdentity actualIdentity))
            {
                return false;
            }

            return FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity);
        }
    }

    /// <summary>
    /// Opens an existing file for a handle-checked read: captures the expected file identity from the resolved
    /// path, opens the file, then verifies the opened handle's identity matches. Closes the TOCTOU window between
    /// path resolution and the read used by <c>ReplaceTextBlockAsync</c>.
    /// </summary>
    private static (FileStream? Stream, Error? Error) TryOpenForHandleCheckedRead(string workspaceRoot, string absolutePath)
    {
        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, absolutePath, out string? resolvedFinalPath))
        {
            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        string identityPath = Path.GetFullPath(resolvedFinalPath ?? absolutePath);

        if (!FileHandleIdentityInterop.TryGetPathMetadata(identityPath, out FileHandleMetadata expectedMetadata))
        {
            return (null, new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage));
        }

        // Prove the object is a regular single-link file before opening it. A blocking open(2) on a
        // FIFO planted in the workspace never returns until a writer appears, and this FileStream
        // carries no CancellationToken, so the request would hang past RequestAborted and leak a
        // thread-pool thread per call. Mirrors PhysicalFileSystemBrowser.ReadAsync's guard.
        if (expectedMetadata.Kind != FileSystemObjectKind.RegularFile
            || expectedMetadata.HardLinkCount != 1)
        {
            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        FileHandleIdentity expectedIdentity = expectedMetadata.Identity;

        FileStream stream;

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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return (null, new Error(ErrorCodes.Workspace.AccessDenied, AccessDeniedMessage));
        }
        catch (IOException)
        {
            return (null, new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage));
        }

        if (!IsOpenedReadHandleContained(workspaceRoot, stream, expectedIdentity))
        {
            stream.Dispose();

            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        return (stream, null);
    }

    /// <summary>
    /// Post-open check for the replace path: the opened handle must be the pre-open identity and the kernel's
    /// path for it must lie under the workspace root. <see cref="FileStream.Name"/> is never consulted: it is
    /// only the string the stream was opened with, so an escaping link swapped in before the open (which also
    /// makes the captured identity the outside file's) would pass a check on it.
    /// </summary>
    internal static bool IsOpenedReadHandleContained(
        string workspaceRoot,
        FileStream stream,
        FileHandleIdentity expectedIdentity) =>
        FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actualIdentity)
        && FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity)
        && WorkspacePathPolicy.IsOpenedHandleUnderWorkspace(workspaceRoot, stream.SafeFileHandle);

    /// <summary>
    /// Walks the tree <see cref="DeleteRecursive"/> would delete (never following links) and returns the first
    /// entry whose canonical location leaves the workspace, or <see langword="null"/> when the whole tree is safe
    /// to delete.
    /// </summary>
    private static string? FindEscapingEntry(string workspaceRoot, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, path, out _))
        {
            return path;
        }

        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
        {
            // A file, or an in-workspace directory link that DeleteRecursive removes as a link without
            // traversing it.
            return null;
        }

        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            string? escaping = FindEscapingEntry(workspaceRoot, child, ct);

            if (escaping is not null)
            {
                return escaping;
            }
        }

        return null;
    }

    /// <summary>
    /// Recursively deletes <paramref name="path"/> and its contents. Each enumerated entry is revalidated with
    /// <see cref="WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck"/>; entries that escape the workspace
    /// via a symbolic link are skipped (left untouched) rather than followed, mirroring the recursive listing
    /// behavior in <see cref="PhysicalFileSystemBrowser"/>. Symlinks that stay inside the workspace are removed as
    /// links (never traversed into) to avoid following them into deletion of their targets.
    /// </summary>
    private static void DeleteRecursive(string workspaceRoot, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, path, out _))
        {
            return;
        }

        bool isDirectory = Directory.Exists(path);

        if (isDirectory)
        {
            bool isSymlink = new DirectoryInfo(path).LinkTarget is not null;

            if (isSymlink)
            {
                Directory.Delete(path, recursive: false);

                return;
            }

            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteRecursive(workspaceRoot, child, ct);
            }

            Directory.Delete(path, recursive: false);

            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        int count = 0;

        int index = 0;

        while (true)
        {
            int found = haystack.IndexOf(needle, index, StringComparison.Ordinal);

            if (found < 0)
            {
                break;
            }

            count++;

            index = found + needle.Length;
        }

        return count;
    }

    private static DateTimeOffset GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private const string FileWriteDisabledMessage =
        "Workspace file write is disabled. Set Arcanum:Workspaces:EnableFileWrite to true to enable this endpoint.";

    private const string AccessDeniedMessage = "Insufficient permissions to complete the operation.";

    private const string FileNotFoundMessage = "The file or directory was not found.";

    private const string FileTooLargeMessage = "The content exceeds the maximum file write size limit.";

    private const string ReplaceTextBlockTooLargeMessage = "The combined size of oldString and newString exceeds the maximum replace text block size limit.";

    private const string SymlinkEscapeMessage = "The path resolves outside the workspace via a symbolic link.";

    private const string LinkedDestinationMessage = "The destination is a symbolic link or has more than one hard link, so it cannot be written through this endpoint. Write to the real file instead.";

    private const string FileChangedDuringEditMessage = "The file changed after it was read, or its state could not be verified, so nothing was written. Re-read the file and retry.";

    private const string IoWriteErrorMessage = "An I/O error occurred while writing the file. See server logs.";

    private const string IoDeleteErrorMessage = "An I/O error occurred while deleting the file or directory. See server logs.";

    private const string DirectoryNotEmptyMessage = "The directory is not empty. Pass recursive=true to delete it along with its contents.";

    private const string ReplacementNotFoundMessage = "The specified text was not found in the file.";

    private const string ReplacementAmbiguousMessage = "The specified text was found a different number of times than expectedReplacements. Provide an expectedReplacements value matching the exact occurrence count.";

    private const string ProtectedPathMessage = "The path is protected workspace metadata (.git or .arcanum) and cannot be created, modified or deleted through the file API.";

    private const string PathIsDirectoryMessage = "The target path is an existing directory; file content cannot be written to it.";

    private const string PathIsFileMessage = "The target path is an existing file; a directory cannot be created there.";

    private const string InvalidUtf8Message = "The file is not valid UTF-8 text. This endpoint edits UTF-8 text files only.";

    private const string BinaryTargetMessage = "The file contains NUL bytes and is treated as binary. This endpoint edits UTF-8 text files only.";
}
