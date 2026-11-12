using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Legacy_signet_receipt_with_no_acceptance_evidence_remains_unknown_on_replay()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        var issued = await f.IssueAsync(owner, f.Request);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Guid signet = issued.Value.Quest.SignetId;

        long revision = issued.Value.Quest.SignetRevision;

        Guid key = Guid.NewGuid();

        string digest = new('A', 64);

        await f.ExecuteAsync($"""
            INSERT INTO guild_signet_revocations(SignetId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,ReasonCode,Revision,Generation)
            VALUES('{signet.ToString("D").ToUpperInvariant()}','{key.ToString("D").ToUpperInvariant()}','{digest}',{revision},'operatorRequested',{revision + 1},1)
            """);

        GuildRevokeSignetRequest request = new(key, revision, GuildCancellationReason.OperatorRequested);

        var replay = await f.Authority.RevokeAsync(owner, new(key, digest), signet, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.NotNull(replay.Value.Receipt);

        Assert.True(replay.Value.Receipt.Replayed);

        Assert.Null(typeof(GuildMutationReceiptDto).GetProperty("AcceptedAt")!.GetValue(replay.Value.Receipt));

        Assert.NotEqual(Guid.Empty, replay.Value.Receipt.Id);

        var again = await f.Authority.RevokeAsync(owner, new(key, digest), signet, request, default);

        Assert.True(again.IsSuccess, again.Error.Message);

        Assert.Equal(replay.Value.Receipt.Id, again.Value.Receipt!.Id);
    }
}
