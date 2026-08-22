using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

internal sealed class ImmediateArcanumClientMutationBoundary : IArcanumClientMutationBoundary
{

    internal static ImmediateArcanumClientMutationBoundary Instance { get; } = new();

    public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<T> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            ArcanumClientMutationResult<T>.Completed(mutation()));

    }

    public async Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        return ArcanumClientMutationResult<T>.Completed(
            await mutation(cancellationToken));

    }

}
