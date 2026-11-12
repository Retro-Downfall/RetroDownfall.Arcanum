using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

// Process-wide allocation includes other concurrent tests. Run alone, without coverage instrumentation.
internal sealed class GuildPerformanceMeasurement(string workload, int operations, int submittedContenders, string fixture)
{
    private static readonly SemaphoreSlim WorkloadGate = new(1, 1);

    internal static async Task<IDisposable> AcquireAsync()
    {
        await WorkloadGate.WaitAsync();

        return new WorkloadLease();
    }

    private sealed class WorkloadLease : IDisposable
    {
        public void Dispose() => WorkloadGate.Release();
    }

    private readonly Stopwatch _watch = Stopwatch.StartNew();

    private readonly long _allocated = GC.GetTotalAllocatedBytes();

    private long _allocatedBytes;

    private bool _stopped;

    internal void Stop()
    {
        if (_stopped) throw new InvalidOperationException("The performance measurement has already stopped.");

        _watch.Stop();

        _allocatedBytes = GC.GetTotalAllocatedBytes() - _allocated;

        _stopped = true;
    }

    internal void Write(long? durableRowsAdded = null, int? pages = null, int? sqlStatements = null, int? sqlWriteStatements = null,
        string? sqlScope = null)
    {
        if (!_stopped) throw new InvalidOperationException("Stop the measurement before reading back or reporting results.");

        string os = JsonEncodedText.Encode(RuntimeInformation.OSDescription).ToString();

        string runtime = JsonEncodedText.Encode(RuntimeInformation.FrameworkDescription).ToString();

        string architecture = RuntimeInformation.ProcessArchitecture.ToString();

        string elapsed = _watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);

        string rows = durableRowsAdded?.ToString(CultureInfo.InvariantCulture) ?? "null";

        string pageCount = pages?.ToString(CultureInfo.InvariantCulture) ?? "null";

        string sqlCount = sqlStatements?.ToString(CultureInfo.InvariantCulture) ?? "null";

        string writeCount = sqlWriteStatements?.ToString(CultureInfo.InvariantCulture) ?? "null";

        Console.WriteLine($"{{\"kind\":\"guild-performance\",\"workload\":\"{workload}\",\"fixture\":\"{fixture}\",\"operations\":{operations},\"submittedContenders\":{submittedContenders},\"elapsedMs\":{elapsed},\"processAllocatedBytes\":{_allocatedBytes},\"durableRowsAdded\":{rows},\"pages\":{pageCount},\"sqlStatements\":{sqlCount},\"sqlWriteStatements\":{writeCount},\"sqlScope\":\"{sqlScope ?? (sqlStatements is null ? "unavailable: store connections not traced" : "per-contender connection, includes transaction and trigger callbacks")}\",\"os\":\"{os}\",\"runtime\":\"{runtime}\",\"architecture\":\"{architecture}\"}}");
    }

    internal static void RequireOptIn() => Skip.IfNot(global::System.Environment.GetEnvironmentVariable("ARCANUM_GUILD_PERF") == "1",
        "Set ARCANUM_GUILD_PERF=1 to run Guild performance qualification.");
}

// Only fixture-owned fresh leases are traced. Connection setup and separate fixture readbacks are outside this scope.
internal sealed class GuildPerformanceSqlTrace
{
    internal const string Scope = "fixture-owned fresh store connections; SQL callbacks include trigger and rolled-back work; exclude connection bootstrap and fixture readbacks";

    private int _active;

    private int _statements;

    private int _writes;

    private readonly SQLitePCL.strdelegate_trace _callback;

    internal GuildPerformanceSqlTrace() => _callback = Observe;

    internal IGrimoireOrdinaryConnectionFactory Wrap(FixtureOrdinaryConnectionFactory inner) => new TracingFactory(inner, this);

    internal void Start()
    {
        Interlocked.Exchange(ref _statements, 0);

        Interlocked.Exchange(ref _writes, 0);

        Volatile.Write(ref _active, 1);
    }

    internal (int Statements, int Writes) Stop()
    {
        Volatile.Write(ref _active, 0);

        return (Volatile.Read(ref _statements), Volatile.Read(ref _writes));
    }

    private void Observe(object? state, string sql)
    {
        if (Volatile.Read(ref _active) == 0) return;

        Interlocked.Increment(ref _statements);

        string statement = sql.TrimStart();

        if (statement.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _writes);
    }

