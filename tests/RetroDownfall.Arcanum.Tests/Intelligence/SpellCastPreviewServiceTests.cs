using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SpellCastPreviewServiceTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Theory]
    [InlineData(true, "browse_web", true, false)]
    [InlineData(true, "mcp_tool", false, true)]
    [InlineData(false, "browse_web", false, false)]
    [InlineData(true, null, true, true)]
    public async Task CastAsync_ReportsCoherentAttunedBuiltinAndMcpTools(
        bool webBrowsingEnabled,
        string? declaredTool,
        bool expectBrowseWeb,
        bool expectMcpTool)
    {
        string spellName = $"preview-{Guid.NewGuid():N}";
        WriteSpell(spellName, declaredTool);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings
            {
                WebBrowsing = webBrowsingEnabled,
            },
        };
        FakeMcpConnectionManager mcp = new(
            AIFunctionFactory.Create(
                static () => "ok",
                "mcp_tool"));
        SpellCastPreviewService service = new(
            mcp,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<SpellCastPreviewService>.Instance);

        Result<SpellCastResult> result = await service.CastAsync(
            spellName,
            _workspace.Root,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Contains(
            ArcanumBuiltInToolNames.GetLocalSystemTime,
            result.Value.AvailableTools);
        Assert.Contains(
            ArcanumBuiltInToolNames.GetArcanumSystemInfo,
            result.Value.AvailableTools);
        Assert.Equal(
            expectBrowseWeb,
            result.Value.AvailableTools.Contains(
                ArcanumBuiltInToolNames.BrowseWeb,
                StringComparer.Ordinal));
        Assert.Equal(
            expectMcpTool,
            result.Value.AvailableTools.Contains(
                "mcp_tool",
                StringComparer.Ordinal));
    }

    private void WriteSpell(
        string spellName,
        string? declaredTool)
    {
        _workspace.WriteFile(
            $"spells/{spellName}/SPELL.md",
            $"""
             ---
             name: {spellName}
             description: Preview test spell
             ---
             body
             """);

        if (declaredTool is null)
        {
            return;
        }

        _workspace.WriteFile(
            $"spells/{spellName}/SKILL.json",
            $$"""
              {
                "name": "{{spellName}}",
                "version": "1.0.0",
                "description": "Preview test spell",
                "tags": [],
                "declaredTools": ["{{declaredTool}}"],
                "dependencies": []
              }
              """);
    }

    private sealed class FakeMcpConnectionManager(
        params AITool[] tools) : IMcpConnectionManager
    {
        public Task InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> StartAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(
            string name,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>(tools);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
