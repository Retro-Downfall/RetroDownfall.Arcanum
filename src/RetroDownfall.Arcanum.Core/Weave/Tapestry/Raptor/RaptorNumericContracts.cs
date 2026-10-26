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
    int NeighborExactBlockSize = 64,
    int NeighborMinimumSearchWidth = 32,
    int NeighborSearchMultiplier = 2,
    int NeighborProjectionTrees = 8,
    int NeighborMinimumLeafSize = 32,
    int NeighborMaximumIterations = 12,
    int NeighborProgressInterval = 1024,
    double LocalConnectivity = 1,
    double SmoothBandwidth = 1,
    int SmoothIterations = 64,
    double SmoothTolerance = 1e-5,
    double MinimumSigmaScale = 1e-3,
    double FuzzyUnionMix = 1,
    double MinimumDistance = 0.1,
    double Spread = 1,
    double CurveA = 1.5769434603113077,
    double CurveB = 0.8950608779109733,
    double InitialRescaleRange = 10,
    int UmapProgressInterval = 1024,
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
    int MixtureBlockSize = 64,
    int KMeansMaximumIterations = 100,
    double MinimumComponentMass = 1e-12,
    double MixtureNormalizationTolerance = 1e-12,
    double MatrixSymmetryTolerance = 1e-12,
    string NumericRecipe = "double-fixed-order-v1",
    string NeighborRecipe = "cosine-normalized-scalar-identical-zero-self-first-identical-anchors-median-rptree-coprime-fill-new-old-reverse-nearest-nndescent-v1",
    string InitializationRecipe = "splitmix64-canonical-random-rescale-zero-singleton-v1",
    string CurveRecipe = "umap-0.5.5-scipy-fit-spread1-mindist0.1-v1",
    string FuzzyRecipe = "umap-0.5.5-double-self-excluded-smooth-fuzzy-union-zero-underflow-v1",
    string OptimizerRecipe = "umap-0.5.5-double-canonical-csr-prune-max-over-epochs-post-epoch-alpha-splitmix64-modulo-negative-v1",
    string MixtureRecipe = "full-covariance-block-welford-pre-m-average-stop-final-score-identical-one-v1;kmeans++-one-draw-splitmix64-sha256-framed-seed-k-le64-modulo-first-lloyd-label-stable-low-index-tie-v1;lexicographic-samples-signed-zero-means-covariance-weights-exact-bic-tie-low-k-v1");

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
