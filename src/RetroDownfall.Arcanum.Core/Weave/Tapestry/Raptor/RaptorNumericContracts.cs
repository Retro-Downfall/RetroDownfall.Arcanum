namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>One original-dimensional input; equal vectors with different ids remain separate samples.</summary>
public sealed record RaptorPoint(string StableId, ReadOnlyMemory<float> Vector);

/// <summary>Code-owned numerical recipe, never operator configuration.</summary>
public sealed record RaptorNumericOptions(
    int ReductionDimensions = 10,
    double MembershipThreshold = 0.1,
    int ComponentUpperBoundExclusive = 50,
    int LocalNeighbors = 10,
    int MinimumNeighbors = 2,
    int ExactNeighborLimit = 2048,
    double LocalConnectivity = 1,
    double SmoothBandwidth = 1,
    int SmoothIterations = 64,
    double SmoothTolerance = 1e-5,
    double MinimumSigmaScale = 1e-3,
    double FuzzyUnionMix = 1,
    double MinimumDistance = 0.1,
    double Spread = 1,
    int SmallEpochs = 500,
    int LargeEpochs = 200,
    int LargeEpochBoundary = 10000,
    double InitialLearningRate = 1,
    double RepulsionStrength = 1,
    int NegativeSampleRate = 5,
    double InitialCoordinateRange = 10,
    double GradientClip = 4,
    double RepulsionDistanceFloor = 0.001,
    double CovarianceRegularization = 1e-6,
    double AverageLikelihoodTolerance = 1e-3,
    int MaximumEmIterations = 100,
    int MixtureInitializations = 1,
    string NumericRecipe = "double-fixed-order-v1",
    string NeighborRecipe = "cosine-exact-or-nndescent-v1",
    string InitializationRecipe = "splitmix64-random-v1",
    string MixtureRecipe = "full-covariance-best-converged-v1");

/// <summary>Independent random streams; local and recursive paths also carry an exact stage key.</summary>
public enum RaptorStage
{
    GlobalNeighbors,

    GlobalUmap,

    GlobalMixture,

    LocalNeighbors,

    LocalUmap,

    LocalMixture,

    Split,
}

/// <summary>
/// Frozen numerical invocation identity. Member ids are sorted ordinally and length-framed before
/// hashing; vector equality never substitutes for sample identity. Scope spelling is preserved.
/// </summary>
public sealed record TapestryClusterIdentity
{
    public TapestryClusterIdentity(
        TapestryScope scope,
        int layer,
        RaptorStage stage,
        string stageKey,
        IEnumerable<string> memberIds)
    {
        ArgumentNullException.ThrowIfNull(memberIds);

        string[] sorted = memberIds.ToArray();

        foreach (string member in sorted)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(member);
        }

        Array.Sort(sorted, StringComparer.Ordinal);

        for (int index = 1; index < sorted.Length; index++)
        {
            if (string.Equals(sorted[index - 1], sorted[index], StringComparison.Ordinal))
            {
                throw new ArgumentException("RAPTOR sample ids must be distinct.", nameof(memberIds));
            }
        }

        Scope = scope;

        Layer = layer;

        Stage = stage;

        StageKey = stageKey;

        MemberDigest = RaptorIdentityHash.OfParts(["raptor-members-v1", .. sorted]);
    }

    public TapestryScope Scope { get; init; }

    public int Layer { get; init; }

    public RaptorStage Stage { get; init; }

    /// <summary>Canonical global/local component or recursive split path; empty for the global stage.</summary>
    public string StageKey { get; init; }

    public string MemberDigest { get; }
}
