using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Ux.Services;

internal sealed class TheForgeLocalMutationRunner : ITheForgeLocalMutationRunner
{

    private readonly IArcanumClientMutationBoundary _boundary;

    private readonly string _managedRoot;

    public TheForgeLocalMutationRunner(IArcanumClientMutationBoundary boundary)
    {

        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));

        _managedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(ArcanumPaths.GrimoireDirectory));

    }

    public async Task RunAsync(
        string path,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ArgumentNullException.ThrowIfNull(mutation);

        Result<ArcanumClientMutationPathDisposition> classification =
            ArcanumClientMutationPathPolicy.Classify(_managedRoot, path);

        if (classification.IsFailure)
        {

            throw new TheForgeLocalMutationRefusedException(
                classification.Error.Code,
                classification.Error.Message);

        }

        if (classification.Value
            is ArcanumClientMutationPathDisposition.OutsideManagedRoot)
        {

            await mutation(cancellationToken).ConfigureAwait(false);

            return;

        }

        ArcanumClientMutationResult<bool> result = await _boundary
            .RunAsync(
                async admittedCancellationToken =>
                {

                    await mutation(admittedCancellationToken).ConfigureAwait(false);

                    return true;

                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsCompleted)
        {

            throw new TheForgeLocalMutationRefusedException(
                result.Error.Code,
                result.Error.Message);

        }

    }

}

internal sealed class TheForgeLocalMutationRefusedException(
    string code,
    string message) : InvalidOperationException($"{code}: {message}")
{

    internal string Code { get; } = code;

}
