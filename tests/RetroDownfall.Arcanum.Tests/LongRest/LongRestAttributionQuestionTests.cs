using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The attribution half of the set-based metadata statement, asked of one leaf in every state a flag and the rows that set it can be in. The
/// four questions it holds used to be four passes over the generation's leaves, each parsing every leaf's manifest; they are one pass now
/// (<see cref="LongRestDiscoveryStore.FrozenAttributionQuestion"/>), and the verdict must not have moved.
/// </summary>
/// <remarks>
/// The old statement is kept here, word for word, as the oracle. A leaf has two frozen flags, a consolidated one and a transformation-output
/// one, and each can be set or not; the row that sets the first is a suppression of the leaf's version, and the row that sets the second is a
/// receipt that kept it. Each of those can be absent, the sweep's own (its receipt is on one of the generation's work items), foreign, or, for a
/// receipt, one of no change, which sets nothing. That is every state, forty-eight of them, and in each the old and the new statements give one
/// verdict and that verdict is the one the rule gives.
/// </remarks>
[Collection("Grimoire")]
public sealed class LongRestAttributionQuestionTests
{
    private const string Oracle = """
        AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_suppressions suppression ON suppression.SourceVersionId = leaf.VersionId
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isConsolidated'), 0) = 0
                AND suppression.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_receipts receipt ON receipt.SurvivorVersionId = leaf.VersionId AND receipt.OutcomeCode = 1
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isTransformationOutput'), 0) = 0
                AND receipt.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            WHERE leaf.GenerationId = $generation AND json_extract(leaf.SnapshotJson, '$.isConsolidated') = 1
                AND NOT EXISTS (SELECT 1 FROM long_rest_suppressions suppression WHERE suppression.SourceVersionId = leaf.VersionId))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            WHERE leaf.GenerationId = $generation AND json_extract(leaf.SnapshotJson, '$.isTransformationOutput') = 1
                AND NOT EXISTS (SELECT 1 FROM long_rest_receipts receipt WHERE receipt.OutcomeCode = 1 AND receipt.SurvivorVersionId = leaf.VersionId))
        """;

    public enum Row
    {
        None,

        Own,

        Foreign,

        NoChange,
    }

    public static TheoryData<bool, Row, bool, Row> States()
    {
        TheoryData<bool, Row, bool, Row> states = [];

        foreach (bool consolidated in new[] { false, true })
        {
            foreach (Row suppression in new[] { Row.None, Row.Own, Row.Foreign })
            {
                foreach (bool output in new[] { false, true })
                {
                    foreach (Row receipt in new[] { Row.None, Row.Own, Row.Foreign, Row.NoChange })
                    {
                        states.Add(consolidated, suppression, output, receipt);
                    }
                }
            }
        }

        return states;
    }

    /// <summary>The rule: a flag set by a row the generation does not own is refused, and so is a frozen flag with no row behind it.</summary>
    private static bool Refused(bool consolidated, Row suppression, bool output, Row receipt) =>
        (suppression == Row.Foreign && !consolidated) || (receipt == Row.Foreign && !output)
        || (consolidated && suppression == Row.None) || (output && receipt is Row.None or Row.NoChange);

    [Theory]
    [MemberData(nameof(States))]
    public async Task The_one_pass_gives_the_verdict_of_the_four_in_every_state_of_a_flag_and_its_rows(bool consolidated, Row suppression, bool output, Row receipt)
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await using LongRestSweepHarness harness = h;

        (LongRestDiscoveryGeneration generation, string leaf, string partner) = await CaptureAsync(h);

