using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// A generation's lifetime is its discovery's. The search index it kept only for discovery is deleted the moment it completes, and a
// completed generation is replaced, not accumulated: when a successor of the same scope and partition completes, the predecessor and everything
// it derived from the corpus is purged. What it decided stays: the receipts, the suppressions and the operation's own history.
public sealed partial class LongRestSweepStoreTests
{
    private static readonly string[] GenerationTables =
    [
        "long_rest_discovery_leaves", "long_rest_discovery_nodes", "long_rest_discovery_edges", "long_rest_accepted_summaries",
    ];

    private static string Quoted(Guid generation) => "'" + generation.ToString("D").ToUpperInvariant() + "'";

    private static async Task<long> CountAsync(LongRestSweepHarness h, string sql) => (long)(await h.ScalarAsync(sql))!;

    // Every row a generation's discovery and adjudication left behind, counted without regard to which table holds it.
    private static async Task<long> DerivedRowsAsync(LongRestSweepHarness h, Guid generation)
    {
        string id = Quoted(generation);

        long total = 0;

        foreach (string table in GenerationTables)
        {
            total += await CountAsync(h, $"SELECT count(*) FROM {table} WHERE GenerationId = {id};");
        }

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_discovery_embeddings WHERE NodeId IN (SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId = {id});");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_work_items WHERE GenerationId = {id};");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_work_inputs WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = {id});");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_adjudications WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = {id});");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_provider_effects WHERE WorkItemId IN (SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = {id});");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_clean_fts WHERE GenerationId = {id};");

        total += await CountAsync(h, $"SELECT count(*) FROM long_rest_protected_fts WHERE GenerationId = {id};");

        return total;
    }

    // What the generation decided, and the operation that ran it, which a superseding completion must leave exactly as they were.
    private static async Task<string> DecisionsAsync(LongRestSweepHarness h, Guid operation)
    {
        List<string> rows = [];

        (string Table, string Where)[] snapshot =
        [
            ("long_rest_receipts", "1 = 1"), ("long_rest_receipt_inputs", "1 = 1"), ("long_rest_suppressions", "1 = 1"),
            ("long_rest_protected_receipt_evidence", "1 = 1"), ("long_rest_protected_receipt_inputs", "1 = 1"),
            ("LongRunningOperations", $"Id = '{operation:N}'"), ("BillableOperations", "1 = 1"), ("saga_memories", "1 = 1"), ("annal_heads", "1 = 1"),
        ];

        foreach ((string table, string where) in snapshot)
        {
            await using SqliteCommand command = h.Connection.CreateCommand();

            command.CommandText = $"SELECT * FROM {table} WHERE {where} ORDER BY 1, 2;";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    object value = reader.GetValue(index);

                    rows.Add(table + ":" + index + ":" + (value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        return string.Join('\n', rows);
    }

    private static async Task<LongRestSweep> DrainAsync(Fixture fixture, LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability)
    {
        LongRestSweep sweep = LongRestSweepHarness.Require(await fixture.Store.BeginOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None));

        for (int page = 0; page < 8 && sweep.Phase != LongRestSweepPhase.Completed; page++)
        {
            Assert.Null(LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, capability, CancellationToken.None)));

            sweep = LongRestSweepHarness.Require(await fixture.Store.BeginOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None));
        }

        Assert.Equal(LongRestSweepPhase.Completed, sweep.Phase);

        return sweep;
    }

    // Completes the fixture's own generation with an applied decision, so it holds receipts, suppressions, accepted summaries, a hierarchy and
    // a paid adjudication, and returns the generation a second capture of the surviving corpus would succeed.
    private static async Task<LongRestSweep> CompleteWithAppliedDecisionAsync(Fixture fixture)
    {
        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None));

        return await DrainAsync(fixture, fixture.Generation, fixture.Capability);
    }

    private static async Task<(LongRestDiscoveryGeneration Generation, LongRestMaintenanceCapability? Capability)> StartSuccessorAsync(Fixture fixture, bool protect)
    {
        LongRestDiscoveryGeneration captured = await fixture.H.CaptureAsync(null, fixture.Generation.Scope.ProtectedPartitionDigest, protect ? fixture.Authority : null);

        LongRestDiscoveryGeneration published = await fixture.H.BuildAsync(captured);

        return (published, protect ? await fixture.CreateCapabilityAsync(published) : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completing_a_successor_purges_the_previous_completed_generation_and_leaves_what_it_decided(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 3);

        Guid predecessor = fixture.Generation.GenerationId;

        LongRestSweep completed = await CompleteWithAppliedDecisionAsync(fixture);

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        // The completed generation has already shed its search index, which only discovery read, but keeps the rest until a successor completes.
        Assert.Equal(0L, await CountAsync(fixture.H, "SELECT count(*) FROM long_rest_clean_fts;") + await CountAsync(fixture.H, "SELECT count(*) FROM long_rest_protected_fts;"));

        Assert.True(await DerivedRowsAsync(fixture.H, predecessor) > 0);

        Assert.Equal(6L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(predecessor)};"));

        Assert.Equal(1L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        string decided = await DecisionsAsync(fixture.H, fixture.Generation.OperationId);

        // The corpus now has one eligible memory, the survivor, which a successor captures and completes.
        (LongRestDiscoveryGeneration successor, LongRestMaintenanceCapability? capability) = await StartSuccessorAsync(fixture, protect);

        Assert.Equal(1L, await CountAsync(fixture.H, $"SELECT count(*) FROM long_rest_discovery_leaves WHERE GenerationId = {Quoted(successor.GenerationId)};"));

        Assert.True(await DerivedRowsAsync(fixture.H, predecessor) > 0, "an incomplete successor must not purge its predecessor");

        _ = await DrainAsync(fixture, successor, capability);

        Assert.Equal(0L, await DerivedRowsAsync(fixture.H, predecessor));

        Assert.Equal(7L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(predecessor)};"));

        Assert.Equal(0L, await fixture.H.ScalarAsync($"SELECT Published FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(predecessor)};"));

        Assert.Equal(7L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE GenerationId = {Quoted(predecessor)};"));

        Assert.Equal(6L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(successor.GenerationId)};"));

        // The successor's own index is gone the moment it completed, though the rest of what it derived stays, and what the predecessor decided is untouched.
        Assert.Equal(0L, await CountAsync(fixture.H, "SELECT count(*) FROM long_rest_clean_fts;") + await CountAsync(fixture.H, "SELECT count(*) FROM long_rest_protected_fts;"));

        Assert.True(await DerivedRowsAsync(fixture.H, successor.GenerationId) > 0);

        string after = await DecisionsAsync(fixture.H, fixture.Generation.OperationId);

        Assert.Equal(decided, after);

        // A replay finds the generation that is left, the successor's, never the one that was purged.
        Guid? replay = LongRestSweepHarness.Require(await fixture.Store.FindCompletedReplayAsync(successor.Fence.CanonicalScope, successor.Scope, successor.Recipe, CancellationToken.None));

        Assert.Equal(successor.OperationId, replay);
    }

    [Fact]
    public async Task A_successor_that_does_not_complete_keeps_the_predecessor()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 3);

        Guid predecessor = fixture.Generation.GenerationId;

        _ = await CompleteWithAppliedDecisionAsync(fixture);

        long rows = await DerivedRowsAsync(fixture.H, predecessor);

        (LongRestDiscoveryGeneration successor, _) = await StartSuccessorAsync(fixture, protect: false);

        // Published but incomplete.
        Assert.Equal(rows, await DerivedRowsAsync(fixture.H, predecessor));

        // Retired is how a failed or abandoned request ends: it is not a completion and purges nothing.
        await fixture.H.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 9, Revision = Revision + 1 WHERE GenerationId = {Quoted(successor.GenerationId)};");

        Assert.Equal(rows, await DerivedRowsAsync(fixture.H, predecessor));

        Assert.Equal(6L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(predecessor)};"));

        // An invalidated successor is not a completion either.
        await fixture.H.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 7, Revision = Revision + 1 WHERE GenerationId = {Quoted(successor.GenerationId)};");

        Assert.Equal(rows, await DerivedRowsAsync(fixture.H, predecessor));

        Assert.Equal(6L, await fixture.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(predecessor)};"));
    }

    [Fact]
    public async Task Supersession_is_scoped_to_the_scope_kind_the_campaign_and_the_partition_and_leaves_retired_generations()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid campaignX = Guid.NewGuid();

        Guid campaignY = Guid.NewGuid();

        CovenantDigest partitionP = new(Enumerable.Repeat((byte)7, 32).ToArray());

        CovenantDigest partitionQ = new(Enumerable.Repeat((byte)9, 32).ToArray());

        (Guid? Campaign, CovenantDigest? Partition)[] scopes = [(null, null), (campaignX, null), (campaignY, null), (null, partitionP), (campaignX, partitionP), (null, partitionQ)];

        List<Guid> completed = [];

        foreach ((Guid? campaign, CovenantDigest? partition) in scopes)
        {
            Guid generation = (await h.StartAsync(campaign, partition)).GenerationId;

            await h.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 6, Revision = Revision + 1, Published = 1 WHERE GenerationId = {Quoted(generation)};");

            completed.Add(generation);
        }

        // A retired generation of the very scope that is about to be superseded: failed requests keep their paid evidence on purpose.
        Guid retired = (await h.StartAsync(null, null)).GenerationId;

        await h.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 9, Revision = Revision + 1 WHERE GenerationId = {Quoted(retired)};");

        string[] before = await StatesAsync(h, [.. completed, retired]);

        // A successor of the Global clean scope completes.
        Guid successor = (await h.StartAsync(null, null)).GenerationId;

        await h.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 6, Revision = Revision + 1, Published = 1 WHERE GenerationId = {Quoted(successor)};");

        Assert.Equal(7L, await h.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(completed[0])};"));

        string[] after = await StatesAsync(h, [.. completed, retired]);

        // Only the first changed: every other Campaign, the other partitions of the same Campaign and Global scope, and the retired generation.
        Assert.NotEqual(before[0], after[0]);

        Assert.Equal(before[1..], after[1..]);

        Assert.Equal(6L, await h.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(successor)};"));

        // Touching the completed successor again, with or without leaving phase 6, supersedes nothing further.
        await h.ExecuteAsync($"UPDATE long_rest_discovery_generations SET PhaseCode = 6, Revision = Revision + 1 WHERE GenerationId = {Quoted(successor)};");

        Assert.Equal(after, await StatesAsync(h, [.. completed, retired]));
    }

    private static async Task<string[]> StatesAsync(LongRestSweepHarness h, Guid[] generations)
    {
        List<string> states = [];

        foreach (Guid generation in generations)
        {
            states.Add((string)(await h.ScalarAsync($"SELECT PhaseCode || ':' || Revision || ':' || Published FROM long_rest_discovery_generations WHERE GenerationId = {Quoted(generation)};"))!);
        }

        return [.. states];
    }
}
