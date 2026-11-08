using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildQuestMutationIntentTests
{
    [Fact]
    public void Cancellation_intent_binds_exact_target_revision_and_reason_but_not_retry_key()
    {
        Guid target = Guid.NewGuid();

        GuildCancelQuestRequest request = new(Guid.NewGuid(), 3, GuildCancellationReason.OperatorRequested);

        string digest = GuildQuestIntent.Compute(target, request).Value;

        Assert.Equal(digest, GuildQuestIntent.Compute(target, request with { IdempotencyKey = Guid.NewGuid() }).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(Guid.NewGuid(), request).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(target, request with { ExpectedRevision = 4 }).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(target, request with { ReasonCode = GuildCancellationReason.OwnerDeleted }).Value);

        Assert.True(GuildQuestIntent.Compute(Guid.Empty, request).IsFailure);

        Assert.True(GuildQuestIntent.Compute(target, request with { ReasonCode = (GuildCancellationReason)99 }).IsFailure);
    }

    [Fact]
    public void Continuation_intent_binds_exact_content_question_revision_and_target()
    {
        Guid target = Guid.NewGuid();

        GuildContinueRequest request = new(Guid.NewGuid(), 3, Guid.NewGuid(), "text/plain", "answer\r\nline two");

        string digest = GuildQuestIntent.Compute(target, request).Value;

        Assert.Equal(digest, GuildQuestIntent.Compute(target, request with { IdempotencyKey = Guid.NewGuid() }).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(Guid.NewGuid(), request).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(target, request with { ExpectedRevision = 4 }).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(target, request with { ContinuationId = Guid.NewGuid() }).Value);

        Assert.NotEqual(digest, GuildQuestIntent.Compute(target, request with { Body = "answer\nline two" }).Value);

        Assert.True(GuildQuestIntent.Compute(target, request with { ExpectedRevision = 0 }).IsFailure);

        Assert.True(GuildQuestIntent.Compute(target, request with { MimeType = "application/octet-stream" }).IsFailure);
    }
}
