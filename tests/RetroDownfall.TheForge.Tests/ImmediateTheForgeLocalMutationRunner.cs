using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class ImmediateTheForgeLocalMutationRunner : ITheForgeLocalMutationRunner
{

    internal static ImmediateTheForgeLocalMutationRunner Instance { get; } = new();

    public Task RunAsync(
        string path,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ArgumentNullException.ThrowIfNull(mutation);

        return mutation(cancellationToken);

    }

}

internal sealed class RefusingTheForgeLocalMutationRunner(
    string code = "Data.FileLocked") : ITheForgeLocalMutationRunner
{

    public Task RunAsync(
        string path,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default) =>
        throw new TheForgeLocalMutationRefusedException(
            code,
            "refused for test");

}

internal sealed class BeforeMutationTheForgeLocalMutationRunner(
    Action beforeMutation) : ITheForgeLocalMutationRunner
{

    public Task RunAsync(
        string path,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {

        beforeMutation();

        return mutation(cancellationToken);

    }

}
