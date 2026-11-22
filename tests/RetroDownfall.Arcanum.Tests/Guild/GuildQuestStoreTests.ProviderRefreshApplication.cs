using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private sealed class RefreshApplicationScenario(SpendScenario scenario, SqliteConnection connection, Guid refreshId)
        : IAsyncDisposable
    {
        internal SpendScenario Scenario { get; } = scenario;

        internal SqliteConnection Connection { get; } = connection;

        internal SqliteCommand Query { get; } = connection.CreateCommand();

        internal Guid QuestId => Scenario.QuestId;

        internal Guid RefreshId { get; } = refreshId;

        internal Guid OperationId { get; private set; }

        internal static async Task<RefreshApplicationScenario> CreateAsync(bool enableDailyBudget = false)
        {
            SpendScenario scenario = await SpendScenario.CreateAsync(enableDailyBudget);

            RefreshApplicationScenario fixture = new(scenario, await scenario.Fixture.OpenAsync(), Guid.NewGuid());

            try
            {
                fixture.Query.Parameters.AddWithValue("$effect", scenario.EffectId.ToString("D").ToUpperInvariant());

                fixture.Query.Parameters.AddWithValue("$refresh", fixture.RefreshId.ToString("D").ToUpperInvariant());

                fixture.OperationId = await HistoricalGuildRefreshFixture.SeedAsync(fixture.Connection, scenario.Fixture.Caller,
                    scenario.QuestId, scenario.EffectId, fixture.RefreshId);

                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();

                throw;
            }
        }

        internal Task<Guid> SeedAcceptedReceiptAsync() =>
            HistoricalGuildRefreshFixture.SeedAcceptedReceiptAsync(Connection, QuestId, Scenario.EffectId, RefreshId);

        public async ValueTask DisposeAsync()
        {
            await Query.DisposeAsync();

            await Connection.DisposeAsync();

            await Scenario.DisposeAsync();
        }
    }
}
