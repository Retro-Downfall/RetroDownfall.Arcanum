using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Performance;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ApiHost")]
public sealed class ArcanumWebApplicationFactoryTests
{

    [SkippableFact]
    public async Task Restartable_profile_preserves_catalog_files_and_credentials_across_hosts()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string tempHome;

        await using (RestartableArcanumProfileFixture profile = new())
        {

            tempHome = profile.TempHome;

            string databasePath = Path.Combine(tempHome, ".config", "arcanum", "arcanum.db");

            string sidecarPath = databasePath + ".kdf";

            Guid markerId;

            string installationIdentityAccount;

            byte[] installationIdentityBytes = [];

            try
            {

                await using (ArcanumWebApplicationFactory firstFactory = profile.CreateFactory())
                {

                    using IServiceScope firstScope = firstFactory.Services.CreateScope();

                    Assert.Same(
                        profile.CredentialStore,
                        firstScope.ServiceProvider.GetRequiredService<IOsCredentialStore>());

                    Assert.Same(
                        profile.PassphraseSource,
                        firstScope.ServiceProvider.GetRequiredService<IGrimoireDbPassphraseSource>());

                    ISessionRepository sessions = firstScope.ServiceProvider
                        .GetRequiredService<ISessionRepository>();

                    Session marker = await sessions.CreateAsync(
                        campaignId: null,
                        title: "issue-257-restart-survives",
                        CancellationToken.None);

                    markerId = marker.Id;

                    Assert.Equal(
                        OsCredentialStoreStatus.Ok,
                        profile.CredentialStore.Set(
                            "arcanum-tests",
                            "issue-257-restart",
                            "survives").Status);

                    Result<Guid> databaseIdentity = await firstScope.ServiceProvider
                        .GetRequiredService<IInstallationResetDatabaseIdentityReader>()
                        .ReadAsync(CancellationToken.None);

                    Assert.True(databaseIdentity.IsSuccess, databaseIdentity.Error.Message);

                    string grimoireDirectory = Path.Combine(tempHome, ".config", "arcanum");

                    Result<BackupRestoreProfileNamespace> profileNamespace =
                        BackupRestoreJournalAuthenticator.ResolveProfileNamespace(grimoireDirectory);

                    Assert.True(profileNamespace.IsSuccess, profileNamespace.Error.Message);

                    installationIdentityAccount =
                        ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
                            profileNamespace.Value.AccountSuffix);

                    OsCredentialStoreResult installationIdentity = profile.CredentialStore.TryGet(
                        ArcanumCredentialIdentity.Service,
                        installationIdentityAccount);

                    Assert.Equal(OsCredentialStoreStatus.Ok, installationIdentity.Status);

                    Assert.NotNull(installationIdentity.Value);

                    string installationIdentityValue = installationIdentity.Value;

                    installationIdentityBytes = Encoding.UTF8.GetBytes(installationIdentityValue);

                    Assert.True(
                        Guid.TryParseExact(
                            installationIdentityValue,
                            "D",
                            out Guid externalIdentity));

                    Assert.True(
                        databaseIdentity.Value == externalIdentity,
                        "The external installation identity does not match the database identity.");

                    installationIdentity = default;

                    installationIdentityValue = string.Empty;

                }

                AssertInstallationIdentityMatches(
                    profile.CredentialStore,
                    installationIdentityAccount,
                    installationIdentityBytes);

                Assert.True(Directory.Exists(tempHome));

                Assert.True(File.Exists(databasePath));

                Assert.True(File.Exists(sidecarPath));

                byte[] catalogBytes = await File.ReadAllBytesAsync(databasePath);

                byte[] sidecarBytes = await File.ReadAllBytesAsync(sidecarPath);

                Assert.NotEmpty(catalogBytes);

                Assert.NotEmpty(sidecarBytes);

                await using (ArcanumWebApplicationFactory secondFactory = profile.CreateFactory())
                {

                    Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(databasePath));

                    using IServiceScope secondScope = secondFactory.Services.CreateScope();

                    Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(databasePath));

                    Assert.Same(
                        profile.CredentialStore,
                        secondScope.ServiceProvider.GetRequiredService<IOsCredentialStore>());

                    Assert.Same(
                        profile.PassphraseSource,
                        secondScope.ServiceProvider.GetRequiredService<IGrimoireDbPassphraseSource>());

                    AssertInstallationIdentityMatches(
                        profile.CredentialStore,
                        installationIdentityAccount,
                        installationIdentityBytes);

                    ISessionRepository sessions = secondScope.ServiceProvider
                        .GetRequiredService<ISessionRepository>();

                    Session? retained = await sessions.GetByIdAsync(markerId, CancellationToken.None);

                    Assert.NotNull(retained);

                    Assert.Equal("issue-257-restart-survives", retained.Title);

                    OsCredentialStoreResult credential = profile.CredentialStore.TryGet(
                        "arcanum-tests",
                        "issue-257-restart");

                    Assert.Equal(OsCredentialStoreStatus.Ok, credential.Status);

                    Assert.Equal("survives", credential.Value);

                    Assert.Equal(
                        OsCredentialStoreStatus.Ok,
                        profile.CredentialStore.Delete(
                            "arcanum-tests",
                            "issue-257-restart").Status);

                }

                Assert.True(Directory.Exists(tempHome));

                Assert.NotEmpty(await File.ReadAllBytesAsync(databasePath));

                Assert.Equal(sidecarBytes, await File.ReadAllBytesAsync(sidecarPath));

            }
            finally
            {

                CryptographicOperations.ZeroMemory(installationIdentityBytes);

            }
        }

        Assert.False(Directory.Exists(tempHome));

    }

    private static void AssertInstallationIdentityMatches(
        IOsCredentialStore credentialStore,
        string account,
        ReadOnlySpan<byte> expected)
    {

        OsCredentialStoreResult current = credentialStore.TryGet(
            ArcanumCredentialIdentity.Service,
            account);

        Assert.Equal(OsCredentialStoreStatus.Ok, current.Status);

        Assert.NotNull(current.Value);

        byte[] actual = Encoding.UTF8.GetBytes(current.Value);

        current = default;

        try
        {

            Assert.True(
                CryptographicOperations.FixedTimeEquals(expected, actual),
                "The external installation-identity credential bytes changed.");

        }
        finally
        {

            CryptographicOperations.ZeroMemory(actual);

        }

    }

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
