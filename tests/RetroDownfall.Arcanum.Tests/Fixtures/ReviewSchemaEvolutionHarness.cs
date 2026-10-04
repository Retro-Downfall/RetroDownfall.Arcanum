using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal static class ReviewSchemaEvolutionHarness
{
    internal static async Task UpgradeAsync(SqliteConnection connection)
    {
        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            1536,
            CancellationToken.None);

        ServiceCollection collection = new();
        _ = collection.AddOptions();
        _ = collection.Configure<ArcanumSettings>(static _ => { });

        await using ServiceProvider services = collection.BuildServiceProvider();

        GrimoireSchemaInstaller installer =
            GrimoireSchemaTestInstaller.Create(GrimoireSchemaVersionChains.Default);

        GrimoireSchemaTransitionCoordinator coordinator = new(
            new FixedConnectionSource(connection),
            GrimoireSchemaVersionChains.Default,
            installer,
            new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
            services,
            new CovenantAvailability(new CovenantRuntimeGenerationProvider()),
            TimeProvider.System);

        for (int pass = 0; pass < 20; pass++)
        {
            Result<GrimoireSchemaTransitionPassOutcome> result =
                await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            if (!result.Value.Advanced)
            {
                return;
            }
        }

        Assert.Fail("The review schema transitions did not drain.");
    }

    private sealed class FixedConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);
    }
}
