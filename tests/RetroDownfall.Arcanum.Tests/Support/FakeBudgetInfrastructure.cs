using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class FakeCommLinkDispatcher : ICommLinkDispatcher
{

    public List<CommLinkMessage> Dispatched { get; } = [];

    public Task<Result<CommLinkDeliveryResult>> DispatchAsync(
        CommLinkMessage message,
        CancellationToken cancellationToken = default)
    {
        Dispatched.Add(message);

        return Task.FromResult(
            Result<CommLinkDeliveryResult>.Success(
                new CommLinkDeliveryResult(CommLinkDeliveryStatus.Delivered)));
    }

}

internal sealed class FakeBudgetAlertRepository : IBudgetAlertRepository
{

    public HashSet<int> AlertedThresholdsToday { get; } = new();

    public Task<bool> RecordAlertAsync(int threshold, decimal spendUsd, decimal dailyLimitUsd, CancellationToken cancellationToken = default)
    {
        bool added = AlertedThresholdsToday.Add(threshold);

        return Task.FromResult(added);
    }

    public Task<bool> HasAlertedTodayAsync(int threshold, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AlertedThresholdsToday.Contains(threshold));
    }

}
