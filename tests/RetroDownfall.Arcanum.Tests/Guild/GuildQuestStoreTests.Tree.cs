using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Tree_pages_capture_exact_subtree_without_siblings_or_later_children()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        GuildQuestStored middle = await f.ChildAsync(root, rootDriver);

        GuildQuestStored sibling = await f.ChildAsync(root, rootDriver);

        GuildQuestDriver middleDriver = await f.DriverAsync(middle, "middle-driver");

        GuildQuestStored leaf = await f.ChildAsync(middle, middleDriver);

        var first = await TreeReadAsync(f, middle.Quest.Id, new(PageSize: 1));

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(middle.Quest.Id, Assert.Single(first.Value.Items).Id);

        Assert.Equal(2, first.Value.Summary.MemberCount);

        Assert.Equal("livePerPage", first.Value.StateConsistency);

        Assert.NotNull(first.Value.NextCursor);

        GuildQuestStored later = await f.ChildAsync(middle, middleDriver);

        f.Restart();

        var second = await TreeReadAsync(f, middle.Quest.Id, new(first.Value.NextCursor, 1));

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal(leaf.Quest.Id, Assert.Single(second.Value.Items).Id);

        Assert.Null(second.Value.NextCursor);

        Assert.Equal(first.Value.SnapshotSequence, second.Value.SnapshotSequence);

        Assert.Equal(2, second.Value.Summary.MemberCount);

        var fresh = await TreeReadAsync(f, root.Quest.Id, new(PageSize: 100));

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        Assert.Equal(new[] { root.Quest.Id, middle.Quest.Id, sibling.Quest.Id, leaf.Quest.Id, later.Quest.Id }, fresh.Value.Items.Select(item => item.Id));

        Assert.Equal(5, fresh.Value.Summary.MemberCount);
    }

    [Fact]
    public async Task Tree_cursor_is_bound_to_scope_filter_count_and_caller()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, driver);

        _ = await f.ChildAsync(root, driver);

        var first = await TreeReadAsync(f, root.Quest.Id, new(PageSize: 1));

        Assert.True(first.IsSuccess, first.Error.Message);

        string cursor = first.Value.NextCursor!;

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, child.Quest.Id, new(cursor, 1))).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, root.Quest.Id, new(cursor, 2))).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, root.Quest.Id, new(cursor, 1), QuestState.Working)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, root.Quest.Id, new(cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A"), 1))).Error.Code);

        GuildCallerContext other = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), f.Host.InstallationGeneration);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, root.Quest.Id, new(cursor, 1), caller: other)).Error.Code);
    }

    [Fact]
    public async Task Tree_state_filter_is_live_and_summary_preserves_other_active_members()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored firstChild = await f.ChildAsync(root, driver);

        GuildQuestStored secondChild = await f.ChildAsync(root, driver);

        var first = await TreeReadAsync(f, root.Quest.Id, new(PageSize: 1), QuestState.Submitted);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(firstChild.Quest.Id, Assert.Single(first.Value.Items).Id);

        Assert.Equal(3, first.Value.Summary.ActiveExecutionCount);

        _ = await f.DriverAsync(secondChild, "second-live-driver");

        var second = await TreeReadAsync(f, root.Quest.Id, new(first.Value.NextCursor, 1), QuestState.Submitted);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Empty(second.Value.Items);

        Assert.Equal(3, second.Value.Summary.MemberCount);

        Assert.Equal(3, second.Value.Summary.ActiveExecutionCount);
    }

    [Fact]
    public async Task Tree_retains_erased_member_tombstone_but_erased_cursor_owner_is_gone()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, driver);

        var first = await TreeReadAsync(f, root.Quest.Id, new(PageSize: 1));

        Assert.True(first.IsSuccess, first.Error.Message);

        await f.ExecuteAsync($"UPDATE guild_quests SET Name=NULL,Retired=1,Revision=Revision+1 WHERE Id='{child.Quest.Id:D}' COLLATE NOCASE");

        var second = await TreeReadAsync(f, root.Quest.Id, new(first.Value.NextCursor, 1));

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.True(Assert.Single(second.Value.Items).Retired);

        await f.ExecuteAsync($"UPDATE guild_quests SET Retired=1,Revision=Revision+1 WHERE Id='{root.Quest.Id:D}' COLLATE NOCASE");

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await TreeReadAsync(f, root.Quest.Id, new(first.Value.NextCursor, 1))).Error.Code);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await TreeReadAsync(f, root.Quest.Id, new())).Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(1001)]
    public async Task Tree_rejects_unbounded_page_count(int count)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, (await TreeReadAsync(f, root.Quest.Id, new(PageSize: count))).Error.Code);
    }

    [Fact]
    public async Task Tree_deep_and_wide_pages_have_no_depth_limit_and_use_the_lineage_indexes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored middle = await f.ChildAsync(root, driver);

        List<Guid> deep = await SeedTreeMembersAsync(f, root, middle.Quest.Id, middle.Quest.SignetId, 1100, true);

        List<Guid> wide = await SeedTreeMembersAsync(f, root, root.Quest.Id, root.Quest.SignetId, 105, false);

        HashSet<Guid> found = [];

        string? cursor = null;

        int pages = 0;

        do
        {
            var page = await TreeReadAsync(f, middle.Quest.Id, new(cursor, 100));

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.Equal(1101, page.Value.Summary.MemberCount);

            Assert.InRange(page.Value.Items.Length, 1, 100);

            foreach (GuildQuestTreeNode node in page.Value.Items)
                Assert.True(found.Add(node.Id), "A tree member was repeated across pages.");

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(page.Value, GuildTreeJsonContext.Default.GuildQuestTreePage);

            Assert.InRange(bytes.Length, 1, 1_048_576);

            GuildQuestTreePage restored = JsonSerializer.Deserialize(bytes, GuildTreeJsonContext.Default.GuildQuestTreePage)!;

            Assert.Equal(page.Value.Items.Select(node => node.Id), restored.Items.Select(node => node.Id));

            Assert.Equal(page.Value.Summary, restored.Summary);

            cursor = page.Value.NextCursor;

            pages++;
        } while (cursor is not null);

        Assert.Equal(12, pages);

        Assert.Equal(1101, found.Count);

        Assert.Contains(middle.Quest.Id, found);

        Assert.All(deep, id => Assert.Contains(id, found));

        Assert.All(wide, id => Assert.DoesNotContain(id, found));

        foreach (bool rootQuery in new[] { false, true })
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteCommand plan = connection.CreateCommand();

            plan.CommandText = "EXPLAIN QUERY PLAN " + GuildQuestStore.TreePageSql(rootQuery);

            plan.Parameters.AddWithValue("$root", root.Quest.Id.ToString("D").ToUpperInvariant());

            plan.Parameters.AddWithValue("$subtree", middle.Quest.Id.ToString("D").ToUpperInvariant());

            plan.Parameters.AddWithValue("$snapshot", DBNull.Value);

            plan.Parameters.AddWithValue("$after", 0);

            plan.Parameters.AddWithValue("$afterId", "");

            plan.Parameters.AddWithValue("$state", DBNull.Value);

            plan.Parameters.AddWithValue("$limit", 101);

            List<string> details = [];

            await using SqliteDataReader reader = await plan.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                details.Add(reader.GetString(3));

            Assert.Contains(details, detail => detail.Contains("SEARCH", StringComparison.Ordinal) && detail.Contains("idx_guild_quests_root", StringComparison.Ordinal));

            Assert.Contains(details, detail => detail.Contains("SEARCH effect", StringComparison.Ordinal) && detail.Contains("idx_guild_budget_effects_quest", StringComparison.Ordinal));

            Assert.Contains(details, detail => detail.Contains("SEARCH lease", StringComparison.Ordinal) && detail.Contains("idx_guild_workspace_leases_quest", StringComparison.Ordinal));

            if (!rootQuery)
                Assert.Contains(details, detail => detail.Contains("SEARCH child", StringComparison.Ordinal) && detail.Contains("idx_guild_quests_parent", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Tree_cursor_authenticates_retired_generation_and_rejects_forged_retirement()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        _ = await f.ChildAsync(root, await f.DriverAsync(root));

        var page = await TreeReadAsync(f, root.Quest.Id, new(PageSize: 1));

        Assert.True(page.IsSuccess, page.Error.Message);

        string[] forged = page.Value.NextCursor!.Split('.');

        forged[0] = Guid.NewGuid().ToString("N");

        forged[3] = Guid.NewGuid().ToString("N");

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, (await TreeReadAsync(f, root.Quest.Id, new(string.Join('.', forged), 1))).Error.Code);

        f.Host.InstallationGeneration = Guid.NewGuid();

        Assert.Equal(ErrorCodes.Guild.CursorGone, (await TreeReadAsync(f, root.Quest.Id, new(page.Value.NextCursor, 1))).Error.Code);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await TreeReadAsync(f, root.Quest.Id, new())).Error.Code);
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("version")]
    [InlineData("policy")]
    [InlineData("receipt-digest")]
    public async Task Tree_refuses_corrupt_child_evidence_without_materializing_retained_payloads(string corruption)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var (root, child, _) = await CompletedReceiptChildAsync(f);

        Assert.True((await TreeReadAsync(f, root.Quest.Id, new())).IsSuccess);

        long baselineStart = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildQuestTreePage>> baselineRead = TreeReadAsync(f, root.Quest.Id, new());

        long baseline = GC.GetAllocatedBytesForCurrentThread() - baselineStart;

        Assert.True(baselineRead.IsCompleted, "The baseline requires the same inline SQLite admission path.");

        Assert.True((await baselineRead).IsSuccess);

        if (corruption == "receipt-digest")
        {
            await f.ExecuteAsync("DELETE FROM guild_result_outbox");

            await CorruptQuestMetadataAsync(f, "guild_result_receipts", "Digest", true);
        }
        else
            await f.ExecuteAsync("UPDATE LongRunningOperations SET " + (corruption switch
            {
                "checkpoint" => "CheckpointPayload=x'00'",
                "version" => "CheckpointVersion=2",
                _ => "RecoveryPolicy=3"
            }) + $" WHERE Id='{child.Quest.OperationId:N}'");

        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<GuildQuestTreePage>> pending = TreeReadAsync(f, root.Quest.Id, new());

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pending.IsCompleted, "The allocation probe requires an inline SQLite fixture read.");

        var page = await pending;

        Assert.Equal(ErrorCodes.Guild.Unavailable, page.Error.Code);

        Assert.True(allocated - baseline < 4 * 1024 * 1024,
            $"Corrupt metadata added {allocated - baseline:N0} managed bytes over the {baseline:N0}-byte admitted baseline.");
    }

    [Fact]
    public async Task Tree_terminal_filter_and_unfiltered_summary_preserve_unresolved_descendant_execution()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, driver);

        var completed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("completed-tree-root")),
            root.Quest.Revision, ReceiptResult(root.Quest.Id) with { NoDispatchProven = true }, default);

        Assert.True(completed.IsSuccess, completed.Error.Message);

        var terminal = await TreeReadAsync(f, root.Quest.Id, new(), QuestState.Completed);

        Assert.True(terminal.IsSuccess, terminal.Error.Message);

        Assert.Equal(root.Quest.Id, Assert.Single(terminal.Value.Items).Id);

        Assert.Equal(2, terminal.Value.Summary.MemberCount);

        Assert.Equal(1, terminal.Value.Summary.ActiveExecutionCount);

        Assert.False(terminal.Value.Items[0].CancellationConverged);

        var authoritative = await f.Store.GetAsync(f.Caller, root.Quest.Id, default);

        Assert.True(authoritative.IsSuccess, authoritative.Error.Message);

        Assert.Equal(authoritative.Value.Quest.State, terminal.Value.Items[0].State);

        Assert.Equal(authoritative.Value.Quest.ReconciliationRequired, terminal.Value.Items[0].ReconciliationRequired);

        Assert.NotEqual(child.Quest.Id, terminal.Value.Items[0].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tree_convergence_rechecks_descendant_physical_ownership_separately_from_paid_unknowns(bool physicalLease)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, driver);

        var childResult = await f.Store.CommitResultAsync(await f.DriverAsync(child, "tree-stopped-child"),
            new(Guid.NewGuid(), ReceiptDigest("tree-child-terminal")), child.Quest.Revision,
            ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(childResult.IsSuccess, childResult.Error.Message);

        GuildCancelQuestRequest request = new(Guid.NewGuid(), root.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var cancellation = await f.Store.RequestCancellationAsync(f.Caller,
            new(request.IdempotencyKey, GuildQuestIntent.Compute(root.Quest.Id, request).Value), root.Quest.Id, request, default);

        Assert.True(cancellation.IsSuccess, cancellation.Error.Message);

        driver = driver with { OperationRevision = (await ReadFrontierOperationAsync(f, root)).Revision };

        GuildQuestResult stopped = new(new(root.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Canceled, "operation.cancelled"), null, NoDispatchProven: true, WorkStoppedProven: true);

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("tree-root-stopped")),
            cancellation.Value.Revision, stopped, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        await f.ExecuteAsync($"UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1 WHERE Id='{child.Quest.Id:D}' COLLATE NOCASE");

        if (physicalLease)
            await SeedCancellationLeaseAsync(f, child, GuildWorkspaceLeaseState.Held);

        var page = await TreeReadAsync(f, root.Quest.Id, new());

        Assert.True(page.IsSuccess, page.Error.Message);

        GuildQuestTreeNode parentNode = page.Value.Items.Single(node => node.Id == root.Quest.Id);

        Assert.Equal(!physicalLease, parentNode.CancellationConverged);

        Assert.Equal(physicalLease ? 2 : 0, page.Value.Summary.ActiveExecutionCount);

        Assert.Equal(1, page.Value.Summary.AccountingUnresolvedCount);

        Assert.Equal(QuestState.Completed, page.Value.Items.Single(node => node.Id == child.Quest.Id).State);

        Assert.Equal(physicalLease, parentNode.ReconciliationRequired);
    }

    [Fact]
    public async Task Tree_run_cannot_enumerate_an_ancestor_or_sibling_subtree()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver rootDriver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, rootDriver);

        GuildQuestStored sibling = await f.ChildAsync(root, rootDriver);

        GuildQuestDriver childDriver = await f.DriverAsync(child, "tree-scoped-child");

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value, Guid.NewGuid(), f.Host.InstallationGeneration, childDriver.Execution);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await TreeReadAsync(f, root.Quest.Id, new(), caller: caller)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await TreeReadAsync(f, sibling.Quest.Id, new(), caller: caller)).Error.Code);

        var own = await TreeReadAsync(f, child.Quest.Id, new(), caller: caller);

        Assert.True(own.IsSuccess, own.Error.Message);

        Assert.Equal(child.Quest.Id, Assert.Single(own.Value.Items).Id);
    }

    [Theory]
    [InlineData("empty-question")]
    [InlineData("whitespace-failure")]
    [InlineData("empty-result")]
    public async Task Tree_state_projection_preserves_the_authoritative_uncertainty_rules(string evidence)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestStored child = await f.ChildAsync(root, await f.DriverAsync(root));

        string id = child.Quest.Id.ToString("D").ToUpperInvariant();

        if (evidence == "empty-question")
            await f.ExecuteAsync($"""
                PRAGMA ignore_check_constraints=ON;
                UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,
                    QuestionId='00000000-0000-0000-0000-000000000000',ContinuationId='{Guid.NewGuid():D}',Revision=Revision+1
                    WHERE Id='{id}';
                UPDATE LongRunningOperations SET State=2 WHERE Id='{child.Quest.OperationId:N}';
                PRAGMA ignore_check_constraints=OFF;
                """);
        else
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"""
                PRAGMA ignore_check_constraints=ON;
                INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                VALUES('{id}','{(evidence == "empty-result" ? Guid.Empty : Guid.NewGuid()).ToString("D").ToUpperInvariant()}',zeroblob(32),4,$reason,1,1,
                    '{Guid.NewGuid().ToString("D").ToUpperInvariant()}',2,'2026-11-08T00:00:00.0000000Z');
                UPDATE LongRunningOperations SET State=5,TerminalErrorCode=$reason WHERE Id='{child.Quest.OperationId:N}';
                PRAGMA ignore_check_constraints=OFF;
                """;

            command.Parameters.AddWithValue("$reason", evidence == "empty-result" ? ErrorCodes.Guild.Unavailable : " \t\r\n");

            _ = await command.ExecuteNonQueryAsync();
        }

        var single = await f.Store.GetAsync(f.Caller, child.Quest.Id, default);

        Assert.True(single.IsSuccess, single.Error.Message);

        Assert.True(single.Value.Quest.ReconciliationRequired);

        var page = await TreeReadAsync(f, root.Quest.Id, new());

        Assert.True(page.IsSuccess, page.Error.Message);

        GuildQuestTreeNode node = page.Value.Items.Single(item => item.Id == child.Quest.Id);

        Assert.Equal(single.Value.Quest.State, node.State);

        Assert.True(node.ReconciliationRequired);

        Assert.Equal(1, page.Value.Summary.ReconciliationRequiredCount);
    }

    private static async Task<List<Guid>> SeedTreeMembersAsync(Fixture f, GuildQuestStored template,
        Guid firstParent, Guid firstParentSignet, int count, bool chain)
    {
        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        Dictionary<string, string[]> columns = [];

        foreach (string table in new[] { "LongRunningOperations", "guild_signets", "guild_content", "guild_quests" })
        {
            await using SqliteCommand shape = connection.CreateCommand();

            shape.Transaction = transaction;

            shape.CommandText = "PRAGMA table_info(" + table + ")";

            List<string> names = [];

            await using SqliteDataReader reader = await shape.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                names.Add(reader.GetString(1));

            columns.Add(table, names.ToArray());
        }

        await using SqliteCommand parentRead = connection.CreateCommand();

        parentRead.Transaction = transaction;

        parentRead.CommandText = "SELECT OperationId FROM guild_quests WHERE Id=$id";

        parentRead.Parameters.AddWithValue("$id", firstParent.ToString("D").ToUpperInvariant());

        string parentOperation = Assert.IsType<string>(await parentRead.ExecuteScalarAsync());

        Guid parent = firstParent, parentSignet = firstParentSignet;

        List<Guid> ids = [];

        for (int index = 0; index < count; index++)
        {
            Guid id = Guid.NewGuid(), operation = Guid.NewGuid(), signet = Guid.NewGuid(), run = Guid.NewGuid(), content = Guid.NewGuid();

            await using SqliteCommand clone = connection.CreateCommand();

            clone.Transaction = transaction;

            clone.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$operation", operation.ToString("N"));

            clone.Parameters.AddWithValue("$signet", signet.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$run", run.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$parent", parent.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$parentSignet", parentSignet.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$parentOperation", parentOperation);

            clone.Parameters.AddWithValue("$content", content.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$checkpoint", GuildQuestCheckpoint.Encode(id));

            clone.Parameters.AddWithValue("$sourceQuest", template.Quest.Id.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$sourceOperation", template.Quest.OperationId.ToString("N"));

            clone.Parameters.AddWithValue("$sourceSignet", template.Quest.SignetId.ToString("D").ToUpperInvariant());

            clone.Parameters.AddWithValue("$sourceContent", template.Quest.GoalRef.ContentId.ToString("D").ToUpperInvariant());

            foreach (string table in columns.Keys)
            {
                Dictionary<string, string> replace = table switch
                {
                    "LongRunningOperations" => new() { ["Id"] = "$operation", ["State"] = "0", ["RunId"] = "$run", ["RootOperationId"] = "$sourceOperation", ["ParentOperationId"] = "$parentOperation", ["CheckpointPayload"] = "$checkpoint", ["CheckpointReference"] = "$id", ["LeaseOwner"] = "NULL", ["LeaseExpiresAt"] = "NULL" },
                    "guild_signets" => new() { ["SignetId"] = "$signet", ["QuestId"] = "$id", ["RunId"] = "$run", ["IssuerSignetId"] = "$parentSignet" },
                    "guild_content" => new() { ["ContentId"] = "$content", ["SourceRecordId"] = "$id" },
                    _ => new() { ["Id"] = "$id", ["OperationId"] = "$operation", ["RunId"] = "$run", ["SignetId"] = "$signet", ["GoalContentId"] = "$content", ["ParentQuestId"] = "$parent", ["IssuerKindCode"] = "1", ["IssuerIdentity"] = "$parent", ["LogicalCallId"] = "$id", ["IssuingStepId"] = "$id", ["MutationId"] = "$id", ["IssuedSequence"] = "(SELECT MAX(IssuedSequence)+1 FROM guild_quests)" }
                };

                string key = table switch { "LongRunningOperations" => "Id=$sourceOperation", "guild_signets" => "SignetId=$sourceSignet", "guild_content" => "ContentId=$sourceContent", _ => "Id=$sourceQuest" };

                clone.CommandText = "INSERT INTO " + table + "(" + string.Join(',', columns[table]) + ") SELECT "
                    + string.Join(',', columns[table].Select(column => replace.GetValueOrDefault(column, column))) + " FROM " + table + " WHERE " + key;

                Assert.Equal(1, await clone.ExecuteNonQueryAsync());
            }

            ids.Add(id);

            if (chain)
            {
                parent = id;

                parentSignet = signet;

                parentOperation = operation.ToString("N");
            }
        }

        await transaction.CommitAsync();

        return ids;
    }

    private static Task<Result<GuildQuestTreePage>> TreeReadAsync(Fixture f, Guid root, GuildPageQuery query,
        QuestState? state = null, GuildCallerContext? caller = null)
    {
        return f.Store.GetTreeAsync(caller ?? f.Caller, root, query, state, CancellationToken.None);
    }
}
