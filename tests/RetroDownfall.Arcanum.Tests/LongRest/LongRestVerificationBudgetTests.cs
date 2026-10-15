namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The verification budget of the Long Rest at a size every run can afford, so that a change which makes the idle path quadratic is a red
/// build and not a slow install. The same numbers at a scale only a deliberate run can afford are asserted by <see cref="LongRestScaleProbe"/>.
/// </summary>
/// <remarks>
/// These assert counts of what was verified and statements that ran, never time. A budget stated in milliseconds fails on a busy machine
/// and passes on a quadratic algorithm that happens to be small today.
/// </remarks>
[Collection("Grimoire")]
public sealed class LongRestVerificationBudgetTests
{
    private const int Corpus = 150;

    private const int Partitions = 3;

    /// <summary>
    /// How many whole-corpus passes the idle path of one clean scope makes: the completed lookup proves its generation's leaves once, because
    /// adopting a completed generation's answer is a seam. A pass is one statement over the generation, however many leaves there are, so the
    /// leaves it proves grow with the corpus and the statements it takes do not.
    /// </summary>
    internal const int CorpusPassesPerIdleCleanLookup = 1;

    [Fact]
    public async Task An_idle_turn_over_a_hundred_and_fifty_memories_pays_for_one_corpus_pass_and_reads_no_leaf_in_full()
    {
        await using LongRestScaleScenario scenario = await LongRestScaleScenario.CreateAsync(Corpus, Partitions, LongRestScaleCompletion.Marked);

        IdleTurn turn = await scenario.RunIdleTurnAsync();

        Assert.Equal(CorpusPassesPerIdleCleanLookup, turn.CorpusPasses);

        Assert.Equal(CorpusPassesPerIdleCleanLookup * Corpus, turn.MetadataLeafReads);

        Assert.Equal(0, turn.FullLeafReads);

        // The clean completed lookup: one set-based proof of the corpus and one of the generation. A protected partition's lookup finds no
        // candidate, so it proves nothing.
        Assert.Equal(2, turn.AntiJoinPasses);

        // The probe builds its projection once per call: once for each partition and once more to learn there are no more.
        Assert.Equal(Partitions + 1, turn.PartitionPasses);

        // Every store's transaction is counted: the lookups (one for the clean scope and one for each partition's) and the probe (one for
        // each partition and one more to learn there are no more). Every one of them is a read, so nothing on the idle path takes the
        // writer lock; SQLite's own statement trace agrees, because it sees a BEGIN IMMEDIATE whichever store issued it.
        Assert.Equal(2 * (1 + Partitions), turn.ReadTransactions);

        Assert.Equal(0, turn.WriterTransactions);

        Assert.Equal(0, turn.ImmediateTransactions);
    }

    /// <summary>
    /// The idle turn is the same statements however large the corpus is. Verifying every leaf of the completed generation is a seam, and a
    /// seam is one statement over the generation's leaves and the live rows they describe, so the figure that grew with the corpus, the
    /// statements the turn takes, no longer does: the statement trace of a turn over 30 memories and one over 150 is the same list of
    /// statements run the same number of times.
    /// </summary>
    [Fact]
    public async Task An_idle_turn_runs_the_same_statements_whatever_the_size_of_the_corpus()
    {
        const int Small = 30;

        await using LongRestScaleScenario small = await LongRestScaleScenario.CreateAsync(Small, Partitions, LongRestScaleCompletion.Marked);

        await using LongRestScaleScenario large = await LongRestScaleScenario.CreateAsync(Corpus, Partitions, LongRestScaleCompletion.Marked);

        IdleTurn smaller = await small.RunIdleTurnAsync();

        IdleTurn larger = await large.RunIdleTurnAsync();

        Assert.Equal(smaller.StatementCount, larger.StatementCount);

        Assert.Equal(smaller.Statements.OrderBy(static pair => pair.Key, StringComparer.Ordinal), larger.Statements.OrderBy(static pair => pair.Key, StringComparer.Ordinal));

        // The leaves are still all proved: the pass reads each of them, and the work that grew with them is the work inside one statement.
        Assert.Equal(CorpusPassesPerIdleCleanLookup * Small, smaller.MetadataLeafReads);

        Assert.Equal(CorpusPassesPerIdleCleanLookup * Corpus, larger.MetadataLeafReads);

        Assert.Equal(0, larger.FullLeafReads);

        // Nor does the one statement that proves the leaves page: it runs once.
        Assert.Equal(1, larger.Statements.Single(static pair => pair.Key.Contains("long_rest_discovery_leaves leaf", StringComparison.Ordinal)
            && pair.Key.Contains("json_group_array", StringComparison.Ordinal)).Value);
    }

    /// <summary>
    /// The larger scenarios mark their generation complete instead of sweeping it, because a sweep is one unit for each memory. That is only a
    /// fair stand-in if a corpus swept for real answers the idle turn in exactly the same way, so this proves it at the size a sweep is cheap.
    /// </summary>
    [Fact]
    public async Task A_corpus_marked_complete_answers_an_idle_turn_exactly_as_a_corpus_swept_to_completion_does()
    {
        const int Size = 24;

        await using LongRestScaleScenario swept = await LongRestScaleScenario.CreateAsync(Size, Partitions, LongRestScaleCompletion.Swept);

        await using LongRestScaleScenario marked = await LongRestScaleScenario.CreateAsync(Size, Partitions, LongRestScaleCompletion.Marked);

        IdleTurn real = await swept.RunIdleTurnAsync();

        IdleTurn stand = await marked.RunIdleTurnAsync();

        Assert.Equal(real.StatementCount, stand.StatementCount);

        Assert.Equal(real.CorpusPasses, stand.CorpusPasses);

        Assert.Equal(real.MetadataLeafReads, stand.MetadataLeafReads);

        Assert.Equal(real.PartitionPasses, stand.PartitionPasses);

        Assert.Equal(real.Statements.OrderBy(static pair => pair.Key, StringComparer.Ordinal), stand.Statements.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }
}
