using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Performance;

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

    [Fact]
    public void Every_factory_test_that_mutates_process_environment_is_serialized()
    {
        CustomAttributeData collection = Assert.Single(
            typeof(ArcanumPerfBaselineTests).GetCustomAttributesData(),
            static attribute =>
                attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal("ApiHost", collection.ConstructorArguments[0].Value);
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
    public async Task Started_host_keeps_factory_database_when_process_test_home_changes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateAuthenticatedClient();
        string? originalHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
        string otherHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"other-api-host-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(otherHome, ".config", "arcanum"));
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", otherHome);

            using IServiceScope scope = factory.Services.CreateScope();
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
            string dataSource = new SqliteConnectionStringBuilder(
                db.Database.GetDbConnection().ConnectionString).DataSource;

            Assert.Equal(
                Path.Combine(factory.TempHome, ".config", "arcanum", "arcanum.db"),
                dataSource);
            Assert.True(await db.Database.CanConnectAsync());
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                originalHome);

            if (Directory.Exists(otherHome))
            {
                Directory.Delete(otherHome, recursive: true);
            }
        }
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