    private sealed class TracingFactory(IGrimoireOrdinaryConnectionFactory inner, GuildPerformanceSqlTrace trace)
        : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind, CancellationToken cancellationToken)
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await inner.OpenFreshAsync(kind, cancellationToken);

            if (opened.IsFailure) return opened.Error;

            IGrimoireOrdinaryConnectionLease lease = opened.Value;

            try
            {
                SQLitePCL.raw.sqlite3_trace(lease.Connection.Handle, trace._callback, null);

                return Result<IGrimoireOrdinaryConnectionLease>.Success(new TracingLease(lease));
            }
            catch
            {
                await lease.DisposeAsync();

                throw;
            }
        }
    }

    private sealed class TracingLease(IGrimoireOrdinaryConnectionLease inner) : IGrimoireOrdinaryConnectionLease
    {
        private int _disposed;

        public SqliteConnection Connection => inner.Connection;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { SQLitePCL.raw.sqlite3_trace(Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null); }
            finally { inner.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { SQLitePCL.raw.sqlite3_trace(Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null); }
            finally { await inner.DisposeAsync(); }
        }
    }
}

public sealed class GuildPerformanceSqlTraceTests
{
    [SkippableFact]
    public async Task Trace_is_scoped_to_owned_leases_and_counts_only_started_work()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        await using (SqliteConnection setup = await database.OpenAsync(default))
        await using (SqliteCommand create = setup.CreateCommand())
        {
            create.CommandText = "CREATE TABLE trace_probe (value INTEGER)";

            await create.ExecuteNonQueryAsync();
        }

        GuildPerformanceSqlTrace trace = new();

        IGrimoireOrdinaryConnectionFactory wrapped = trace.Wrap(new(database.ConnectionString));

        trace.Start();

        await using (SqliteConnection unrelated = await database.OpenAsync(default))
        await using (SqliteCommand command = unrelated.CreateCommand())
        {
            command.CommandText = "INSERT INTO trace_probe VALUES (1)";

            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal((0, 0), trace.Stop());

        trace.Start();

        var opened = await wrapped.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using (IGrimoireOrdinaryConnectionLease lease = opened.Value)
        await using (SqliteCommand command = lease.Connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO trace_probe VALUES (2)";

            await command.ExecuteNonQueryAsync();

            command.CommandText = "UPDATE trace_probe SET value=3 WHERE value=2";

            await command.ExecuteNonQueryAsync();

            command.CommandText = "SELECT count(*) FROM trace_probe";

            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }

        var counts = trace.Stop();

        Assert.True(counts.Statements >= 3);

        Assert.Equal(2, counts.Writes);

        trace.Start();

        Assert.Equal((0, 0), trace.Stop());
    }
}

