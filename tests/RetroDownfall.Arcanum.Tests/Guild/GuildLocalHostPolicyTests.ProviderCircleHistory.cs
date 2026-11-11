using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Circle_effect_revalidates_accepted_history_beyond_first_page()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var author = await BindCohortRunAsync(f, owner, members[0]);
        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        long revision = current.Value.Revision;

        for (int index = 0; index < 101; index++)
        {
            AppendSharedNoteRequest note = new(Guid.NewGuid(), revision++, GuildNoteKind.Observation, null,
                "accepted history " + index.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

            var appended = await cohorts.AppendNoteAsync(author, NoteMutation(note), cohort.Id, note, default);

            Assert.True(appended.IsSuccess, appended.Error.Message);
        }

        var freshRoot = await f.Store.GetAsync(owner, root.Quest.Id, default);

        Assert.True(freshRoot.IsSuccess, freshRoot.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, freshRoot.Value);
        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;
        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        int sends = 0;

        Task<Result<string>> Send(CancellationToken _) { sends++; return Task.FromResult(Result<string>.Success("answer")); }

        for (int page = 0; page < 2; page++)
        {
            var prepared = await f.Store.PrepareCircleInputAsync(execution, default);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            var batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

            Assert.Equal(page == 0 ? 100 : 1, batch.Items.Length);

            GuildBudgetEffectRequest effect = new(Guid.NewGuid(), root.Quest.Id, execution.Dispatch.DispatchId, root.Quest.RunId,
                GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 100);

            var accepted = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default, circleInputs: [batch]);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            BillableOperationRecord bill = new(root.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

            var settled = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

            Assert.True(settled.IsSuccess, settled.Error.Message);
        }

        Assert.Equal(2, sends);
        Assert.Equal(101L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));
        Assert.Equal(101L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));
        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));

        Guid lastInbox;
        Guid sourceContent;

        await using (var query = connection.Connection.CreateCommand())
        {
            query.CommandText = """
                SELECT p.InboxId,l.ContentId FROM guild_portal_inputs p
                JOIN guild_inbox i ON i.Id=p.InboxId AND i.EnvelopeId=p.EnvelopeId
                JOIN guild_distribution_notifications n ON n.EnvelopeId=i.EnvelopeId
                JOIN guild_cohort_ledger l ON l.Id=n.ObjectId AND l.CohortId=n.CohortId
                WHERE p.RecipientQuestId=$quest ORDER BY p.InboxId DESC LIMIT 1
                """;
            query.Parameters.AddWithValue("$quest", root.Quest.Id.ToString("D").ToUpperInvariant());

            await using var reader = await query.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            lastInbox = Guid.Parse(reader.GetString(0));
            sourceContent = Guid.Parse(reader.GetString(1));
        }

        await using (var transaction = connection.Connection.BeginTransaction(deferred: false))
        {
            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection.Connection, transaction, sourceContent.ToString("N"), default);

            await using var erase = connection.Connection.CreateCommand();

            erase.Transaction = transaction;
            erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$content";
            erase.Parameters.AddWithValue("$content", sourceContent.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await erase.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        GuildBudgetEffectRequest next = new(Guid.NewGuid(), root.Quest.Id, execution.Dispatch.DispatchId, root.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 100);

        var refused = await f.Store.ExecuteProviderEffectAsync(execution, daily, next, Send, default, circleInputs: null);

        Assert.True(refused.IsFailure);
        Assert.Equal(2, sends);
        Assert.Equal(101L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));
        Assert.Equal(101L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));
        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));
        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_inputs WHERE InboxId='{lastInbox:D}' COLLATE NOCASE"));
    }
}
