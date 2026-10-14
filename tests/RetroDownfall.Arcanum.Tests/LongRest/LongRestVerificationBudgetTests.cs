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
    /// How many whole-corpus passes the idle path of one clean scope makes: the completed lookup proves its generation's leaves once. This is the
    /// one figure the Seams-and-Steps split lowers, and it is named here so that the split changes one constant and not a scatter of numbers.
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

        // Every lookup is a read, and nothing on the idle path takes the writer lock.
        Assert.Equal(1 + Partitions, turn.ReadTransactions);

        Assert.Equal(0, turn.WriterTransactions);
    }

    [Fact]
    public async Task The_statements_of_an_idle_turn_do_not_multiply_with_the_corpus_beyond_its_one_corpus_pass()
    {
        const int Small = 30;

        await using LongRestScaleScenario small = await LongRestScaleScenario.CreateAsync(Small, Partitions, LongRestScaleCompletion.Marked);

        await using LongRestScaleScenario large = await LongRestScaleScenario.CreateAsync(Corpus, Partitions, LongRestScaleCompletion.Marked);

        IdleTurn smaller = await small.RunIdleTurnAsync();

        IdleTurn larger = await large.RunIdleTurnAsync();

        // A statement is the same shape however many times it runs, so growth shows as a shape that ran more often. The corpus pass reads
        // the leaves in pages of 128 and reads each leaf by itself, so over 150 memories rather than 30 its per-leaf statements ran once
        // more for each extra memory, and its paging statement ran once more for each extra page. Nothing else may have grown.
        const int PageSize = 128;

        int perLeaf = (Corpus - Small) * CorpusPassesPerIdleCleanLookup;

        int perPage = ((Corpus / PageSize) - (Small / PageSize)) * CorpusPassesPerIdleCleanLookup;

        List<int> growth = [];

        foreach (string text in smaller.Statements.Keys.Union(larger.Statements.Keys, StringComparer.Ordinal))
        {
            int before = smaller.Statements.GetValueOrDefault(text);

            int after = larger.Statements.GetValueOrDefault(text);

            if (after != before)
            {
                growth.Add(after - before);
            }
        }

        Assert.NotEmpty(growth);

        Assert.All(growth, extra => Assert.True(extra == perLeaf || extra == perPage, $"A statement ran {extra} more times over {Corpus} memories than over {Small}."));

        // The per-leaf statements are the few that read one leaf, and one statement pages; nothing outside them grows.
        Assert.InRange(growth.Count(extra => extra == perLeaf), 1, 12);

        Assert.Equal(1, growth.Count(extra => extra == perPage));

        Assert.Equal(smaller.StatementCount + growth.Sum(), larger.StatementCount);
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
