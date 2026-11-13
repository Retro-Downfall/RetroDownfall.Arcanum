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

        private ProviderCredentialStore? _credentials;

        private GuildWorkspaceLeaseHostLock? _hostLock;

        private BlockingRefreshStream? _stream;

        private BlockingRefreshOperationStore? _operations;

        private Task<Result<GuildSpendRefreshResult>>? _running;

        private int _disposed;

        private LiveResetRefresh(SpendScenario scenario) => _scenario = scenario;

        internal static async Task<LiveResetRefresh> StartAsync(string connectionString, Guid sessionId, IGrimoireConnectionAdmissionGate gate,
            ICovenantConnectionDrain drain, bool startRefresh = true)
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

                scenario.Settings.Providers[0].CredentialEnvironmentVariable = "ARCANUM_TEST_REFRESH_" + Guid.NewGuid().ToString("N");

                GuildSpendRefreshConfiguration configuration = new(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(scenario.Settings));

                InMemoryOsCredentialStore os = new();

                os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), SpendSecret);

                ProviderCredentialStore credentials = result._credentials = new(os, new EphemeralDataProtectionProvider());

                SqliteConnection operationConnection = result._operationConnection = new(connectionString);

                ArcanumDbContext db = result._db = GrimoireFixture.CreateContext(operationConnection);

                LongRunningOperationStore inner = result._inner = new(db, fixture.Connections, drain);

                Assert.NotNull(await inner.GetAsync(current.Value.Quest.OperationId));

                BlockingRefreshOperationStore operations = result._operations = new(inner);

                string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-reset-" + Guid.NewGuid().ToString("N"));

                var locked = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

                Assert.True(locked.IsSuccess);

                GuildWorkspaceLeaseHostLock hostLock = result._hostLock = locked.Value;

                BlockingRefreshStream stream = result._stream = new();

                GuildSpendRefreshService service = new(fixture.Store, configuration, new ProviderApiKeyResolver(credentials),
                    new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(stream)),
                    new RefreshTestReadiness(hostLock), gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

                Task<Result<GuildSpendRefreshResult>>? running = result._running = startRefresh
                    ? service.RefreshAsync(fixture.Caller, scenario.QuestId, scenario.EffectId, Guid.NewGuid(), default)
                    : null;

                if (running is null) return result;

                await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

                await operations.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

                LongRunningOperation operation = Assert.IsType<LongRunningOperation>(await inner.GetAsync(operations.HeartbeatOperationId));

                Assert.True(await inner.TryTransitionAsync(operation.Id, operation.Revision, operation.LeaseOwner,
                    LongRunningOperationState.Completed, DateTimeOffset.UtcNow));

                return result;
            }
            catch
            {
                await result.DisposeAsync();

                throw;
            }
        }

        internal SqliteConnection OperationConnection => _operationConnection!;

        internal void ReleaseRead() => _stream?.ReleaseRead.TrySetResult();

        internal Task DisposalStarted => _stream!.Disposing.Task;

        internal void ReleaseDisposal() => _stream?.ReleaseDispose.TrySetResult();

        internal async Task WaitForHeartbeatStopAsync()
        {
            TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using CancellationTokenRegistration registration = _operations!.HeartbeatStop.Register(() => stopping.TrySetResult());

            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        internal bool HeartbeatFinished => _operations!.HeartbeatFinished.Task.IsCompleted;

        internal void ReleaseHeartbeat() => _operations?.ReleaseHeartbeat.TrySetResult();

        internal Task JoinAsync() => _running ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            ReleaseRead();

            ReleaseDisposal();

            ReleaseHeartbeat();

            try
            {
                if (_running is not null) await _running;
            }
            finally
            {
                _hostLock?.Dispose();

                _inner?.Dispose();

                if (_db is not null) await _db.DisposeAsync();

                if (_operationConnection is not null) await _operationConnection.DisposeAsync();

                _credentials?.Dispose();

                await _scenario.DisposeAsync();
            }
        }
    }
}
