using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

public sealed class TrustedMcpWorkspaceStore : ITrustedMcpWorkspaceStore, IDisposable
{

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static string StorePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "trusted-mcp-workspaces.json");

    public void Dispose() => _fileLock.Dispose();

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

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            TrustedMcpWorkspaceDocument document = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            document.Entries[normalized] = hash;

            await SaveUnlockedAsync(document, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _fileLock.Release();

        }

    }

    internal static string NormalizeWorkspaceRoot(string workspaceRootPath)
    {

        return Path.GetFullPath(workspaceRootPath.Trim());

    }

    private async Task<TrustedMcpWorkspaceDocument> LoadAsync(CancellationToken cancellationToken)
    {

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _fileLock.Release();

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

        ApplyRestrictiveUnixFileMode(StorePath);

    }

    private static void ApplyRestrictiveUnixFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort — trust store remains protected by OS user account isolation.
        }
    }

    private static async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {

        await using FileStream stream = File.OpenRead(path);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash);

    }

}
