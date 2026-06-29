using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerTransportFactoryTests
{

    [Fact]
    public async Task StartAsync_sse_server_returns_sse_not_supported()
    {

        await using McpConnectionManager manager = CreateManager(new ArcanumSettings());

        await manager.RegisterFromConfigAsync(
            Config("s", new McpServerConfig { Type = "sse", Url = "https://example.com/rpc" }),
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result result = await manager.StartAsync("s", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.SseNotSupported", result.Error.Code);

    }

    [Fact]
    public async Task StartAsync_http_loopback_url_is_blocked_by_ssrf_policy()
    {

        await using McpConnectionManager manager = CreateManager(new ArcanumSettings());

        await manager.RegisterFromConfigAsync(
            Config("h", new McpServerConfig { Url = "https://127.0.0.1/rpc" }),
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result result = await manager.StartAsync("h", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.BlockedUrl", result.Error.Code);

    }

    [Fact]
    public async Task StartAsync_http_plaintext_host_not_allowlisted_is_refused()
    {

        await using McpConnectionManager manager = CreateManager(new ArcanumSettings());

        await manager.RegisterFromConfigAsync(
            Config("h", new McpServerConfig { Url = "http://mcp.example.com/rpc" }),
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result result = await manager.StartAsync("h", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.InsecureUrl", result.Error.Code);

    }

    [Fact]
    public async Task StartAsync_http_plaintext_allowlisted_host_still_blocked_by_ssrf_when_loopback()
    {

        ArcanumSettings settings = new()
        {
            Mcp = new McpSettings { AllowedHttpHosts = ["127.0.0.1"] },
        };

        await using McpConnectionManager manager = CreateManager(settings);

        await manager.RegisterFromConfigAsync(
            Config("h", new McpServerConfig { Url = "http://127.0.0.1/rpc" }),
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result result = await manager.StartAsync("h", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.BlockedUrl", result.Error.Code);

    }

    [Fact]
    public async Task StartAsync_stdio_server_takes_subprocess_path_and_fails_on_missing_binary()
    {

        await using McpConnectionManager manager = CreateManager(new ArcanumSettings());

        await manager.RegisterFromConfigAsync(
            Config("s", new McpServerConfig { Command = "arcanum-nonexistent-binary-zzz" }),
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result result = await manager.StartAsync("s", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Mcp.StartFailed", result.Error.Code);

    }

    private static McpConfig Config(string name, McpServerConfig server) =>
        new()
        {
            McpServers = new Dictionary<string, McpServerConfig> { [name] = server },
        };

    private static McpConnectionManager CreateManager(ArcanumSettings settings)
    {

        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            new HumanPromptRegistry(),
            scopeFactory,
            pacer,
            new FakeEventBus(),
            new UntrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings));

    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
