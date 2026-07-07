using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class FakeCommLinkDispatcher : ICommLinkDispatcher
{

    public List<CommLinkMessage> Dispatched { get; } = [];

    public Task<Result> DispatchAsync(CommLinkMessage message, CancellationToken cancellationToken = default)
    {
        Dispatched.Add(message);

        return Task.FromResult(Result.Success());
    }

}

internal sealed class FakeBudgetAlertRepository : IBudgetAlertRepository
{

    public HashSet<int> AlertedThresholdsToday { get; } = new();

    public Task<bool> RecordAlertAsync(int threshold, decimal spendUsd, decimal dailyLimitUsd, CancellationToken cancellationToken = default)
    {
        AlertedThresholdsToday.Add(threshold);

        return Task.FromResult(true);
    }

    public Task<bool> HasAlertedTodayAsync(int threshold, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AlertedThresholdsToday.Contains(threshold));
    }

}
