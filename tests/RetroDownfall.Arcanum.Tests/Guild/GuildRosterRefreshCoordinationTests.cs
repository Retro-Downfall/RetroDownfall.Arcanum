using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRosterRefreshCoordinationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "guild-refresh-scope-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Independently_scoped_refreshes_replay_before_observing_a_changed_source()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await InstallAsync(file);

        var connections = new FixtureOrdinaryConnectionFactory(file.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        var blocked = new AcceptanceConnectionFactory(connections, refuse: false);

        ServiceCollection services = new();

        services.AddArcanumGrimoireForCli();

        services.AddSingleton<IGrimoireConnectionAdmissionGate>(gate);

        int scopeNumber = 0;

        services.AddScoped<IGrimoireOrdinaryConnectionFactory>(_ => Interlocked.Increment(ref scopeNumber) == 1 ? blocked : connections);

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();

        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        GuildRosterRefreshStore firstRefreshes = firstScope.ServiceProvider.GetRequiredService<GuildRosterRefreshStore>();

        GuildRosterRefreshStore secondRefreshes = secondScope.ServiceProvider.GetRequiredService<GuildRosterRefreshStore>();

        Assert.NotSame(firstRefreshes, secondRefreshes);

        Guid generation = Guid.NewGuid();

        var caller = new GuildCallerContext(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), generation);

        GuildRoster firstRoster = Roster(connections, gate, firstRefreshes, generation);

        GuildRoster secondRoster = Roster(connections, gate, secondRefreshes, generation);

        await SourceAsync("Original");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        Task<Result<GuildRefreshReceipt>> first = firstRoster.RefreshAsync(caller, firstRoster.CreateRefreshMutation(request), request, CancellationToken.None);

        try
        {
            await blocked.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await SourceAsync("Changed while first acceptance is pending");

            // Drive the unsafe interleaving to completion when separate scope gates permit it.
            // With shared coordination the second request waits until the first receipt commits.
            bool independentGate = secondRefreshes.RefreshGate.Wait(0);

            if (independentGate)
            {
                secondRefreshes.RefreshGate.Release();
            }

            Task<Result<GuildRefreshReceipt>> second = secondRoster.RefreshAsync(caller, secondRoster.CreateRefreshMutation(request), request, CancellationToken.None);

            if (independentGate)
            {
                _ = await second.WaitAsync(TimeSpan.FromSeconds(10));
            }

            blocked.Release.TrySetResult();

            Result<GuildRefreshReceipt> accepted = await first;

            Result<GuildRefreshReceipt> replay = await second;

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.False(accepted.Value.Receipt.Replayed);

            Assert.True(replay.Value.Receipt.Replayed);

            Assert.Equal(accepted.Value.Receipt.Id, replay.Value.Receipt.Id);

            Guid agent = Assert.Single(accepted.Value.Diagnostics.Items).AgentId!.Value;

            var current = await secondRoster.ResolveAsync(caller, agent, null, null, CancellationToken.None);

            Assert.True(current.IsSuccess, current.Error.Message);

            Assert.Equal(1, current.Value.Revision);

            Assert.Equal("Original", current.Value.Description);
        }
        finally
        {
            blocked.Release.TrySetResult();

            _ = await first;
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Interrupted_cache_materialization_has_no_acceptance_and_retry_reobserves_current_bytes(bool changed, long revision)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await InstallAsync(file);

        var connections = new FixtureOrdinaryConnectionFactory(file.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        var unavailable = new AcceptanceConnectionFactory(connections, refuse: true);

        Guid generation = Guid.NewGuid();

        var caller = new GuildCallerContext(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), generation);

        var interruptedRefreshes = new GuildRosterRefreshStore(unavailable, gate, new GuildRosterRefreshCoordinator());

        GuildRoster interrupted = Roster(connections, gate, interruptedRefreshes, generation);

        await SourceAsync("Original");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var mutation = interrupted.CreateRefreshMutation(request);

        var failed = await interrupted.RefreshAsync(caller, mutation, request, CancellationToken.None);

        Assert.True(failed.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Unavailable, failed.Error.Code);

        var refreshedStore = new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator());

        Assert.Null((await refreshedStore.FindAsync(generation, caller.PrincipalId!.Value, mutation, CancellationToken.None)).Value);

        if (changed)
        {
            await SourceAsync("Changed before retry");
        }

        GuildRoster restarted = Roster(connections, gate, refreshedStore, generation);

        var accepted = await restarted.RefreshAsync(caller, mutation, request, CancellationToken.None);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.False(accepted.Value.Receipt.Replayed);

        Guid agent = Assert.Single(accepted.Value.Diagnostics.Items).AgentId!.Value;

        var current = await restarted.ResolveAsync(caller, agent, null, null, CancellationToken.None);

        Assert.Equal(revision, current.Value.Revision);

        Assert.Equal(changed ? "Changed before retry" : "Original", current.Value.Description);

        await SourceAsync("Changed after acceptance");

        var replay = await restarted.RefreshAsync(caller, mutation, request, CancellationToken.None);

        Assert.True(replay.IsSuccess);

        Assert.True(replay.Value.Receipt.Replayed);

        Assert.Equal(accepted.Value.Receipt.Id, replay.Value.Receipt.Id);

        Assert.Equal(revision, (await restarted.ResolveAsync(caller, agent, null, null, CancellationToken.None)).Value.Revision);
    }

    private GuildRoster Roster(IGrimoireOrdinaryConnectionFactory connections, IGrimoireConnectionAdmissionGate gate,
        GuildRosterRefreshStore refreshes, Guid generation) => new(new GuildStore(connections, gate, TimeProvider.System),
            refreshes, new GuildRosterContext(generation, _root), new GuildDeclaredAgentSourceReader(),
            new GuildRemoteCardReader(new SystemDnsResolver()), TimeProvider.System);

    private Task SourceAsync(string description) => File.WriteAllTextAsync(Path.Combine(_root, "review.md"),
        "---\nname: Reviewer\ndescription: " + description + "\n---");

    private static async Task InstallAsync(EvolutionScratchDatabase file)
    {
        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class AcceptanceConnectionFactory(IGrimoireOrdinaryConnectionFactory inner, bool refuse) : IGrimoireOrdinaryConnectionFactory
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken)
        {
            if (kind == GrimoireOrdinaryFreshConnectionKind.ReadWrite)
            {
                Reached.TrySetResult();

                if (refuse)
                {
                    return new Error(ErrorCodes.Guild.Unavailable, "Injected acceptance-open failure after cache materialization.");
                }

                await Release.Task.WaitAsync(cancellationToken);
            }

            return await inner.OpenFreshAsync(kind, cancellationToken);
        }
    }
}
