using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed class EntryWindowPolicyTests
{

    [Theory]
    [InlineData(50, false, 0, 50)]
    [InlineData(50, true, 10, 50)]
    [InlineData(50, true, 50, 50)]
    [InlineData(50, true, 60, 60)]
    public void WatermarkAware_matches_GetSessionAsync_window(
        int maxMessages,
        bool hasWatermark,
        int afterWatermarkCount,
        int expectedTake)
    {

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.WatermarkAware,
            maxMessages,
            hasWatermark: hasWatermark,
            afterWatermarkCount: afterWatermarkCount);

        Assert.Equal(expectedTake, take);

    }

    [Theory]
    [InlineData(50, 999, 50)]
    [InlineData(100, 0, 100)]
    public void MaxMessagesOnly_returns_maxMessages_ignoring_requestedTake(
        int maxMessages,
        int requestedTake,
        int expectedTake)
    {

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.MaxMessagesOnly,
            maxMessages,
            requestedTake: requestedTake);

        Assert.Equal(expectedTake, take);

    }

    [Theory]
    [InlineData(50, 10, 10)]
    [InlineData(50, 0, 1)]
    [InlineData(50, -5, 1)]
    [InlineData(50, 50, 50)]
    [InlineData(50, 999, 50)]
    public void ClampedTakeLast_clamps_to_one_through_maxMessages(
        int maxMessages,
        int requestedTake,
        int expectedTake)
    {

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.ClampedTakeLast,
            maxMessages,
            requestedTake: requestedTake);

        Assert.Equal(expectedTake, take);

    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(999, 999)]
    public void RawTakeLast_uses_Max_one_requestedTake_without_maxMessages(
        int requestedTake,
        int expectedTake)
    {

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.RawTakeLast,
            maxMessages: 50,
            requestedTake: requestedTake);

        Assert.Equal(expectedTake, take);

    }

}