        foreach (string trigger in new[] { "long_rest_discovery_leaves_guard_update", "long_rest_suppressions_validate_insert" })
        {
            await h.ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger};");
        }

        await h.ExecuteAsync("""
            UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.isConsolidated', json($consolidated), '$.isTransformationOutput', json($output))
            WHERE MemoryId = $memory;
            """, ("$consolidated", consolidated ? "true" : "false"), ("$output", output ? "true" : "false"), ("$memory", MemoryId(2)));

        if (suppression != Row.None)
        {
            await ReceiptAsync(h, "suppression-receipt", outcome: 1, survivorMemory: MemoryId(4), survivor: partner);

            await h.ExecuteAsync("INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, 'suppression-receipt');",
                ("$source", leaf), ("$survivor", partner));

            if (suppression == Row.Own)
            {
                await OwnAsync(h, generation, "suppression-receipt");
            }
        }

        if (receipt != Row.None)
        {
            await ReceiptAsync(h, "output-receipt", outcome: receipt == Row.NoChange ? 2 : 1, survivorMemory: MemoryId(2), survivor: leaf);

            if (receipt == Row.Own)
            {
                await OwnAsync(h, generation, "output-receipt");
            }
        }

        bool oracle = await AdmitsAsync(h, generation, Oracle);

        bool folded = await AdmitsAsync(h, generation, LongRestDiscoveryStore.FrozenAttributionQuestion);

        Assert.Equal(oracle, folded);

        Assert.Equal(!Refused(consolidated, suppression, output, receipt), folded);
    }

    /// <summary>
    /// The statement the oracle and the fold are parts of still answers the whole question: a corpus with nothing attributed to anyone is
    /// admitted, and the same corpus with a foreign suppression is not.
    /// </summary>
    [Fact]
    public async Task The_whole_statement_admits_an_untouched_corpus_and_refuses_a_foreign_suppression()
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await using LongRestSweepHarness harness = h;

        (LongRestDiscoveryGeneration generation, string leaf, string partner) = await CaptureAsync(h);

        await using (SqliteTransaction transaction = h.Connection.BeginTransaction(deferred: true))
        {
            Assert.True(await LongRestDiscoveryStore.FrozenSourceMetadataMatchesAsync(h.Connection, transaction, generation, CancellationToken.None));
        }

        await h.ExecuteAsync("DROP TRIGGER long_rest_suppressions_validate_insert;");

        await ReceiptAsync(h, "foreign-receipt", outcome: 1, survivorMemory: MemoryId(4), survivor: partner);

        await h.ExecuteAsync("INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, 'foreign-receipt');",
            ("$source", leaf), ("$survivor", partner));

        await using SqliteTransaction refused = h.Connection.BeginTransaction(deferred: true);

        Assert.False(await LongRestDiscoveryStore.FrozenSourceMetadataMatchesAsync(h.Connection, refused, generation, CancellationToken.None));
    }

    /// <summary>
    /// Three leaves are captured, and then a fourth memory is written that the generation did not freeze: the survivor a consolidation names. A
    /// survivor that is itself a leaf would be a transformation output as well, and the corpus would be refused for two reasons at once.
    /// </summary>
    private static async Task<(LongRestDiscoveryGeneration Generation, string Leaf, string Survivor)> CaptureAsync(LongRestSweepHarness h)
    {
        for (int number = 1; number <= 3; number++)
        {
            await h.SeedDistinctAsync(MemoryId(number), number);
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.SeedDistinctAsync(MemoryId(4), 4);

        string leaf = (string)(await h.ScalarAsync($"SELECT VersionId FROM long_rest_discovery_leaves WHERE MemoryId = '{MemoryId(2)}';"))!;

        string survivor = (string)(await h.ScalarAsync($"""
            SELECT head.CurrentVersionId FROM annal_claims claim JOIN annal_heads head ON head.ClaimId = claim.ClaimId
            WHERE claim.SubjectStoreCode = 1 AND claim.SubjectId = '{MemoryId(4)}';
            """))!;

        return (generation, leaf, survivor);
    }

    private static string MemoryId(int number) => new Guid("a0000000-0000-0000-0000-" + number.ToString("D12", CultureInfo.InvariantCulture)).ToString("D");

    private static Task ReceiptAsync(LongRestSweepHarness h, string receipt, int outcome, string survivorMemory, string survivor) => h.ExecuteAsync("""
        INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
        VALUES ($receipt, 1, 2, $outcome, $reason, $hash, $hash, $memory, $survivor, '2026-10-10T00:00:00.0000000Z');
        """, ("$receipt", receipt), ("$outcome", outcome), ("$reason", outcome == 1 ? 0 : 1), ("$hash", new string('a', 64)), ("$memory", survivorMemory), ("$survivor", survivor));

    /// <summary>Puts the receipt on a committed work item of the generation, which is what makes it the sweep's own.</summary>
    private static Task OwnAsync(LongRestSweepHarness h, LongRestDiscoveryGeneration generation, string receipt) => h.ExecuteAsync("""
        INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId, SourceDigest, RecipeDigest, Revision, DispositionCode, ReceiptId)
        VALUES ($unit, randomblob(32), $generation, $operation, 3, $item, zeroblob(32), zeroblob(32), 0, 2, $receipt);
        """, ("$unit", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$generation", generation.GenerationId.ToString("D").ToUpperInvariant()),
        ("$operation", generation.OperationId.ToString("N")), ("$item", "anchor:" + receipt), ("$receipt", receipt));

    private static async Task<bool> AdmitsAsync(LongRestSweepHarness h, LongRestDiscoveryGeneration generation, string question)
    {
        await using SqliteCommand command = h.Connection.CreateCommand();

        command.CommandText = "SELECT 1 " + question + ";";

        _ = command.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
    }
}
