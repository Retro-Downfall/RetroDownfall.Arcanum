using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Caching;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed class TrustedMcpWorkspaceStore : ITrustedMcpWorkspaceStore, IDisposable
{

    private const int McpFileHashCacheCapacity = 64;

    private static readonly SemaphoreSlim _storeLock = new(1, 1);

    private readonly BoundedLruCache<string, McpFileHashCacheEntry> _mcpFileHashCache = new(McpFileHashCacheCapacity);

    private static string StorePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "trusted-mcp-workspaces.json");

    public void Dispose()
    {
        // The store lock is static and shared across instances; do not dispose it.
    }

    public async Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default)
    {

        string normalized = NormalizeWorkspaceRoot(workspaceRootPath);

        string mcpPath = Path.Combine(normalized, "mcp.json");

        if (!File.Exists(mcpPath))
        {

            return false;

        }

        string currentHash = await ComputeFileSha256HexAsync(mcpPath, cancellationToken).ConfigureAwait(false);

        TrustedMcpWorkspaceDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);

        if (!document.Entries.TryGetValue(normalized, out string? storedHash))
        {

            return false;

        }

        return string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase);

    }

    public async Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default)
    {

        string normalized = NormalizeWorkspaceRoot(workspaceRootPath);

        string mcpPath = Path.Combine(normalized, "mcp.json");

        if (!File.Exists(mcpPath))
        {

            throw new FileNotFoundException("Workspace mcp.json was not found.", mcpPath);

        }

        string hash = await ComputeFileSha256HexAsync(mcpPath, cancellationToken).ConfigureAwait(false);

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            TrustedMcpWorkspaceDocument document = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            document.Entries[normalized] = hash;

            await SaveUnlockedAsync(document, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _storeLock.Release();

        }

    }

    internal static string NormalizeWorkspaceRoot(string workspaceRootPath)
    {

        return Path.GetFullPath(workspaceRootPath.Trim());

    }

    private async Task<TrustedMcpWorkspaceDocument> LoadAsync(CancellationToken cancellationToken)
    {

        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _storeLock.Release();

        }

    }

    private async Task<TrustedMcpWorkspaceDocument> LoadUnlockedAsync(CancellationToken cancellationToken)
    {

        if (!File.Exists(StorePath))
        {

            return new TrustedMcpWorkspaceDocument();

        }

        await using FileStream stream = File.OpenRead(StorePath);

        TrustedMcpWorkspaceDocument? document = await JsonSerializer.DeserializeAsync(
            stream,
            McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument,
            cancellationToken).ConfigureAwait(false);

        return document ?? new TrustedMcpWorkspaceDocument();

    }

    private async Task SaveUnlockedAsync(TrustedMcpWorkspaceDocument document, CancellationToken cancellationToken)
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await using FileStream stream = File.Create(StorePath);

        await JsonSerializer.SerializeAsync(
            stream,
            document,
            McpConfigJsonSerializerContext.Default.TrustedMcpWorkspaceDocument,
            cancellationToken).ConfigureAwait(false);

        SecureFilePermissions.ApplyOwnerOnlyFile(StorePath);

    }

    private async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {

        FileInfo fileInfo = new(path);

        if (!fileInfo.Exists)
        {

            throw new FileNotFoundException("Workspace mcp.json was not found.", path);

        }

        long lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;

        long length = fileInfo.Length;

        if (_mcpFileHashCache.TryGetValue(path, out McpFileHashCacheEntry cached)
            && cached.LastWriteUtcTicks == lastWriteUtcTicks
            && cached.Length == length)
        {

            return cached.Hash;

        }

        await using FileStream stream = fileInfo.OpenRead();

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        string hex = Convert.ToHexString(hash);

        _mcpFileHashCache.Set(path, new McpFileHashCacheEntry(lastWriteUtcTicks, length, hex));

        return hex;

    }

    private sealed record McpFileHashCacheEntry(long LastWriteUtcTicks, long Length, string Hash);

}
