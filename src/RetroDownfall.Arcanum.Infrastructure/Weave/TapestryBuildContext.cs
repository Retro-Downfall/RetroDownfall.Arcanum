using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>First-party execution observation only: no authority, connection or lease handles.</summary>
internal sealed record TapestryBuildObservation(TapestryBuildSnapshot Snapshot,
    string SummaryProvider, string SummaryModel, string EmbeddingProvider, string EmbeddingModel,
    TapestrySummaryPreparation? SummaryPreparation = null);

internal static class TapestryBuildContext
{
    private static readonly AsyncLocal<Frame?> Active = new();

    internal static TapestryBuildObservation? Current => Active.Value?.Observation;

    internal static IDisposable Enter(TapestryBuildObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        Frame frame = new(observation, Active.Value);

        Active.Value = frame;

        return frame;
    }

    private sealed class Frame(TapestryBuildObservation observation, Frame? previous) : IDisposable
    {
        internal TapestryBuildObservation Observation { get; } = observation;

        public void Dispose()
        {
            if (!ReferenceEquals(Active.Value, this))
            {
                throw new InvalidOperationException("Tapestry observations must leave in nesting order.");
            }

            Active.Value = previous;
        }
    }
}
