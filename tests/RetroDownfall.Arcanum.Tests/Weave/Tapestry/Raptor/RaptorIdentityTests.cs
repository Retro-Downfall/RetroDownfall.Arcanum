using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorIdentityTests
{
    [Fact]
    public void Seed_IsStableAcrossInputPermutationAndChangesAcrossStage()
    {
        RaptorAlgorithmManifest manifest = RaptorAlgorithmManifest.Current;

        TapestryClusterIdentity identity = new(new(TapestryScopeKind.Workspace, "/repo/A"), 1, RaptorStage.GlobalUmap, "", ["c", "a", "b"]);

        ulong seed = manifest.DeriveSeed(identity);

        Assert.Equal(seed, manifest.DeriveSeed(new(identity.Scope, identity.Layer, identity.Stage, identity.StageKey, ["b", "c", "a"])));

        Assert.NotEqual(seed, manifest.DeriveSeed(identity with { Stage = RaptorStage.LocalUmap }));

        Assert.NotEqual(seed, manifest.DeriveSeed(identity with { StageKey = "global:1" }));

        Assert.NotEqual(seed, manifest.DeriveSeed(identity with { Layer = 2 }));

        Assert.NotEqual(seed, manifest.DeriveSeed(identity with { Scope = new(TapestryScopeKind.Session, "/repo/A") }));

        Assert.NotEqual(seed, manifest.DeriveSeed(identity with { Scope = new(TapestryScopeKind.Workspace, "/repo/a") }));

        Assert.NotEqual(seed, manifest.DeriveSeed(new(identity.Scope, identity.Layer, identity.Stage, identity.StageKey, ["a", "b", "different"])));
    }

    [Fact]
    public void Seed_FramingPreservesExactMemberIdentitiesAndMultiplicity()
    {
        TapestryScope scope = new(TapestryScopeKind.Workspace, "/repo");

        TapestryClusterIdentity first = new(scope, 0, RaptorStage.GlobalUmap, "", ["a\u001fb", "c"]);

        TapestryClusterIdentity second = new(scope, 0, RaptorStage.GlobalUmap, "", ["a", "b\u001fc"]);

        Assert.NotEqual(RaptorAlgorithmManifest.Current.DeriveSeed(first), RaptorAlgorithmManifest.Current.DeriveSeed(second));

        Assert.Throws<ArgumentException>(() => new TapestryClusterIdentity(scope, 0, RaptorStage.GlobalUmap, "", ["a", "a"]));
    }

    [Fact]
    public void Identity_FreezesInputMembersBeforeTheCallerCanMutateThem()
    {
        string[] members = ["a", "b"];

        TapestryClusterIdentity identity = new(new(TapestryScopeKind.Workspace, "/repo"), 0, RaptorStage.GlobalUmap, "", members);

        ulong first = RaptorAlgorithmManifest.Current.DeriveSeed(identity);

        members[0] = "different";

        Assert.Equal(first, RaptorAlgorithmManifest.Current.DeriveSeed(identity));
    }

    [Fact]
    public void AlgorithmFingerprint_ChangesForEveryBehavioralConstant()
    {
        RaptorNumericOptions baseline = new();

        RaptorNumericOptions[] changes =
        [
            baseline with { ReductionDimensions = 9 },
            baseline with { MembershipThreshold = 0.2 },
            baseline with { ComponentUpperBoundExclusive = 49 },
            baseline with { LocalNeighbors = 9 },
            baseline with { MinimumNeighbors = 3 },
            baseline with { ExactNeighborLimit = 2047 },
            baseline with { NeighborExactBlockSize = 65 },
            baseline with { NeighborMinimumSearchWidth = 33 },
            baseline with { NeighborSearchMultiplier = 3 },
            baseline with { NeighborProjectionTrees = 9 },
            baseline with { NeighborMinimumLeafSize = 33 },
            baseline with { NeighborMaximumIterations = 13 },
            baseline with { NeighborProgressInterval = 1025 },
            baseline with { LocalConnectivity = 0.5 },
            baseline with { SmoothBandwidth = 2 },
            baseline with { SmoothIterations = 63 },
            baseline with { SmoothTolerance = 1e-4 },
            baseline with { MinimumSigmaScale = 1e-2 },
            baseline with { FuzzyUnionMix = 0.5 },
            baseline with { MinimumDistance = 0.2 },
            baseline with { Spread = 2 },
            baseline with { CurveA = 2 },
            baseline with { CurveB = 1 },
            baseline with { InitialRescaleRange = 11 },
            baseline with { UmapProgressInterval = 1023 },
            baseline with { CurveRecipe = "other" },
            baseline with { FuzzyRecipe = "other" },
            baseline with { OptimizerRecipe = "other" },
            baseline with { SmallEpochs = 499 },
            baseline with { LargeEpochs = 199 },
            baseline with { LargeEpochBoundary = 9999 },
            baseline with { InitialLearningRate = 0.5 },
            baseline with { RepulsionStrength = 2 },
            baseline with { NegativeSampleRate = 4 },
            baseline with { InitialCoordinateRange = 5 },
            baseline with { GradientClip = 3 },
            baseline with { RepulsionDistanceFloor = 0.002 },
            baseline with { CovarianceRegularization = 1e-5 },
            baseline with { AverageLikelihoodTolerance = 1e-2 },
            baseline with { MaximumEmIterations = 99 },
            baseline with { MixtureInitializations = 2 },
            baseline with { NumericRecipe = "double-fixed-order-v2" },
            baseline with { NeighborRecipe = "cosine-exact-or-nndescent-v2" },
            baseline with { InitializationRecipe = "splitmix64-random-v2" },
            baseline with { MixtureRecipe = "full-covariance-best-converged-v2" },
        ];

        RaptorAlgorithmManifest original = new(baseline);

        TapestryClusterIdentity identity = new(new(TapestryScopeKind.Workspace, "/repo"), 0, RaptorStage.GlobalUmap, "", ["a", "b"]);

        foreach (RaptorNumericOptions changed in changes)
        {
            RaptorAlgorithmManifest candidate = new(changed);

            Assert.NotEqual(original.Fingerprint(), candidate.Fingerprint());

            Assert.NotEqual(original.DeriveSeed(identity), candidate.DeriveSeed(identity));
        }

        Assert.Equal(changes.Length, changes.Select(options => new RaptorAlgorithmManifest(options).Fingerprint()).Distinct(StringComparer.Ordinal).Count());
    }
}
