using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A deterministic two-step barrier for observing one runtime-holder publication from inside its
/// critical section.
/// </summary>
internal sealed class BlockingCovenantRuntimePublicationCheckpoint(
    CovenantRuntimePublicationStep beforeSwap,
    CovenantRuntimePublicationStep afterSwap) : ICovenantRuntimePublicationCheckpoint, IDisposable
{

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly ManualResetEventSlim _beforeReached = new(initialState: false);

    private readonly ManualResetEventSlim _releaseBefore = new(initialState: false);

    private readonly ManualResetEventSlim _afterReached = new(initialState: false);

    private readonly ManualResetEventSlim _releaseAfter = new(initialState: false);

    private readonly ManualResetEventSlim _callbacksDrained = new(initialState: true);

    private readonly Lock _lifetime = new();

    private Exception? _failure;

    private int _activeCallbacks;

    private int _armed;

    private bool _disposed;

    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void WaitForBeforeSwap()
    {

        if (!_beforeReached.Wait(Bound))
        {

            throw new TimeoutException("The runtime publication did not reach its before-swap checkpoint.");

        }

    }

    internal void AdvanceToAfterSwap()
    {

        _releaseBefore.Set();

        if (!_afterReached.Wait(Bound))
        {

            throw new TimeoutException("The runtime publication did not reach its after-swap checkpoint.");

        }

    }

    internal void ReleaseAfterSwap() => _releaseAfter.Set();

    internal void AssertNoFailure()
    {

        Exception? failure = Volatile.Read(ref _failure);

        Assert.True(failure is null, failure?.ToString());

    }

    public void Reached(CovenantRuntimePublicationStep step)
    {

        if (Volatile.Read(ref _armed) == 0)
        {

            return;

        }

        lock (_lifetime)
        {

            if (_disposed)
            {

                return;

            }

            _activeCallbacks++;

            _callbacksDrained.Reset();

        }

        try
        {

            if (step == beforeSwap)
            {

                _beforeReached.Set();

                if (!_releaseBefore.Wait(Bound))
                {

                    RecordFailure(new TimeoutException(
                        "The runtime publication before-swap checkpoint was not released."));

                }

            }
            else if (step == afterSwap)
            {

                _afterReached.Set();

                if (!_releaseAfter.Wait(Bound))
                {

                    RecordFailure(new TimeoutException(
                        "The runtime publication after-swap checkpoint was not released."));

                }

            }

        }
        catch (Exception exception)
        {

            RecordFailure(exception);

        }
        finally
        {

            lock (_lifetime)
            {

                _activeCallbacks--;

                if (_activeCallbacks == 0)
                {

                    _callbacksDrained.Set();

                }

            }

        }

    }

    public void Dispose()
    {

        lock (_lifetime)
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            _releaseBefore.Set();

            _releaseAfter.Set();

        }

        if (!_callbacksDrained.Wait(Bound))
        {

            RecordFailure(new TimeoutException(
                "The runtime publication checkpoint callback did not drain during disposal."));

            return;

        }

        _beforeReached.Dispose();

        _releaseBefore.Dispose();

        _afterReached.Dispose();

        _releaseAfter.Dispose();

        _callbacksDrained.Dispose();

    }

    private void RecordFailure(Exception failure)
    {

        _ = Interlocked.CompareExchange(ref _failure, failure, comparand: null);

    }

}
