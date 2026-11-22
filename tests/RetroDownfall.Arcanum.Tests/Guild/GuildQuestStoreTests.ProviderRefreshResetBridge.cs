using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    internal sealed class LiveResetRefresh : IAsyncDisposable
    {
        private readonly SpendScenario _scenario;

        private SqliteConnection? _operationConnection;

        private ArcanumDbContext? _db;

        private LongRunningOperationStore? _inner;

        private int _disposed;

        private LiveResetRefresh(SpendScenario scenario) => _scenario = scenario;

        internal static async Task<LiveResetRefresh> StartAsync(string connectionString, Guid sessionId, ICovenantConnectionDrain drain)
        {
            Fixture fixture = Fixture.ForExistingDatabase(connectionString, sessionId);

            SpendScenario scenario = await SpendScenario.CreateAsync(fixture: fixture);

            LiveResetRefresh result = new(scenario);

            try
            {
                var resolved = await fixture.Store.RecordDispatchEvidenceAsync(scenario.Driver,
                    new(scenario.DispatchId, 2, GuildDispatchEvidence.Confirmed, scenario.ApprenticeId), default);

                Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : null);

                var current = await fixture.Store.GetAsync(fixture.Caller, scenario.QuestId, default);

                Assert.True(current.IsSuccess, current.IsFailure ? current.Error.Message : null);

                var committed = await fixture.Store.CommitResultAsync(scenario.Driver, new(Guid.NewGuid(), ReceiptDigest("live-reset-commit")),
                    current.Value.Quest.Revision, ReceiptResult(scenario.QuestId) with { WorkStoppedProven = true }, default);

                Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : null);

                var acknowledged = await fixture.Store.AcknowledgeRootResultAsync(fixture.Caller, scenario.QuestId,
                    committed.Value.ResultId, committed.Value.CanonicalContentDigest, 1, default);

                Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : null);

                GuildPortalStore portal = new(fixture.Store, fixture.Authority, fixture.Host, TimeProvider.System);

                var drained = await portal.DrainOutboxAsync(fixture.Caller, scenario.QuestId, default);

                Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

                List<Guid> envelopes = [];

                await using (SqliteCommand command = scenario.Connection.CreateCommand())
                {
                    command.CommandText = "SELECT EnvelopeId FROM guild_inbox WHERE DeliveryStateCode=1";

                    await using SqliteDataReader reader = await command.ExecuteReaderAsync();

                    while (await reader.ReadAsync()) envelopes.Add(Guid.Parse(reader.GetString(0)));
                }

                foreach (Guid envelope in envelopes)
                {
                    var applied = await portal.AcknowledgeOperatorAsync(fixture.Caller, envelope, default);

                    Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : null);
                }

                // This test-only connection is not an admission witness and must not pin replacement.
                await scenario.Connection.CloseAsync();

                SqliteConnection operationConnection = result._operationConnection = new(connectionString);

                ArcanumDbContext db = result._db = GrimoireFixture.CreateContext(operationConnection);

                LongRunningOperationStore inner = result._inner = new(db, fixture.Connections, drain);

                Assert.NotNull(await inner.GetAsync(current.Value.Quest.OperationId));

                return result;
            }
            catch
            {
                await result.DisposeAsync();

                throw;
            }
        }

        internal SqliteConnection OperationConnection => _operationConnection!;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _inner?.Dispose();

            if (_db is not null) await _db.DisposeAsync();

            if (_operationConnection is not null) await _operationConnection.DisposeAsync();

            await _scenario.DisposeAsync();
        }
    }
}
