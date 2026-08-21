namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class DataRetentionLeaseMaintainer
{

    internal static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(1);

    private readonly Func<
        Guid,
        string,
        DateTimeOffset,
        DateTimeOffset,
        CancellationToken,
        Task<bool>> _renewLease;

    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _leaseDuration;

    private readonly TimeSpan _heartbeatInterval;

    internal DataRetentionLeaseMaintainer(
        Func<
            Guid,
            string,
            DateTimeOffset,
            DateTimeOffset,
            CancellationToken,
            Task<bool>> renewLease,
        TimeProvider timeProvider,
        TimeSpan? leaseDuration = null,
        TimeSpan? heartbeatInterval = null)
    {

        ArgumentNullException.ThrowIfNull(renewLease);

        ArgumentNullException.ThrowIfNull(timeProvider);

        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;

        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;

        if (_leaseDuration <= TimeSpan.Zero)
        {

            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        }

        if (_heartbeatInterval <= TimeSpan.Zero
            || _heartbeatInterval >= _leaseDuration)
        {

            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        }

        _renewLease = renewLease;

        _timeProvider = timeProvider;

    }

    internal async Task<T> RunAsync<T>(
        Guid operationId,
        string ownerId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        ArgumentNullException.ThrowIfNull(action);

        using CancellationTokenSource actionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using CancellationTokenSource heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<T> actionTask = action(actionCancellation.Token);

        try
        {

            while (!actionTask.IsCompleted)
            {

                Task delay = Task.Delay(
                    _heartbeatInterval,
                    _timeProvider,
                    heartbeatCancellation.Token);

                Task completed = await Task.WhenAny(
                    actionTask,
                    delay).ConfigureAwait(false);

                if (ReferenceEquals(completed, actionTask))
                {

                    break;

                }

                await delay.ConfigureAwait(false);

                DateTimeOffset now = _timeProvider.GetUtcNow();

                bool renewed = await _renewLease(
                    operationId,
                    ownerId,
                    now,
                    now.Add(_leaseDuration),
                    cancellationToken).ConfigureAwait(false);

                if (!renewed)
                {

                    if (actionTask.IsCompleted)
                    {

                        return await actionTask.ConfigureAwait(false);

                    }

                    throw new DataRetentionLeaseLostException(
                        "The retention operation lost its durable lease while applying a candidate.");

                }

            }

            return await actionTask.ConfigureAwait(false);

        }
        catch
        {

            actionCancellation.Cancel();

            await ObserveCompletionAsync(actionTask).ConfigureAwait(false);

            throw;

        }
        finally
        {

            heartbeatCancellation.Cancel();

        }

    }

    private static async Task ObserveCompletionAsync<T>(Task<T> task)
    {

        try
        {

            _ = await task.ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

        }
        catch
        {

        }

    }

}

internal sealed class DataRetentionLeaseLostException(string message)
    : InvalidOperationException(message);
