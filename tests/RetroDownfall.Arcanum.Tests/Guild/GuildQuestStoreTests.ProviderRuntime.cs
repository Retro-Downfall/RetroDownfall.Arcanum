using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Qualified_sterile_runtime_uses_committed_input_and_physical_accounting_without_history_or_tools(bool resolveUnpriced)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));

        f.UseBudgetParticipant(new GuildBudgetLedger(daily));

        GuildQuestStored quest = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Sterile, quest.Quest.GoalRef, "test", "wizard-test-model", [], 30);

        Result<GuildLocalExecutionBinding> bound = await f.Authority.InQuestTransactionAsync<GuildLocalExecutionBinding>(quest.Quest.RootQuestId,
            (c, t, _, ct) => GuildLocalExecutionStore.CreateWithinAsync(c, t, quest, input, null, DateTimeOffset.UtcNow, ct), default);

        Assert.True(bound.IsSuccess, bound.Error.Message);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(quest);

        GuildQuestDispatchRecord dispatch = (await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("runtime"), quest.Quest.RunId, false, default)).Value;

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var fixture = await wizard.CreateGuildRuntimeAsync(daily);

            var missing = await fixture.Runtime.ExecuteAsync(null!, input, default);

            Assert.True(missing.IsFailure);

            Assert.Equal(0, fixture.Calls());

            var mismatch = await fixture.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input with { Model = "changed" }, default);

            Assert.True(mismatch.IsFailure);

            Assert.Equal(0, fixture.Calls());

            var result = await fixture.Runtime.ExecuteAsync(new(f.Store, driver, dispatch), input, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("qualified answer", result.Value.Text);

            Assert.Equal(1, fixture.Calls());

            Assert.Single(fixture.Messages());

            Assert.Contains(f.Request.Goal, fixture.Messages()[0].Text, StringComparison.Ordinal);

            Assert.True(fixture.Options()?.Tools is null or { Count: 0 });

            Assert.Equal(30, fixture.Options()?.MaxOutputTokens);

            Assert.Equal(1L, await f.CountAsync("InferenceRuns"));

            Assert.Equal(1L, await f.CountAsync("BillableOperations"));

            Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));

            Assert.Equal(0L, await f.CountAsync("Entries"));

            GuildQuestStored current = (await f.Store.GetAsync(f.Caller, quest.Quest.Id, default)).Value;

            GuildQuestResult terminal = ReceiptResult(quest.Quest.Id) with { WorkStoppedProven = true };

            GuildMutationIdentity mutation = new(Guid.NewGuid(), ReceiptDigest("runtime terminal"));

            Assert.True((await f.Store.CommitResultAsync(driver, mutation, current.Quest.Revision, terminal, default)).IsFailure);

            async Task<long> RunStatus()
            {
                await using SqliteCommand status = connection.CreateCommand();

                status.CommandText = "SELECT Status FROM InferenceRuns";

                return (long)(await status.ExecuteScalarAsync())!;
            }

            Assert.Equal(0L, await RunStatus());

            var confirmed = resolveUnpriced
                ? await f.Store.ResolveLocalDispatchAsync(driver, dispatch.DispatchId, default)
                : await f.Store.RecordDispatchEvidenceAsync(driver,
                    new(dispatch.DispatchId, dispatch.Revision + 1, GuildDispatchEvidence.Confirmed, bound.Value.ApprenticeId), default);

            Assert.True(confirmed.IsSuccess, confirmed.Error.Message);

            current = (await f.Store.GetAsync(f.Caller, quest.Quest.Id, default)).Value;

            Assert.True((await f.Store.CommitResultAsync(driver, mutation,
                current.Quest.Revision + 1, terminal, default)).IsFailure);

            Assert.Equal(0L, await RunStatus());

            Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

            var committed = await f.Store.CommitResultAsync(driver, mutation, current.Quest.Revision, terminal, default);

            Assert.True(committed.IsSuccess, committed.Error.Message);

            Assert.Equal(1L, await RunStatus());

            Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }
}
