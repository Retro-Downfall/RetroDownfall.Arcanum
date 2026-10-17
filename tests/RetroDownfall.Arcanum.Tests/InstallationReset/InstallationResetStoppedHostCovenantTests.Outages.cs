using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The Covenant authority a stopped-host plan establishes reports an outage as an outage: a catalog
/// that cannot be read right now is <c>Covenant.Unavailable</c> with the outage's remedy, and only a
/// catalog that was read and does not hold what it should is anything else.
/// </summary>
/// <remarks>
/// The outage is a table lock between two connections of one shared cache: the lease reads the state
/// table through a connection that is told, as a busy or locked database tells it, that the table is
/// locked, while the statement before it succeeds. That is the one way to fail a single statement of a
/// sequence on a real catalog without replacing the catalog.
/// </remarks>
public sealed partial class InstallationResetStoppedHostCovenantTests
{
    [SkippableFact]
    public async Task A_state_table_that_is_locked_when_the_facts_are_read_is_an_outage_and_not_a_missing_state()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature: true);

        await using ServiceProvider provider = Compose(covenantFeature: true).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        StoppedHostCovenantAuthority authority = scope.ServiceProvider.GetRequiredService<StoppedHostCovenantAuthority>();

        Result<HostProcessToolsRecoveryMarkerSnapshot> marker = authority.CaptureMarker();

        Assert.True(marker.IsSuccess, marker.IsFailure ? marker.Error.Message : null);

        await using SqliteConnection holder = await OpenSharedAsync();

        await using SqliteConnection lease = await OpenSharedAsync();

        // A locked table of a shared cache is waited for up to the command timeout before it is
        // reported; a second is as long as this case needs to hear it.
        lease.DefaultTimeout = 1;

        await ExecuteAsync(holder, "BEGIN;");

        await ExecuteAsync(holder, "UPDATE covenant_state SET StateKey = StateKey WHERE 0;");

        Result result = await authority.EnsureAsync(lease, marker.Value, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.True(
            result.Error.Code == ErrorCodes.Covenant.Unavailable,
            result.Error.Code + ": " + result.Error.Message);

        Assert.Contains(StoppedHostCovenantAuthority.OutageRemedy, result.Error.Message, StringComparison.Ordinal);

        await ExecuteAsync(holder, "ROLLBACK;");
    }

    [SkippableFact]
    public async Task A_catalog_without_its_state_table_is_still_a_missing_state_and_not_an_outage()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature: true);

        await using ServiceProvider provider = Compose(covenantFeature: true).BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        StoppedHostCovenantAuthority authority = scope.ServiceProvider.GetRequiredService<StoppedHostCovenantAuthority>();

        Result<HostProcessToolsRecoveryMarkerSnapshot> marker = authority.CaptureMarker();

        Assert.True(marker.IsSuccess, marker.IsFailure ? marker.Error.Message : null);

        await using SqliteConnection lease = await OpenSharedAsync();

        await ExecuteAsync(lease, "DROP TABLE covenant_state;");

        Result result = await authority.EnsureAsync(lease, marker.Value, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.InventoryUnavailable, result.Error.Code);

        Assert.Contains("holds no Covenant state tables", result.Error.Message, StringComparison.Ordinal);
    }

    private static async Task<SqliteConnection> OpenSharedAsync()
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = ArcanumPaths.GrimoireDatabaseFile,
            Password = StoppedHostResetTestSupport.FixturePassphrase,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(CancellationToken.None);

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
