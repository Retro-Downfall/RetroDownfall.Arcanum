using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class RecordingArcanumClientMutationBoundary(
    ArcanumClientMutationDisposition disposition =
        ArcanumClientMutationDisposition.Completed) : IArcanumClientMutationBoundary
{

    private readonly Error _error = disposition switch
    {
        ArcanumClientMutationDisposition.Blocked => new Error(
            ErrorCodes.Data.ResetInProgress,
            "An installation maintenance operation is active."),
        ArcanumClientMutationDisposition.Unsafe => new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "Client mutation admission could not be validated safely."),
        _ => Error.None,
    };

    internal int Calls { get; private set; }

    internal Action? BeforeMutation { get; set; }

    public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<T> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        cancellationToken.ThrowIfCancellationRequested();

        Calls++;

        if (disposition is not ArcanumClientMutationDisposition.Completed)
        {

            return Task.FromResult(Refusal<T>());

        }

        BeforeMutation?.Invoke();

        return Task.FromResult(
            ArcanumClientMutationResult<T>.Completed(mutation()));

    }

    public async Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        cancellationToken.ThrowIfCancellationRequested();

        Calls++;

        if (disposition is not ArcanumClientMutationDisposition.Completed)
        {

            return Refusal<T>();

        }

        BeforeMutation?.Invoke();

        T value = await mutation(cancellationToken).ConfigureAwait(false);

        return ArcanumClientMutationResult<T>.Completed(value);

    }

    private ArcanumClientMutationResult<T> Refusal<T>() =>
        disposition is ArcanumClientMutationDisposition.Blocked
            ? ArcanumClientMutationResult<T>.Blocked(_error)
            : ArcanumClientMutationResult<T>.Unsafe(_error);

}
