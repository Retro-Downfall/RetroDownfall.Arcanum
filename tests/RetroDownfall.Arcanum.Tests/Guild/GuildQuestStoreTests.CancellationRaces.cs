using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_child_issue_serialize_at_the_admitted_transaction_without_touching_siblings(bool childFirst)
    {
        await using Fixture f = await Fixture.CreateAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        CancellationToken ct = deadline.Token;

        var root = await ReceiptRootAsync(f);

        var rootDriver = await f.DriverAsync(root);

        var middle = await f.ChildAsync(root, rootDriver);

        var middleDriver = await f.DriverAsync(middle);

        var sibling = await f.ChildAsync(root, rootDriver);

        GuildCallerContext childCaller = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value,
            Guid.NewGuid(), f.Host.InstallationGeneration, middleDriver.Execution);

        Guid key = Guid.NewGuid();

        GuildIssuanceIdentity identity = new(f.Host.InstallationGeneration, middle.Quest.Id, null, Guid.NewGuid(), key);

        GuildIssueChildRequest childRequest = new(key, middle.Quest.Revision, null, "Racing child", "Do work", new());

        GuildCancelQuestRequest cancelRequest = new(Guid.NewGuid(), middle.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(cancelRequest.IdempotencyKey, GuildQuestIntent.Compute(middle.Quest.Id, cancelRequest).Value);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int locateCount = 0;

        f.Host.BeforeResolve = async (caller, token) =>
        {
            if (childFirst && ReferenceEquals(caller, childCaller))
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        f.Host.BeforeLocate = async (caller, quest, token) =>
        {
            // Cancellation first locates, then authorizes again inside its root write transaction.
            if (!childFirst && ReferenceEquals(caller, f.Caller) && quest == middle.Quest.Id
                && Interlocked.Increment(ref locateCount) == 2)
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        Task<Result<GuildQuestStored>> Issue() => f.Store.IssueAsync(childCaller, identity, middle.Quest.Id, childRequest, ct);

        Task<Result<GuildCancellationDto>> Cancel() => f.Store.RequestCancellationAsync(f.Caller, mutation,
            middle.Quest.Id, cancelRequest, ct);

        Task<Result<GuildQuestStored>> issue;

        Task<Result<GuildCancellationDto>> cancel;

        try
        {
            if (childFirst)
            {
                issue = Issue();

                await entered.Task.WaitAsync(ct);

                cancel = Task.Run(Cancel, ct);

                Assert.False(cancel.IsCompleted);
            }
            else
            {
                cancel = Cancel();

                await entered.Task.WaitAsync(ct);

                issue = Task.Run(Issue, ct);

                Assert.False(issue.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        var issued = await issue;

        var canceled = await cancel;

        Assert.True(canceled.IsSuccess, canceled.Error.Message);

        Assert.Equal(childFirst, issued.IsSuccess);

        f.Host.BeforeLocate = null;

        f.Host.BeforeResolve = null;

        var page = await f.Store.ReadCancellationWorkPageAsync(f.Caller, middle.Quest.Id, 0, ct);

        Assert.True(page.IsSuccess, page.Error.Message);

        if (childFirst) Assert.Equal(issued.Value.Quest.Id, Assert.Single(page.Value).QuestId);
        else Assert.Empty(page.Value);

        Assert.Equal(childFirst ? 4 : 3, await f.CountAsync("guild_quests"));

        Assert.Equal(childFirst ? 4 : 3, await f.CountAsync("fixture_allocations"));

        Assert.False((await f.Store.GetAsync(f.Caller, root.Quest.Id, ct)).Value.Quest.CancellationRequested);

        Assert.False((await f.Store.GetAsync(f.Caller, sibling.Quest.Id, ct)).Value.Quest.CancellationRequested);

        _ = await f.ChildAsync(sibling, await f.DriverAsync(sibling));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_result_commit_preserve_the_winning_receipt_and_revision(bool resultFirst)
    {
        await using Fixture f = await Fixture.CreateAsync();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        CancellationToken ct = deadline.Token;

        var row = await ReceiptRootAsync(f);

        var driver = await f.DriverAsync(row);

        GuildCallerContext run = new(GuildCallerKind.Run, f.Caller.PrincipalId!.Value,
            Guid.NewGuid(), f.Host.InstallationGeneration, driver.Execution);

        driver = driver with { Caller = run };

        GuildQuestResult result = ReceiptResult(row.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true };

        GuildCancelQuestRequest request = new(Guid.NewGuid(), row.Quest.Revision, GuildCancellationReason.OperatorRequested);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int cancelLocates = 0;

        f.Host.BeforeLocate = async (caller, _, token) =>
        {
            if (resultFirst && ReferenceEquals(caller, run)
                || !resultFirst && ReferenceEquals(caller, f.Caller) && Interlocked.Increment(ref cancelLocates) == 2)
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        Task<Result<GuildResultReceipt>> Complete() => f.Store.CommitResultAsync(driver,
            new(Guid.NewGuid(), ReceiptDigest("race")), row.Quest.Revision, result, ct);

        Task<Result<GuildCancellationDto>> Cancel() => f.Store.RequestCancellationAsync(f.Caller,
            new(request.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, request).Value), row.Quest.Id, request, ct);

        Task<Result<GuildResultReceipt>> completion;

        Task<Result<GuildCancellationDto>> cancellation;

        try
        {
            if (resultFirst)
            {
                completion = Complete();

                await entered.Task.WaitAsync(ct);

                cancellation = Task.Run(Cancel, ct);

                Assert.False(cancellation.IsCompleted);
            }
            else
            {
                cancellation = Cancel();

                await entered.Task.WaitAsync(ct);

                completion = Task.Run(Complete, ct);

                Assert.False(completion.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(resultFirst, (await completion).IsSuccess);

        Assert.Equal(!resultFirst, (await cancellation).IsSuccess);

        f.Host.BeforeLocate = null;

        f.Restart();

        var current = (await f.Store.GetAsync(f.Caller, row.Quest.Id, ct)).Value.Quest;

        Assert.Equal(resultFirst ? QuestState.Completed : QuestState.Submitted, current.State);

        Assert.Equal(!resultFirst, current.CancellationRequested);

        Assert.Equal(resultFirst ? result.Receipt.ResultId : null, current.ResultReceiptId);

        Assert.Equal(resultFirst ? 1 : 0, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(resultFirst ? 1 : 0, await f.CountAsync("fixture_settlements"));
    }
}