public sealed partial class GuildQuestStoreTests
{
    [SkippableTheory]
    [InlineData(32)]
    [InlineData(128)]
    [Trait("Category", "Perf")]
    public async Task Performance_quest_issue_replay_tree_and_cancel_frontier(int count)
    {
        GuildPerformanceMeasurement.RequireOptIn();

        using IDisposable workload = await GuildPerformanceMeasurement.AcquireAsync();

        GuildPerformanceSqlTrace trace = new();

        await using Fixture f = await Fixture.CreateAsync(trace.Wrap);

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        long before = await f.CountAsync("guild_quests");

        trace.Start();

        GuildPerformanceMeasurement issue = new("quest-child-issue", count, 1, "real-sqlcipher-fake-host-budget");

        HashSet<Guid> children = [];

        for (int i = 0; i < count; i++) children.Add((await f.ChildAsync(root, driver)).Quest.Id);

        issue.Stop();

        var issueSql = trace.Stop();

        issue.Write(await f.CountAsync("guild_quests") - before, sqlStatements: issueSql.Statements, sqlWriteStatements: issueSql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        Assert.Equal(count, children.Count);

        Assert.Equal(count + 1L, await f.CountAsync("guild_quests"));

        trace.Start();

        GuildPerformanceMeasurement replay = new("quest-root-idempotent-replay", count, 1, "real-sqlcipher-fake-host-budget");

        for (int i = 0; i < count; i++)
        {
            var result = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(root.Quest.Id, result.Value.Quest.Id);

            Assert.True(result.Value.Quest.Receipt!.Replayed);
        }

        replay.Stop();

        var replaySql = trace.Stop();

        replay.Write(await f.CountAsync("guild_quests") - before - count, sqlStatements: replaySql.Statements, sqlWriteStatements: replaySql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        trace.Start();

        GuildPerformanceMeasurement traversal = new("quest-tree-page", count + 1, 1, "real-sqlcipher-fake-host-budget");

        HashSet<Guid> seen = [];

        string? cursor = null;

        int pages = 0;

        do
        {
            var page = await TreeReadAsync(f, root.Quest.Id, new(cursor, 10));

            Assert.True(page.IsSuccess, page.Error.Message);

            foreach (var node in page.Value.Items) Assert.True(seen.Add(node.Id));

            cursor = page.Value.NextCursor;

            pages++;
        } while (cursor is not null);

        traversal.Stop();

        var traversalSql = trace.Stop();

        traversal.Write(pages: pages, sqlStatements: traversalSql.Statements, sqlWriteStatements: traversalSql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        Assert.Equal(count + 1, seen.Count);

        Assert.True(children.IsSubsetOf(seen));

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), root.Quest.Revision, GuildCancellationReason.OperatorRequested);

        long cancellationsBefore = await f.CountAsync("guild_quest_cancellations");

        trace.Start();

        GuildPerformanceMeasurement cascade = new("quest-cancel-frontier", count, 1, "real-sqlcipher-fake-host-budget");

        var accepted = await f.Store.RequestCancellationAsync(f.Caller,
            new(cancel.IdempotencyKey, GuildQuestIntent.Compute(root.Quest.Id, cancel).Value), root.Quest.Id, cancel, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        HashSet<Guid> canceled = [];

        long after = 0;

        int cancellationPages = 0;

        while (true)
        {
            var frontier = await f.Store.ReadCancellationWorkPageAsync(f.Caller, root.Quest.Id, after, default);

            Assert.True(frontier.IsSuccess, frontier.Error.Message);

            cancellationPages++;

            if (frontier.Value.Length == 0) break;

            foreach (var item in frontier.Value) Assert.True(canceled.Add(item.QuestId));

            after = frontier.Value[^1].IssuedSequence;
        }

        cascade.Stop();

        var cascadeSql = trace.Stop();

        cascade.Write(await f.CountAsync("guild_quest_cancellations") - cancellationsBefore, cancellationPages, sqlStatements: cascadeSql.Statements, sqlWriteStatements: cascadeSql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        Assert.Equal(children.Order(), canceled.Order());
    }

    [SkippableFact]
    [Trait("Category", "Perf")]
    public async Task Performance_budget_sibling_allocation_contention()
    {
        GuildPerformanceMeasurement.RequireOptIn();

        using IDisposable workload = await GuildPerformanceMeasurement.AcquireAsync();

        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        GuildBudgetGrant budget = new(new(GuildBudgetMode.Finite, 16m), new(GuildBudgetMode.Finite, 16));

        Guid parent;

        await using (SqliteConnection connection = await f.OpenAsync())
        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            var created = await ledger.AllocateAccountWithinAsync(connection, transaction, GuildBudgetOwnerKind.Quest, Guid.NewGuid(), null, budget, default);

            Assert.True(created.IsSuccess, created.Error.Message);

            parent = created.Value;

            await transaction.CommitAsync();
        }

        const int count = 8;

        GuildBudgetGrant allocation = new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 1));

        int statements = 0;

        int writes = 0;

        GuildPerformanceMeasurement measure = new("budget-concurrent-sibling-allocation", count, count, "real-sqlcipher-budget-ledger");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            await using SqliteConnection connection = await f.OpenAsync();

            SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) =>
            {
                Interlocked.Increment(ref statements);

                string statement = sql.TrimStart();

                if (statement.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                    || statement.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref writes);
            }, null);

            try
            {
                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                var result = await ledger.AllocateAccountWithinAsync(connection, transaction, GuildBudgetOwnerKind.Quest, Guid.NewGuid(), parent, allocation, default);

                if (result.IsSuccess) await transaction.CommitAsync();

                return result;
            }
            finally
            {
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            }
        })));

        measure.Stop();

        measure.Write(await f.CountAsync("guild_budget_accounts") - 1, sqlStatements: statements, sqlWriteStatements: writes);

        Assert.All(outcomes, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Equal(count, outcomes.Select(result => result.Value).Distinct().Count());

        await using SqliteConnection verify = await f.OpenAsync();

        await using SqliteTransaction read = verify.BeginTransaction(deferred: false);

        var state = await ledger.ReadWithinAsync(verify, read, parent, default);

        Assert.True(state.IsSuccess, state.Error.Message);

        Assert.Equal(8m, state.Value.Remaining.Usd.Value);

        Assert.Equal(8L, state.Value.Remaining.Tokens.Value);
    }
}

