using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ApiHost")]
public sealed class ArcanumWebApplicationFactoryTests
{

    [Fact]
    public async Task Constructor_redirects_persistent_paths_to_temp_home()
    {

        await using ArcanumWebApplicationFactory factory = new();

        string expected = Path.Combine(factory.TempHome, ".config", "arcanum");

        Assert.Equal(expected, ArcanumPaths.GrimoireDirectory);

        Assert.Equal(expected, ArcanumPaths.SecretStoreDirectory);

    }

    [SkippableFact]
    public async Task Started_test_host_does_not_create_pid_file()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        string pidPath = Path.Combine(factory.TempHome, ".config", "arcanum", "arcanum.pid");

        Assert.False(File.Exists(pidPath));

    }

    [SkippableFact]
    public async Task Bootstrap_loads_only_testing_isolated_global_mcp_config()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        const string isolatedServerName = "isolated-global-config";

        string configPath = Path.Combine(factory.TempHome, ".config", "arcanum", "mcp.json");

        McpConfig config = new()
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [isolatedServerName] = new()
                {
                    Command = "must-not-start",
                    AlwaysOn = false,
                },
            },
        };

        await File.WriteAllTextAsync(
            configPath,
            System.Text.Json.JsonSerializer.Serialize(
                config,
                McpConfigJsonSerializerContext.Default.McpConfig));

        using HttpClient client = factory.CreateAuthenticatedClient();

        IMcpConnectionManager manager = factory.Services.GetRequiredService<IMcpConnectionManager>();

        McpServerInfo status = Assert.Single(await manager.GetAllStatusesAsync());

        Assert.Equal(isolatedServerName, status.Name);

        Assert.Equal(McpServerState.Stopped, status.State);

    }

}
