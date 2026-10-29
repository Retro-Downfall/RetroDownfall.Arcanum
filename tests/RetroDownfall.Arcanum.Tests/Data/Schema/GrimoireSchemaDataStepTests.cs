using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class GrimoireSchemaDataStepTests
{
    [Fact]
    public void A_data_only_step_accepts_a_named_bounded_backfill()
    {
        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: new TestBackfill("quota-census")) with
        {
            Statements = [],
        };

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.ChainWithSteps(2, step);

        Assert.Empty(Assert.Single(chain.Steps).Statements);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("name-that-is-deliberately-longer-than-the-sixty-four-character-persisted-journal-limit")]
    public void A_backfill_name_outside_the_journal_contract_is_refused(string name)
    {
        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: new TestBackfill(name));

        Assert.Throws<InvalidOperationException>(() => GrimoireSchemaEvolutionFixture.ChainWithSteps(2, step));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_backfill_without_a_positive_source_page_bound_is_refused(int rows)
    {
        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: new TestBackfill("quota-census", rows));

        Assert.Throws<InvalidOperationException>(() => GrimoireSchemaEvolutionFixture.ChainWithSteps(2, step));
    }

    [Fact]
    public void A_step_with_neither_statements_nor_backfill_is_still_refused()
    {
        GrimoireSchemaVersionStep step = GrimoireSchemaEvolutionFixture.Step(1, 2) with { Statements = [] };

        Assert.Throws<InvalidOperationException>(() => GrimoireSchemaEvolutionFixture.ChainWithSteps(2, step));
    }
}
