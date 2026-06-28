using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class SanctumBreachStoreTests
{

    [Fact]
    public void GetSnapshot_UnknownCampaign_ReturnsEmpty()
    {

        SanctumBreachStore store = new();

        IReadOnlyList<SanctumBreach> snapshot = store.GetSnapshot("missing-campaign", limit: 10);

        Assert.Empty(snapshot);

    }

    [Fact]
    public void Record_ThenGetSnapshot_ReturnsRecordedBreach()
    {

        SanctumBreachStore store = new();

        SanctumBreach breach = CreateBreach("campaign-a", "PathEscape", detail: "escaped");

        store.Record(breach);

        IReadOnlyList<SanctumBreach> snapshot = store.GetSnapshot("campaign-a", limit: 10);

        Assert.Single(snapshot);

        Assert.Equal(breach.BreachId, snapshot[0].BreachId);

        Assert.Equal("PathEscape", snapshot[0].BreachType);

    }

    [Fact]
    public void GetSnapshot_LimitLessThanCount_ReturnsMostRecentEntries()
    {

        SanctumBreachStore store = new();

        const string campaignId = "campaign-limit";

        for (int i = 0; i < 5; i++)
        {
            store.Record(CreateBreach(campaignId, "NetworkEgress", detail: $"breach-{i}"));
        }

        IReadOnlyList<SanctumBreach> snapshot = store.GetSnapshot(campaignId, limit: 2);

        Assert.Equal(2, snapshot.Count);

        Assert.Equal("breach-3", snapshot[0].Detail);

        Assert.Equal("breach-4", snapshot[1].Detail);

    }

    [Fact]
    public void GetSnapshot_LimitAtLeastCount_ReturnsAllInChronologicalOrder()
    {

        SanctumBreachStore store = new();

        const string campaignId = "campaign-full";

        store.Record(CreateBreach(campaignId, "DisabledTool", detail: "first"));

        store.Record(CreateBreach(campaignId, "DisabledTool", detail: "second"));

        IReadOnlyList<SanctumBreach> snapshot = store.GetSnapshot(campaignId, limit: 10);

        Assert.Equal(2, snapshot.Count);

        Assert.Equal("first", snapshot[0].Detail);

        Assert.Equal("second", snapshot[1].Detail);

    }

    [Fact]
    public void Record_MoreThanMaxCapacity_RetainsMostRecentBreaches()
    {

        SanctumBreachStore store = new();

        const string campaignId = "campaign-ring";

        const int total = 1002;

        for (int i = 0; i < total; i++)
        {
            store.Record(CreateBreach(campaignId, "PathEscape", detail: $"entry-{i}"));
        }

        IReadOnlyList<SanctumBreach> snapshot = store.GetSnapshot(campaignId, limit: 1000);

        Assert.Equal(1000, snapshot.Count);

        Assert.Equal("entry-2", snapshot[0].Detail);

        Assert.Equal("entry-1001", snapshot[^1].Detail);

    }

    [Fact]
    public void Record_IsolatesBreachesByCampaignId()
    {

        SanctumBreachStore store = new();

        store.Record(CreateBreach("campaign-one", "PathEscape", detail: "one"));

        store.Record(CreateBreach("campaign-two", "NetworkEgress", detail: "two"));

        IReadOnlyList<SanctumBreach> first = store.GetSnapshot("campaign-one", limit: 10);

        IReadOnlyList<SanctumBreach> second = store.GetSnapshot("campaign-two", limit: 10);

        Assert.Single(first);

        Assert.Single(second);

        Assert.Equal("one", first[0].Detail);

        Assert.Equal("two", second[0].Detail);

    }

    [Fact]
    public void Record_MoreThanMaxTrackedCampaigns_EvictsLeastRecentlyUsedCampaignKey()
    {

        SanctumBreachStore store = new();

        store.Record(CreateBreach("campaign-first", "PathEscape", detail: "first"));

        for (int i = 0; i < SanctumBreachStore.MaxTrackedCampaigns; i++)
        {

            store.Record(CreateBreach($"campaign-{i}", "PathEscape", detail: $"c-{i}"));

        }

        Assert.Empty(store.GetSnapshot("campaign-first", limit: 10));

        Assert.Single(store.GetSnapshot("campaign-255", limit: 10));

    }

    [Fact]
    public void GetSnapshot_PromotesCampaignSoItSurvivesSubsequentEvictionPressure()
    {

        SanctumBreachStore store = new();

        store.Record(CreateBreach("campaign-keep", "PathEscape", detail: "keep"));

        for (int i = 0; i < SanctumBreachStore.MaxTrackedCampaigns - 1; i++)
        {

            store.Record(CreateBreach($"campaign-fill-{i}", "PathEscape", detail: $"fill-{i}"));

        }

        Assert.Single(store.GetSnapshot("campaign-keep", limit: 10));

        store.Record(CreateBreach("campaign-newest", "PathEscape", detail: "newest"));

        Assert.Single(store.GetSnapshot("campaign-keep", limit: 10));

        Assert.Empty(store.GetSnapshot("campaign-fill-0", limit: 10));

    }

    private static SanctumBreach CreateBreach(string campaignId, string breachType, string detail) =>
        new()
        {
            BreachId = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            ToolName = "test_tool",
            BreachType = breachType,
            Detail = detail,
            Timestamp = DateTimeOffset.UtcNow,
        };

}
