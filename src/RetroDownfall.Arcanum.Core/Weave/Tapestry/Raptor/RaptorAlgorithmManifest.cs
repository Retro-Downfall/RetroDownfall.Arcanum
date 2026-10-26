using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>
/// Code-owned numeric identity. Changes invalidate seeds and future generation/reuse identities.
/// This contract does not select a production builder or promise cross-runtime numeric equality.
/// </summary>
public sealed record RaptorAlgorithmManifest(RaptorNumericOptions Options)
{
    public const string Version = "raptor-umap-gmm-v1";

    public const string ReferenceCommit = "7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767";

    public static RaptorAlgorithmManifest Current { get; } = new(new RaptorNumericOptions());

    /// <summary>SHA-256 of named constants with explicit length framing and invariant numeric bits.</summary>
    public string Fingerprint()
    {
        ArgumentNullException.ThrowIfNull(Options);

        return RaptorIdentityHash.OfParts(
        [
            "raptor-manifest-v1",
            Version,
            ReferenceCommit,
            nameof(Options.ReductionDimensions), Number(Options.ReductionDimensions),
            nameof(Options.MembershipThreshold), Number(Options.MembershipThreshold),
            nameof(Options.ComponentUpperBoundExclusive), Number(Options.ComponentUpperBoundExclusive),
            nameof(Options.LocalNeighbors), Number(Options.LocalNeighbors),
            nameof(Options.MinimumNeighbors), Number(Options.MinimumNeighbors),
            nameof(Options.ExactNeighborLimit), Number(Options.ExactNeighborLimit),
            nameof(Options.LocalConnectivity), Number(Options.LocalConnectivity),
            nameof(Options.SmoothBandwidth), Number(Options.SmoothBandwidth),
            nameof(Options.SmoothIterations), Number(Options.SmoothIterations),
            nameof(Options.SmoothTolerance), Number(Options.SmoothTolerance),
            nameof(Options.MinimumSigmaScale), Number(Options.MinimumSigmaScale),
            nameof(Options.FuzzyUnionMix), Number(Options.FuzzyUnionMix),
            nameof(Options.MinimumDistance), Number(Options.MinimumDistance),
            nameof(Options.Spread), Number(Options.Spread),
            nameof(Options.SmallEpochs), Number(Options.SmallEpochs),
            nameof(Options.LargeEpochs), Number(Options.LargeEpochs),
            nameof(Options.LargeEpochBoundary), Number(Options.LargeEpochBoundary),
            nameof(Options.InitialLearningRate), Number(Options.InitialLearningRate),
            nameof(Options.RepulsionStrength), Number(Options.RepulsionStrength),
            nameof(Options.NegativeSampleRate), Number(Options.NegativeSampleRate),
            nameof(Options.InitialCoordinateRange), Number(Options.InitialCoordinateRange),
            nameof(Options.GradientClip), Number(Options.GradientClip),
            nameof(Options.RepulsionDistanceFloor), Number(Options.RepulsionDistanceFloor),
            nameof(Options.CovarianceRegularization), Number(Options.CovarianceRegularization),
            nameof(Options.AverageLikelihoodTolerance), Number(Options.AverageLikelihoodTolerance),
            nameof(Options.MaximumEmIterations), Number(Options.MaximumEmIterations),
            nameof(Options.MixtureInitializations), Number(Options.MixtureInitializations),
            nameof(Options.NumericRecipe), Options.NumericRecipe,
            nameof(Options.NeighborRecipe), Options.NeighborRecipe,
            nameof(Options.InitializationRecipe), Options.InitializationRecipe,
            nameof(Options.MixtureRecipe), Options.MixtureRecipe,
        ]);
    }

    /// <summary>
    /// Independent SplitMix64 stream seed. The first eight SHA-256 bytes are read little endian;
    /// stage and stage key isolate global, local and recursive operations over the same members.
    /// </summary>
    public ulong DeriveSeed(TapestryClusterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Scope.Id);

        ArgumentNullException.ThrowIfNull(identity.StageKey);

        ArgumentOutOfRangeException.ThrowIfNegative(identity.Layer);

        if (!Enum.IsDefined(identity.Scope.Kind) || !Enum.IsDefined(identity.Stage))
        {
            throw new ArgumentException("RAPTOR scope and stage must be defined.", nameof(identity));
        }

        string digest = RaptorIdentityHash.OfParts(
        [
            "raptor-seed-v1",
            Fingerprint(),
            Number((int)identity.Scope.Kind),
            identity.Scope.Id,
            Number(identity.Layer),
            Number((int)identity.Stage),
            identity.StageKey,
            identity.MemberDigest,
        ]);

        return BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(digest));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "RAPTOR constants must be finite.");
        }

        return BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>Framing accepts arbitrary valid Unicode ids without separator or concatenation aliases.</summary>
internal static class RaptorIdentityHash
{
    private static readonly UTF8Encoding Encoding = new(false, true);

    internal static string OfParts(IEnumerable<string> parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> size = stackalloc byte[sizeof(int)];

        foreach (string part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);

            byte[] bytes = Encoding.GetBytes(part);

            BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);

            hash.AppendData(size);

            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
