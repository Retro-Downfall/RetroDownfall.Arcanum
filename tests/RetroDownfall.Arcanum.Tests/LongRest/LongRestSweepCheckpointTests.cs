using System.Text;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestSweepCheckpointTests
{
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string Initial = """
        {"version":1,"operationId":"11111111-2222-3333-4444-555555555555","scope":"Global","campaignId":null,"generationId":null,"phase":null,"revision":0,"committedItemCursor":null,"committedWorkDigest":null,"protectedPartitionDigest":null}
        """;

    [Fact]
    public void Reads_exact_initial_checkpoint_without_reconstructing_authority()
    {
        var decoded = LongRestSweepCheckpointCodec.Decode(Operation, Encoding.UTF8.GetBytes(Initial));

        Assert.True(decoded.IsSuccess);

        Assert.Equal(new LongRestSweepCheckpoint(1, Operation, LongRestSweepScopeKind.Global, null, null, null, 0, null, null), decoded.Value);
    }

    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"campaignId\":null", "\"campaignId\":\"11111111-2222-3333-4444-555555555555\"")]
    [InlineData("\"phase\":null", "\"phase\":\"Hierarchy\"")]
    [InlineData("\"revision\":0", "\"revision\":-1")]
    [InlineData("\"version\":1", "\"version\":1,\"authority\":true")]
    public void Refuses_inconsistent_or_unknown_checkpoint_coordinates(string before, string after) =>
        Assert.True(LongRestSweepCheckpointCodec.Decode(Operation, Encoding.UTF8.GetBytes(Initial.Replace(before, after, StringComparison.Ordinal))).IsFailure);

    [Fact]
    public void Refuses_other_operation_oversized_invalid_utf8_and_overlong_cursor()
    {
        Assert.True(LongRestSweepCheckpointCodec.Decode(Guid.NewGuid(), Encoding.UTF8.GetBytes(Initial)).IsFailure);

        Assert.True(LongRestSweepCheckpointCodec.Decode(Operation, new byte[LongRestSweepCheckpointCodec.MaxCheckpointBytes + 1]).IsFailure);

        Assert.True(LongRestSweepCheckpointCodec.Decode(Operation, [0xff]).IsFailure);

        string cursor = new('x', LongRestPolicy.MaxIdentityBytes + 1);

        Assert.True(LongRestSweepCheckpointCodec.Decode(Operation, Encoding.UTF8.GetBytes(Initial.Replace("\"committedItemCursor\":null", $"\"committedItemCursor\":\"{cursor}\"", StringComparison.Ordinal))).IsFailure);
    }
}
