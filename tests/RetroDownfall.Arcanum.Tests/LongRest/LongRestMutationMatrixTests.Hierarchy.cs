using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The same oracle in the middle of the hierarchy build, where the corpus is larger than one page of the frontier and a fold is read from
/// children that stand for more leaves than a request lists.
/// </summary>
/// <remarks>
/// <para>The discovery rows in this class's other half all stand after the build: the forest is whole and published. A build is different in
/// three ways the rows have to be seen against. A fold's text derives from every leaf under its children, however deep, while the request
/// lists only the first few of each child. Between the unit that accepts a fold's summary and the unit that embeds it, the corpus can change
/// under a text that is already accepted. And a step re-proves the exact inputs it acts on, which for a fold is the whole subtree.</para>
/// <para>The corpus here is 300 memories that all resemble each other, so the forest is three layers deep: twenty-odd folds of a dozen leaves,
/// two folds of those, and a top fold of two children that each stand for well over a hundred leaves. A row is applied to a leaf under the top
/// fold that its request does not list and that no rotating window reaches, at each of the three points a send can be stopped: before the unit
/// that reads the fold, before the summary's send, and before the embedding's send. Whatever the route, the unit must not send anything built
/// from that leaf, and nothing may publish on it.</para>
/// </remarks>
public sealed partial class LongRestMutationMatrixTests
{
    private const int DeepLeaves = 300;

    /// <summary>Where in the top fold's unit the corpus changes.</summary>
    public enum Point
    {
        /// <summary>After the last unit of the layer below, before the unit that reads the fold.</summary>
        BetweenUnits,

        /// <summary>After the summary's first gate, before its send.</summary>
        BeforeSummarySend,

        /// <summary>After the summary is accepted, after the embedding's first gate, before its send.</summary>
        BeforeEmbeddingSend,
    }

    private static readonly string[] DeepCleanRows =
        ["content", "hash-bypass", "label-insert", "owner-binding", "consolidated-source-delete", "bypass:schema-rebuild", "bypass:restore-sanitation"];

    private static readonly string[] DeepProtectedRows =
        ["content", "hash-bypass", "label-content-digest", "owner-binding", "consolidated-source-erase", "bypass:schema-rebuild", "bypass:restore-sanitation", "bypass:erasure-kernel"];

