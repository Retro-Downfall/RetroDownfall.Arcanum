using System.CommandLine;

using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildSagaReview(DeferredHandler<MemoryCommands> handler)
    {
        Command review = ReviewRoot("Saga");

        Command list = new("list", "List unreviewed Saga versions in one exact scope, newest first.");

        Option<Guid?> campaign = ReviewCampaignOption();

        Option<bool> unresolved = new("--unresolved")
        {
            Description = "Review legacy Saga versions whose Campaign scope could not be resolved.",
        };

        (Option<int?> limit, Option<string?> cursor) = ReviewPageOptions();

        list.Add(campaign);
        list.Add(unresolved);
        list.Add(limit);
        list.Add(cursor);

        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Value.SagaReviewList(
                pr.GetValue(campaign),
                pr.GetValue(unresolved),
                pr.GetValue(limit) ?? MemoryReviewLimits.MaxPageSize,
                pr.GetValue(cursor),
                ct).ConfigureAwait(false));

        review.Add(list);
        review.Add(ReviewApply("Saga", (first, cancellationToken) => handler.Value.SagaReviewApply(first, cancellationToken)));

        return review;
    }

    private static Command BuildLexiconReview(DeferredHandler<MemoryCommands> handler)
    {
        Command review = ReviewRoot("Lexicon");

        Command list = new("list", "List unreviewed Lexicon versions in one exact scope, newest first.");

        Option<Guid?> campaign = ReviewCampaignOption();

        (Option<int?> limit, Option<string?> cursor) = ReviewPageOptions();

        list.Add(campaign);
        list.Add(limit);
        list.Add(cursor);

        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Value.LexiconReviewList(
                pr.GetValue(campaign),
                pr.GetValue(limit) ?? MemoryReviewLimits.MaxPageSize,
                pr.GetValue(cursor),
                ct).ConfigureAwait(false));

        review.Add(list);
        review.Add(ReviewApply("Lexicon", (first, cancellationToken) => handler.Value.LexiconReviewApply(first, cancellationToken)));

        return review;
    }

    private static Command BuildCovenantReview(DeferredHandler<MemoryCommands> handler)
    {
        Command review = ReviewRoot("Covenant");

        Command list = new("list", "List unreviewed Covenant versions in one exact scope and lane, newest first.");

        Option<Guid?> campaign = ReviewCampaignOption();

        Option<string> lane = new("--lane")
        {
            Description = "Exact Covenant lane: confirmed or proposed.",
            Required = true,
        };

        (Option<int?> limit, Option<string?> cursor) = ReviewPageOptions();

        list.Add(campaign);
        list.Add(lane);
        list.Add(limit);
        list.Add(cursor);

        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Value.CovenantReviewList(
                pr.GetValue(campaign),
                pr.GetValue(lane)!,
                pr.GetValue(limit) ?? MemoryReviewLimits.MaxPageSize,
                pr.GetValue(cursor),
                ct).ConfigureAwait(false));

        review.Add(list);
        review.Add(ReviewApply("Covenant", (first, cancellationToken) => handler.Value.CovenantReviewApply(first, cancellationToken)));

        return review;
    }

    private static Command ReviewRoot(string store) =>
        new("review", $"Review newly written {store} versions without gating their normal eligibility.");

    private static Command ReviewApply(
        string store,
        Func<string, CancellationToken, Task<int>> apply)
    {
        Command command = new(
            "apply",
            $"Prepare, confirm, and atomically apply one exact-version {store} review decision file.");

        Option<string> file = new("--file", "-f")
        {
            Description = "JSON review request path, or - for stdin with --yes.",
            Required = true,
        };

        command.Add(file);

        command.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await apply(pr.GetValue(file)!, ct).ConfigureAwait(false));

        return command;
    }

    private static Option<Guid?> ReviewCampaignOption() => new("--campaign", "-C")
    {
        Description = "Exact Campaign GUID. Omit for exact Global scope.",
    };

    private static (Option<int?> Limit, Option<string?> Cursor) ReviewPageOptions()
    {
        Option<int?> limit = new("--limit")
        {
            Description = $"Maximum versions to return, from 1 through {MemoryReviewLimits.MaxPageSize}.",
        };

        Option<string?> cursor = new("--cursor")
        {
            Description = "Opaque continuation cursor returned by the preceding page.",
        };

        return (limit, cursor);
    }
}
