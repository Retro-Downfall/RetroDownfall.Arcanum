using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The distinct maintenance read handle used for pre-erasure catalog proof.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CovenantMaintenanceConnectionFactoryTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void Read_only_catalog_builder_is_keyed_private_unpooled_and_not_immutable()
    {

        string path = Path.Combine(Path.GetTempPath(), "catalog-proof.db");

        SqliteConnectionStringBuilder builder =
            CovenantMaintenanceConnectionFactory.ReadOnly(path, "catalog-proof-key");

        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);

        Assert.Equal(SqliteCacheMode.Private, builder.Cache);

        Assert.False(builder.Pooling);

        Assert.Equal("catalog-proof-key", builder.Password);

        Assert.Equal(path, builder.DataSource);

        Assert.DoesNotContain("immutable", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);

        Assert.NotEqual(
            CovenantMaintenanceConnectionFactory.SidecarFreeReadOnly(path, "catalog-proof-key")
                .DataSource,
            builder.DataSource);

    }

    [Fact]
    public async Task Open_read_only_returns_a_distinct_WAL_visible_handle_without_initializing_it()
    {

        await using CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await database.InstallHealthyCovenantCatalogAsync(withAccelerator: false, Token);

        ICovenantMaintenanceConnectionFactory factory = database.MaintenanceConnections();

        await using SqliteConnection connection = await factory.OpenReadOnlyAsync(Token);

        SqliteConnectionStringBuilder builder = new(connection.ConnectionString);

        Assert.NotSame(database.Connection, connection);

        Assert.Equal(ConnectionState.Open, connection.State);

        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);

        Assert.Equal(SqliteCacheMode.Private, builder.Cache);

        Assert.False(builder.Pooling);

        Assert.DoesNotContain("immutable", builder.DataSource, StringComparison.OrdinalIgnoreCase);

        // Initialization belongs to the owning guard. A factory that initialized here would make
        // the guard's exactly-once mode proof impossible to enforce.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {

                using CovenantSqliteAuthorizationScope _ =
                    CovenantSqliteConnectionInitializer.Instance.Authorize(
                        connection,
                        CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

                await Task.CompletedTask;

            });

    }

}