    public static IEnumerable<object[]> DeepCases()
    {
        foreach (bool isProtected in new[] { false, true })
        {
            foreach (string name in isProtected ? DeepProtectedRows : DeepCleanRows)
            {
                foreach (Point point in Enum.GetValues<Point>())
                {
                    yield return [name, isProtected, point];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(DeepCases))]
    public async Task A_deep_fold_refuses_a_mutation_before_it_sends_anything_built_from_it(string name, bool isProtected, Point point)
    {
        Func<Scenario, Task>? prepare = name.StartsWith("consolidated-source", StringComparison.Ordinal) ? Rows.Single(candidate => candidate.Name == name).Prepare : null;

        await using Scenario s = await CreateDeepAsync(isProtected, prepare);

        int before = s.F.Handler.SendCount;

        string? mutated = null;

        if (point == Point.BetweenUnits)
        {
            mutated = s.FixedSurvivor ?? await UncoveredLeafAsync(s, null);

            await ApplyDeepAsync(s, name, mutated);
        }
        else
        {
            LongRestProviderPayloadKind aimed = point == Point.BeforeSummarySend ? LongRestProviderPayloadKind.Summary : LongRestProviderPayloadKind.Embedding;

            s.F.AfterAttemptValidated = async (attempt, _) =>
            {
                if (mutated is not null || !attempt.Work.ItemId.StartsWith("fold:3:", StringComparison.Ordinal) || attempt.PayloadKind != aimed)
                {
                    return;
                }

                // The request this unit is building: the fold whose children stand for more leaves than the request lists.
                LongRestHierarchyFold fold = s.F.Payloads.Last(payload => payload.Kind == LongRestProviderPayloadKind.Summary).Fold!;

                mutated = s.FixedSurvivor ?? await UncoveredLeafAsync(s, fold);

                await ApplyDeepAsync(s, name, mutated);
            };
        }

        _ = await Record.ExceptionAsync(() => s.F.ProcessAsync(s.F.Operation, "fixture-worker", s.Capability, CancellationToken.None));

        Assert.NotNull(mutated);

        Assert.Equal(point == Point.BeforeEmbeddingSend ? 1 : 0, s.F.Handler.SendCount - before);

        Assert.Equal(0L, await s.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations WHERE Published = 1;"));

        LongRunningOperation? operation = await s.F.Operations.GetAsync(s.F.Operation);

        Assert.True(operation is null || operation.State != LongRunningOperationState.Completed, "A sweep whose footing changed must not complete on it.");
    }

    /// <summary>
    /// A paid unit that names no leaf would have every gate in front of its send prove nothing about it, which is the failure a gate that forgot its
    /// inputs would be. The paid door refuses it before anything is prepared or sent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fold_that_names_no_leaf_sends_nothing(bool isProtected)
    {
        await using Scenario s = await CreateDeepAsync(isProtected, null);

        s.F.FoldRead = static work => work with { Leaves = [] };

        int before = s.F.Handler.SendCount;

        _ = await Record.ExceptionAsync(() => s.F.ProcessAsync(s.F.Operation, "fixture-worker", s.Capability, CancellationToken.None));

        Assert.Equal(before, s.F.Handler.SendCount);
    }

    /// <summary>
    /// The shape the rows rest on, held to: if the clustering of the corpus ever changed, a row would be applied to a fold that no longer has
    /// children bigger than a request lists, and would prove nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_top_fold_has_children_that_stand_for_more_leaves_than_its_request_lists(bool isProtected)
    {
        await using Scenario s = await CreateDeepAsync(isProtected, null);

        _ = await s.F.ProcessAsync(s.F.Operation, "fixture-worker", s.Capability, CancellationToken.None);

        LongRestHierarchyFold fold = s.F.Payloads.Last(payload => payload.Kind == LongRestProviderPayloadKind.Summary).Fold!;

        Assert.Equal(3, fold.Layer);

        foreach (LongRestHierarchyChild child in fold.Children)
        {
            long leaves = (await ColumnAsync(s, SubtreeLeavesSql, ("$g", s.GenerationText), ("$root", child.NodeId.ToString("D").ToUpperInvariant()))).Count;

            Assert.True(leaves > LongRestDiscoveryLimits.ExpandedLeaves, $"A child of the top fold stands for {leaves} leaves, which a request lists {child.ExpandedTargets.Length} of.");

            Assert.Equal(LongRestDiscoveryLimits.ExpandedLeaves, child.ExpandedTargets.Length);
        }
    }

    private static async Task<Scenario> CreateDeepAsync(bool isProtected, Func<Scenario, Task>? prepare)
    {
        Scenario s = await Scenario.CreateAsync(isProtected, DeepLeaves, queueUnit: false, related: false, prepare: prepare,
            vectorOf: static index =>
            {
                float[] vector = new float[64];

                vector[0] = 1;

                vector[1 + (index % 5)] = 0.05f * (index % 3);

                return vector;
            },
            stopWhen: static async candidate => (long)(await candidate.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 2;"))! == 2);

        Assert.Equal(2L, await s.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 2;"));

        Assert.Equal(0L, await s.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 3;"));

        Assert.Equal(LongRestSweepPhase.Hierarchy, s.Generation.Phase);

        return s;
    }

    private static Task ApplyDeepAsync(Scenario s, string name, string target)
    {
        const string bypass = "bypass:";

        if (name.StartsWith(bypass, StringComparison.Ordinal))
        {
            s.Bystander = target;

            return ApplyBypassAsync(s, name[bypass.Length..]);
        }

        return Rows.Single(candidate => candidate.Name == name).Apply(s, target);
    }

    private const string SubtreeLeavesSql = """
        WITH RECURSIVE subtree(NodeId) AS (
            SELECT $root UNION ALL SELECT edge.ChildNodeId FROM long_rest_discovery_edges edge JOIN subtree ON edge.ParentNodeId = subtree.NodeId WHERE edge.GenerationId = $g)
        SELECT node.MemoryId FROM subtree JOIN long_rest_discovery_nodes node ON node.NodeId = subtree.NodeId AND node.GenerationId = $g
        WHERE node.Layer = 0 ORDER BY node.MemoryId;
        """;

    /// <summary>
    /// A leaf the fold's text derives from that its request does not list and that no step's rotating window reaches, with a margin for the
    /// revision moving by a few units between the choice and the gate that meets it.
    /// </summary>
    private static async Task<string> UncoveredLeafAsync(Scenario s, LongRestHierarchyFold? fold)
    {
        List<string> all = await ColumnAsync(s, "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $g ORDER BY MemoryId;", ("$g", s.GenerationText));

        List<string> under = all;

        HashSet<string> listed = [];

        if (fold is not null)
        {
            under = [];

            foreach (LongRestHierarchyChild child in fold.Children)
            {
                under.AddRange(await ColumnAsync(s, SubtreeLeavesSql, ("$g", s.GenerationText), ("$root", child.NodeId.ToString("D").ToUpperInvariant())));
            }

            listed = [.. fold.Children.SelectMany(child => child.ExpandedTargets).Select(target => target.MemoryId)];
        }

        long revision = (long)(await s.F.H.ScalarAsync($"""
            SELECT COALESCE((SELECT Revision FROM long_rest_sweeps WHERE OperationId = '{s.F.Operation:N}'), 0)
                + (SELECT Revision FROM long_rest_discovery_generations WHERE GenerationId = '{s.GenerationText}');
            """))!;

        int tiles = (all.Count + LongRestDiscoveryStore.ScrubWindowLeaves - 1) / LongRestDiscoveryStore.ScrubWindowLeaves;

        HashSet<int> reached = [.. Enumerable.Range(-3, 8).Select(delta => (int)((((revision + delta) % tiles) + tiles) % tiles))];

        return under.Last(memory => !listed.Contains(memory) && !reached.Contains(all.IndexOf(memory) / LongRestDiscoveryStore.ScrubWindowLeaves));
    }

    private static async Task<List<string>> ColumnAsync(Scenario s, string sql, params (string Name, object? Value)[] parameters)
    {
        List<string> values = [];

        await using SqliteCommand command = s.F.H.Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
