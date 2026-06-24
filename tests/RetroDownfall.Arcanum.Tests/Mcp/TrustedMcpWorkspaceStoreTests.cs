using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class TrustedMcpWorkspaceStoreTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string _storePath = string.Empty;

    private string? _backupStorePath;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _storePath = Path.Combine(ArcanumPaths.GrimoireDirectory, "trusted-mcp-workspaces.json");

        if (File.Exists(_storePath))
        {

            _backupStorePath = Path.Combine(_workspace.Root, "trusted-mcp-workspaces.json.bak");

            File.Copy(_storePath, _backupStorePath, overwrite: true);

            File.Delete(_storePath);

        }

    }

    public async Task DisposeAsync()
    {

        if (File.Exists(_storePath))
        {

            File.Delete(_storePath);

        }

        if (_backupStorePath is not null && File.Exists(_backupStorePath))
        {

            File.Copy(_backupStorePath, _storePath, overwrite: true);

        }

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task IsTrustedAsync_returns_false_when_mcp_json_missing()
    {

        using TrustedMcpWorkspaceStore store = new();

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.False(trusted);

    }

    [Fact]
    public async Task TrustAsync_then_IsTrustedAsync_returns_true_for_matching_hash()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.True(trusted);

        Assert.True(File.Exists(_storePath));

    }

    [Fact]
    public async Task TrustAsync_without_mcp_json_throws_FileNotFoundException()
    {

        using TrustedMcpWorkspaceStore store = new();

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.TrustAsync(_workspace.Root));

    }

    [Fact]
    public async Task IsTrustedAsync_returns_false_after_mcp_json_changes()
    {

        _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        _workspace.WriteFile("mcp.json", """{"mcpServers":{"x":{}}}""");

        bool trusted = await store.IsTrustedAsync(_workspace.Root);

        Assert.False(trusted);

    }

    [Fact]
    public async Task IsTrustedAsync_reuses_cached_hash_when_file_unchanged()
    {

        string mcpPath = _workspace.WriteFile("mcp.json", """{"mcpServers":{}}""");

        using TrustedMcpWorkspaceStore store = new();

        await store.TrustAsync(_workspace.Root);

        string firstHash = await ComputeSha256HexAsync(mcpPath);

        bool firstCheck = await store.IsTrustedAsync(_workspace.Root);

        bool secondCheck = await store.IsTrustedAsync(_workspace.Root);

        Assert.True(firstCheck);

        Assert.True(secondCheck);

        Assert.Equal(firstHash, await ComputeSha256HexAsync(mcpPath));

    }

    [Fact]
    public void NormalizeWorkspaceRoot_returns_full_path()
    {

        string normalized = TrustedMcpWorkspaceStore.NormalizeWorkspaceRoot(_workspace.Root);

        Assert.Equal(Path.GetFullPath(_workspace.Root), normalized);

    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {

        await using FileStream stream = File.OpenRead(path);

        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash);

    }

}
