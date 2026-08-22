using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class RecordingGrimoireCliInitialization(
    string? refusal = null,
    Action? beforeOperation = null) : IGrimoireCliInitialization, IServiceProvider
{

    public int ExclusiveCalls { get; private set; }

    public int BootstrapCalls { get; private set; }

    public bool IsInsideExclusiveCallback { get; private set; }

    public bool CallbackCompleted { get; private set; }

    public async Task<T> RunExclusiveAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {

        ExclusiveCalls++;

        if (refusal is not null)
        {

            throw new InvalidOperationException(refusal);

        }

        beforeOperation?.Invoke();

        IsInsideExclusiveCallback = true;

        try
        {

            T result = await operation(this, cancellationToken).ConfigureAwait(false);

            CallbackCompleted = true;

            return result;

        }
        finally
        {

            IsInsideExclusiveCallback = false;

        }

    }

    public Task<T> RunExclusiveWithBootstrapAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {

        BootstrapCalls++;

        throw new InvalidOperationException(
            "This test double does not permit a bootstrap boundary.");

    }

    public object? GetService(Type serviceType) => null;

}
