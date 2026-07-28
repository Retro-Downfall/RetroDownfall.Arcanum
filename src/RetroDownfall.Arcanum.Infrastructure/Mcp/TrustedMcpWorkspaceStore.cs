using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed class TrustedMcpWorkspaceStore : ITrustedMcpWorkspaceStore, IDisposable
{

    internal const int MaxTrustDocumentBytes = 8 * 1024 * 1024;

    internal const int MaxTrustDocumentEntries = 256;

    internal const int MaxNormalizedWorkspacePathChars = 4096;

    internal const int Sha256HexLength = 64;

    private static readonly SemaphoreSlim _storeLock = new(1, 1);

    private static string StorePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "trusted-mcp-workspaces.json");

    public void Dispose()
    {
        // The store lock is static and shared across instances; do not dispose it.
    }

    public async Task<bool> IsTrustedAsync(
        string workspaceRootPath,
        CancellationToken cancellationToken = default)
    {

        TrustedMcpWorkspaceSnapshot snapshot =
            await GetSnapshotAsync(workspaceRootPath, cancellationToken)
                .ConfigureAwait(false);

        return snapshot.IsApproved;
    }

    public async Task<bool> IsTrustedAsync(
        string workspaceRootPath,
        string sourceDigest,
        CancellationToken cancellationToken = default)
    {

        if (!IsValidSha256Hex(sourceDigest))
        {
            return false;
        }

        TrustedMcpWorkspaceSnapshot snapshot =
            await GetSnapshotAsync(workspaceRootPath, cancellationToken)
                .ConfigureAwait(false);

        return snapshot.Authorizes(sourceDigest);

    }

    public async Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
        string workspaceRootPath,
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeWorkspaceRoot(workspaceRootPath, out string normalized))
        {
            return default;
        }

        string? currentDigest = await TryComputeCurrentDigestAsync(
                normalized,
                cancellationToken)
            .ConfigureAwait(false);

        if (currentDigest is null)
        {
            return default;
        }

        bool approved = await IsApprovedDigestNormalizedAsync(
                normalized,
                currentDigest,
                cancellationToken)
            .ConfigureAwait(false);

        return new TrustedMcpWorkspaceSnapshot(currentDigest, approved);

    }

    public async Task<bool> IsApprovedDigestAsync(
        string workspaceRootPath,
        string sourceDigest,
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeWorkspaceRoot(workspaceRootPath, out string normalized)
            || !IsValidSha256Hex(sourceDigest))
        {
            return false;
        }

        return await IsApprovedDigestNormalizedAsync(
                normalized,
                sourceDigest,
                cancellationToken)
            .ConfigureAwait(false);

    }

    public async Task TrustAsync(
        string workspaceRootPath,
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string normalized;

        try
        {
            normalized = NormalizeWorkspaceRoot(workspaceRootPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or PathTooLongException
                or NotSupportedException)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "The workspace path is invalid or exceeds the trust safety limit.",
                ex);
        }

        McpFileDigestResult digestResult;

        try
        {
            digestResult = await ReadCurrentDigestAsync(
                    normalized,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "Workspace mcp.json could not be read. Verify access and retry.",
                ex);
        }

        if (digestResult.Status is McpFileDigestStatus.NotFound)
        {
            throw new FileNotFoundException("Workspace mcp.json was not found.");
        }

        if (digestResult.Status is McpFileDigestStatus.TooLarge)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "Workspace mcp.json exceeds the maximum trusted configuration size. Reduce it and retry.");
        }

        if (digestResult.Status is McpFileDigestStatus.Invalid)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "Workspace mcp.json could not be validated as a safe regular file.");
        }

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            TrustedStoreLoadResult loadResult =
                await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            if (!loadResult.IsValid)
            {
                throw InvalidStoreException();
            }

            TrustedMcpWorkspaceDocument document = loadResult.Document!;

            if (!document.Entries.ContainsKey(normalized)
                && document.Entries.Count >= MaxTrustDocumentEntries)
            {
                throw new TrustedMcpWorkspaceStoreException(
                    "The MCP approval store reached its workspace limit. Remove stale approvals and retry.");
            }

            document.Entries[normalized] = digestResult.Digest!;

            await SaveUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storeLock.Release();
        }

    }

    internal static string NormalizeWorkspaceRoot(string workspaceRootPath)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRootPath);

        string trimmed = workspaceRootPath.Trim();

        if (trimmed.Length > MaxNormalizedWorkspacePathChars)
        {
            throw new ArgumentException(
                "Workspace path exceeds the internal safety limit.",
                nameof(workspaceRootPath));
        }

        string normalized = Path.GetFullPath(trimmed);

        if (normalized.Length > MaxNormalizedWorkspacePathChars)
        {
            throw new ArgumentException(
                "Workspace path exceeds the internal safety limit.",
                nameof(workspaceRootPath));
        }

        return normalized;

    }

    private static async Task<bool> IsApprovedDigestNormalizedAsync(
        string normalizedWorkspaceRoot,
        string sourceDigest,
        CancellationToken cancellationToken)
    {

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            TrustedStoreLoadResult loadResult =
                await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            if (!loadResult.IsValid
                || !loadResult.Document!.Entries.TryGetValue(
                    normalizedWorkspaceRoot,
                    out string? storedDigest))
            {
                return false;
            }

            return string.Equals(
                storedDigest,
                sourceDigest,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _storeLock.Release();
        }

    }

    private static async Task<TrustedStoreLoadResult> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {

        SecureFileReadResult readResult;

        try
        {
            readResult = await SecureFileReader
                .ReadBytesAsync(
                    StorePath,
                    MaxTrustDocumentBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrustedStoreLoadResult.Invalid;
        }

        using (readResult)
        {
            if (readResult.Status is SecureFileReadStatus.NotFound)
            {
                return TrustedStoreLoadResult.Valid(new TrustedMcpWorkspaceDocument());
            }

            if (readResult.Status is not SecureFileReadStatus.Success)
            {
                return TrustedStoreLoadResult.Invalid;
            }

            try
            {
                TrustedMcpWorkspaceDocument? document = JsonSerializer.Deserialize(
                    readResult.Bytes.Span,
                    McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument);

                return document is not null && IsValidDocument(document)
                    ? TrustedStoreLoadResult.Valid(document)
                    : TrustedStoreLoadResult.Invalid;
            }
            catch (JsonException)
            {
                return TrustedStoreLoadResult.Invalid;
            }
        }

    }

    private static async Task SaveUnlockedAsync(
        TrustedMcpWorkspaceDocument document,
        CancellationToken cancellationToken)
    {

        if (!IsValidDocument(document))
        {
            throw InvalidStoreException();
        }

        byte[] serialized;

        try
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(
                document,
                McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "The MCP approval store could not be serialized safely.",
                ex);
        }

        if (serialized.Length > MaxTrustDocumentBytes)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "The MCP approval store exceeds its serialized size limit. Remove stale approvals and retry.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(
                    ArcanumPaths.GrimoireDirectory))
            {
                throw new TrustedMcpWorkspaceStoreException(
                    "Could not secure MCP approval-store directory permissions. Verify storage permissions and retry.");
            }

            string tempPath = Path.Combine(
                ArcanumPaths.GrimoireDirectory,
                $".trusted-mcp-workspaces.{Guid.NewGuid():N}.tmp");

            AtomicReplaceStatus replaceStatus = await AtomicFile.ReplaceAsync(
                    StorePath,
                    tempPath,
                    async (stream, ct) =>
                    {
                        if (!SecureFilePermissions.TryApplyOwnerOnlyFileStrict(tempPath))
                        {
                            throw new TrustedMcpWorkspaceStoreException(
                                "Could not secure temporary MCP approval-store permissions.");
                        }

                        await stream.WriteAsync(serialized, ct).ConfigureAwait(false);
                    },
                    cancellationToken,
                    afterReplace: () =>
                        SecureFilePermissions.TryApplyOwnerOnlyFileStrict(StorePath))
                .ConfigureAwait(false);

            if (replaceStatus is not AtomicReplaceStatus.Succeeded)
            {
                throw new TrustedMcpWorkspaceStoreException(
                    "Could not safely update the MCP approval store. Verify storage permissions and retry.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TrustedMcpWorkspaceStoreException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or CryptographicException)
        {
            throw new TrustedMcpWorkspaceStoreException(
                "Could not safely update the MCP approval store. Verify storage permissions and retry.",
                ex);
        }

    }

    private static async Task<string?> TryComputeCurrentDigestAsync(
        string normalizedWorkspaceRoot,
        CancellationToken cancellationToken)
    {

        try
        {
            McpFileDigestResult result =
                await ReadCurrentDigestAsync(
                        normalizedWorkspaceRoot,
                        cancellationToken)
                    .ConfigureAwait(false);

            return result.Status is McpFileDigestStatus.Success
                ? result.Digest
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

    }

    private static async Task<McpFileDigestResult> ReadCurrentDigestAsync(
        string normalizedWorkspaceRoot,
        CancellationToken cancellationToken)
    {

        string mcpPath = Path.Combine(normalizedWorkspaceRoot, "mcp.json");

        SecureFileReadResult readResult = await SecureFileReader
            .ReadBytesAsync(
                mcpPath,
                McpSecurityLimits.MaxMcpConfigBytes,
                cancellationToken)
            .ConfigureAwait(false);

        using (readResult)
        {
            return readResult.Status switch
            {
                SecureFileReadStatus.NotFound => McpFileDigestResult.NotFound,
                SecureFileReadStatus.TooLarge => McpFileDigestResult.TooLarge,
                SecureFileReadStatus.Success => McpFileDigestResult.Success(
                    Convert.ToHexString(SHA256.HashData(readResult.Bytes.Span))),
                _ => McpFileDigestResult.Invalid,
            };
        }

    }

    private static bool TryNormalizeWorkspaceRoot(
        string workspaceRootPath,
        out string normalized)
    {

        try
        {
            normalized = NormalizeWorkspaceRoot(workspaceRootPath);

            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or PathTooLongException
                or NotSupportedException)
        {
            normalized = string.Empty;

            return false;
        }

    }

    private static bool IsValidDocument(TrustedMcpWorkspaceDocument document)
    {

        if (document.Entries is null
            || document.Entries.Count > MaxTrustDocumentEntries)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in document.Entries)
        {
            if (!IsNormalizedStoredPath(entry.Key)
                || !IsValidSha256Hex(entry.Value))
            {
                return false;
            }
        }

        return true;

    }

    private static bool IsNormalizedStoredPath(string path)
    {

        if (string.IsNullOrWhiteSpace(path)
            || path.Length > MaxNormalizedWorkspacePathChars)
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizeWorkspaceRoot(path),
                path,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or PathTooLongException
                or NotSupportedException)
        {
            return false;
        }

    }

    private static bool IsValidSha256Hex(string? digest)
    {

        if (digest is null || digest.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (char value in digest)
        {
            if (!Uri.IsHexDigit(value))
            {
                return false;
            }
        }

        return true;

    }

    private static TrustedMcpWorkspaceStoreException InvalidStoreException() =>
        new(
            "The MCP approval store is corrupt or exceeds safety limits. "
            + "Remove it and approve the workspace again.");

    private readonly record struct TrustedStoreLoadResult(
        bool IsValid,
        TrustedMcpWorkspaceDocument? Document)
    {

        public static TrustedStoreLoadResult Invalid { get; } =
            new(false, null);

        public static TrustedStoreLoadResult Valid(
            TrustedMcpWorkspaceDocument document) =>
            new(true, document);

    }

    private readonly record struct McpFileDigestResult(
        McpFileDigestStatus Status,
        string? Digest)
    {

        public static McpFileDigestResult NotFound { get; } =
            new(McpFileDigestStatus.NotFound, null);

        public static McpFileDigestResult TooLarge { get; } =
            new(McpFileDigestStatus.TooLarge, null);

        public static McpFileDigestResult Invalid { get; } =
            new(McpFileDigestStatus.Invalid, null);

        public static McpFileDigestResult Success(string digest) =>
            new(McpFileDigestStatus.Success, digest);

    }

    private enum McpFileDigestStatus
    {
        Success,
        NotFound,
        TooLarge,
        Invalid,
    }

}

internal sealed class TrustedMcpWorkspaceStoreException : InvalidOperationException
{

    public TrustedMcpWorkspaceStoreException(string message)
        : base(message)
    {
    }

    public TrustedMcpWorkspaceStoreException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }

}
