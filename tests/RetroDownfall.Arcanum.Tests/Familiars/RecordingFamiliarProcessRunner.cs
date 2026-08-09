using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// Replays recorded CLI output and records the invocation it was asked for, so a mapping test can
/// assert both directions of the adapter — what Arcanum asked the Familiar to do, and what it made
/// of the answer — without spawning anything.
/// </summary>
internal sealed class RecordingFamiliarProcessRunner : IFamiliarProcessRunner
{

    private readonly Queue<Func<IAsyncEnumerable<string>>> _streams = new();

    private FamiliarProcessOutput _bufferedOutput =
        new(FamiliarProcessFailure.None, 0, string.Empty, string.Empty);

    public List<FamiliarProcessRequest> Requests { get; } = [];

    public FamiliarProcessRequest LastRequest => Requests[^1];

    public void EnqueueLines(params string[] lines) =>
        _streams.Enqueue(() => ToAsync(lines));

    public void EnqueueFixture(string fixtureName) =>
        EnqueueLines(FamiliarFixtures.ReadLines(fixtureName));

    public void EnqueueFailure(FamiliarProcessException failure) =>
        _streams.Enqueue(() => Throwing(failure));

    /// <summary>Frames arrive, then the CLI exits non-zero — the partial-answer case.</summary>
    public void EnqueueLinesThenFailure(IEnumerable<string> lines, FamiliarProcessException failure) =>
        _streams.Enqueue(() => ToAsyncThenThrow(lines, failure));

    public void SetBufferedOutput(FamiliarProcessOutput output) => _bufferedOutput = output;

    public IAsyncEnumerable<string> RunLinesAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken)
    {

        _ = cancellationToken;

        Requests.Add(request);

        return _streams.Count > 0
            ? _streams.Dequeue()()
            : ToAsync([]);

    }

    public Task<FamiliarProcessOutput> RunToCompletionAsync(
        FamiliarProcessRequest request,
        CancellationToken cancellationToken)
    {

        _ = cancellationToken;

        Requests.Add(request);

        return Task.FromResult(_bufferedOutput);

    }

    private static async IAsyncEnumerable<string> ToAsync(IEnumerable<string> lines)
    {

        foreach (string line in lines)
        {

            await Task.Yield();

            yield return line;

        }

    }

    private static async IAsyncEnumerable<string> ToAsyncThenThrow(
        IEnumerable<string> lines,
        FamiliarProcessException failure)
    {

        foreach (string line in lines)
        {

            await Task.Yield();

            yield return line;

        }

        throw failure;

    }

#pragma warning disable CS1998 // An iterator that only throws still has to be an async iterator.
    private static async IAsyncEnumerable<string> Throwing(
        FamiliarProcessException failure,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        _ = cancellationToken;

        throw failure;

#pragma warning disable CS0162 // Required so the compiler treats this as an iterator.
        yield break;
#pragma warning restore CS0162

    }
#pragma warning restore CS1998

}
