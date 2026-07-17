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

        if (Directory.Exists(resolvedPath))
        {
            return new Error(ErrorCodes.Workspace.PathIsDirectory, PathIsDirectoryMessage);
        }

        byte[] contentBytes = Encoding.UTF8.GetBytes(content);

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

        try
        {

            await using (readStream)
            {

                using MemoryStream buffer = new();

                await readStream.CopyToAsync(buffer, ct).ConfigureAwait(false);

                byte[] bytes = buffer.ToArray();

                hadBom = bytes.Length >= Utf8Bom.Length && bytes.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom);

                byte[] textBytes = hadBom ? bytes[Utf8Bom.Length..] : bytes;

                text = Encoding.UTF8.GetString(textBytes);

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

        Result writeResult = await WriteAtomicallyAsync(workspaceRoot, resolvedPath, outputBytes, ct).ConfigureAwait(false);

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

        ArcanumSettings settings = options.Value;

        long configured = settings.Workspaces?.MaxFileWriteSizeBytes ?? new WorkspaceSettings().MaxFileWriteSizeBytes;

        return ArcanumSettingClamps.MaxFileWriteSizeBytes(configured);
    }

    private long GetMaxReplaceTextBlockBytes()
    {

        ArcanumSettings settings = options.Value;

        long configured = settings.Workspaces?.MaxReplaceTextBlockBytes ?? new WorkspaceSettings().MaxReplaceTextBlockBytes;

        return ArcanumSettingClamps.MaxReplaceTextBlockBytes(configured);
    }

    /// <summary>
    /// Atomically replaces <paramref name="absolutePath"/> with <paramref name="contentBytes"/>, creating parent
    /// directories as needed. Mirrors the tier-2 handle-identity TOCTOU pattern used by the MCP sandboxed file
    /// tools (<c>SandboxedFileIo.TryWriteAllTextAtomicallyAsync</c>): the destination is revalidated for workspace
    /// containment immediately before the temp file is created and again (via post-move handle identity) after
    /// the atomic rename, closing the window between path resolution and the actual write.
    /// </summary>
    private static async Task<Result> WriteAtomicallyAsync(
        string workspaceRoot,
        string absolutePath,
        byte[] contentBytes,
        CancellationToken ct)
    {

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
                    TryVerifyMovedDestination(workspaceRoot, absolutePath, expectedIdentity)).ConfigureAwait(false);

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
            _ => new Error(ErrorCodes.Workspace.WriteFailed, IoWriteErrorMessage),
        };
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

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, Path.GetFullPath(verifyStream.Name), out _))
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

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(workspaceRoot, absolutePath))
        {
            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, absolutePath, out string? resolvedFinalPath))
        {
            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        string identityPath = Path.GetFullPath(resolvedFinalPath ?? absolutePath);

        if (!FileHandleIdentityInterop.TryGetPathIdentity(identityPath, out FileHandleIdentity expectedIdentity))
        {
            return (null, new Error(ErrorCodes.Workspace.FileNotFound, FileNotFoundMessage));
        }

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

        if (!FileHandleIdentityInterop.TryGetHandleIdentity(stream.SafeFileHandle, out FileHandleIdentity actualIdentity)
            || !FileHandleIdentity.IdentitiesMatch(expectedIdentity, actualIdentity))
        {

            stream.Dispose();

            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        string openedFullPath;

        try
        {
            openedFullPath = Path.GetFullPath(stream.Name);
        }
        catch (Exception)
        {

            stream.Dispose();

            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, openedFullPath, out _))
        {

            stream.Dispose();

            return (null, new Error(ErrorCodes.Workspace.SymbolicLinkEscape, SymlinkEscapeMessage));
        }

        return (stream, null);
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

    private const string IoWriteErrorMessage = "An I/O error occurred while writing the file. See server logs.";

    private const string IoDeleteErrorMessage = "An I/O error occurred while deleting the file or directory. See server logs.";

    private const string DirectoryNotEmptyMessage = "The directory is not empty. Pass recursive=true to delete it along with its contents.";

    private const string ReplacementNotFoundMessage = "The specified text was not found in the file.";

    private const string ReplacementAmbiguousMessage = "The specified text was found a different number of times than expectedReplacements. Provide an expectedReplacements value matching the exact occurrence count.";

    private const string PathIsDirectoryMessage = "The target path is an existing directory; file content cannot be written to it.";

    private const string PathIsFileMessage = "The target path is an existing file; a directory cannot be created there.";

}