public sealed partial class GuildPortalStoreTests
{
    [SkippableTheory]
    [InlineData(24)]
    [InlineData(96)]
    [Trait("Category", "Perf")]
    public async Task Performance_circle_publish_claim_materialize_and_replay(int count)
    {
        GuildPerformanceMeasurement.RequireOptIn();

        using IDisposable workload = await GuildPerformanceMeasurement.AcquireAsync();

        GuildPerformanceSqlTrace trace = new();

        await using Fixture f = await Fixture.CreateAsync(trace.Wrap);

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(root);

        var cursor = await f.Portal.CaptureReplayCursorAsync(f.Caller, root.Quest.Id, default);

        Assert.True(cursor.IsSuccess, cursor.Error.Message);

        long before = await f.CountAsync("guild_envelopes");

        trace.Start();

        GuildPerformanceMeasurement publish = new("circle-publish", count, 1, "real-sqlcipher-fake-host-budget");

        for (int i = 0; i < count; i++)
        {
            var sent = await f.Portal.PublishAsync(f.Caller, root.Quest.Id, Intervention(root.Quest.Id, $"message-{i:D2}"), default);

            Assert.True(sent.IsSuccess, sent.Error.Message);
        }

        publish.Stop();

        var publishSql = trace.Stop();

        publish.Write(await f.CountAsync("guild_envelopes") - before, sqlStatements: publishSql.Statements, sqlWriteStatements: publishSql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        long inputsBefore = await f.CountAsync("guild_portal_inputs");

        trace.Start();

        GuildPerformanceMeasurement read = new("circle-claim-materialize-apply", count, 1, "real-sqlcipher-fake-host-budget");

        for (int i = 0; i < count; i++)
        {
            var claimed = await f.Portal.ClaimAsync(driver, default);

            Assert.True(claimed.IsSuccess, claimed.Error.Message);

            var prompt = await f.Portal.ReadPromptAsync(claimed.Value, default);

            Assert.True(prompt.IsSuccess, prompt.Error.Message);

            Assert.Contains($"message-{i:D2}", prompt.Value, StringComparison.Ordinal);

            var applied = await f.Portal.ApplyAsync(claimed.Value, default);

            Assert.True(applied.IsSuccess, applied.Error.Message);
        }

        read.Stop();

        var readSql = trace.Stop();

        read.Write(await f.CountAsync("guild_portal_inputs") - inputsBefore, sqlStatements: readSql.Statements, sqlWriteStatements: readSql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        Assert.Equal(count, await f.CountAsync("guild_portal_inputs"));

        trace.Start();

        GuildPerformanceMeasurement replay = new("circle-event-replay", count, 1, "real-sqlcipher-fake-host-budget");

        var observed = await f.Portal.ReadReplayAsync(f.Caller, root.Quest.Id, cursor.Value.Cursor, default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        replay.Stop();

        var replaySql = trace.Stop();

        replay.Write(pages: 1, sqlStatements: replaySql.Statements, sqlWriteStatements: replaySql.Writes, sqlScope: GuildPerformanceSqlTrace.Scope);

        Assert.True(observed.Value.Page.Items.Length >= count);
    }
}

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [SkippableFact]
    [Trait("Category", "Perf")]
    public async Task Performance_disjoint_workspace_leases_concurrently()
    {
        GuildPerformanceMeasurement.RequireOptIn();

        using IDisposable workload = await GuildPerformanceMeasurement.AcquireAsync();

        await using Fixture f = await Fixture.CreateAsync();

        const int count = 8;

        GuildPerformanceMeasurement measure = new("workspace-disjoint-lease-acquisition", count, count, "real-sqlcipher-physical-broker-fixture-authority");

        var results = await Task.WhenAll(Enumerable.Range(0, count).Select(i => f.AcquireAsync($"file-{i:D2}.txt")));

        measure.Stop();

        measure.Write();

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Equal(count, results.Select(result => result.Value.Id).Distinct().Count());
    }
}
