using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

/// <summary>
/// Hardening pass — <see cref="EmbeddingBlobCodec.CosineSimilarity"/> is SIMD-vectorized via
/// <see cref="System.Numerics.Vector{T}"/>. These tests confirm the vectorized result exactly matches a
/// naive <c>double</c>-precision reference implementation (within float tolerance) across vector lengths
/// that are and are not multiples of the hardware SIMD width, plus the pre-existing guard/edge-case
/// contract and the <c>Encode</c>/<c>Decode</c> blob round-trip.
/// </summary>
public sealed class EmbeddingBlobCodecTests
{

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(768)]
    public void CosineSimilarity_MatchesReferenceImplementation_AcrossSimdBoundaries(int length)
    {

        // Deliberately spans lengths below/at/above common hardware SIMD widths (4/8/16/32 lanes) so the
        // vectorized main loop and the scalar remainder loop are both exercised, in isolation and
        // combined, regardless of which width the running CPU actually uses for Vector<float>.
        float[] a = MakeVector(length, seed: 1);

        float[] b = MakeVector(length, seed: 2);

        float actual = EmbeddingBlobCodec.CosineSimilarity(a, b);

        float expected = ReferenceCosineSimilarity(a, b);

        AssertApproximatelyEqual(expected, actual);

    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(768)]
    public void CosineSimilarity_IdenticalVectors_ReturnsApproximatelyOne(int length)
    {

        float[] a = MakeVector(length, seed: 5);

        float similarity = EmbeddingBlobCodec.CosineSimilarity(a, a);

        AssertApproximatelyEqual(1f, similarity);

    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(33)]
    public void CosineSimilarity_OrthogonalVectors_ReturnsApproximatelyZero(int length)
    {

        // Standard basis vectors e0 and e1 are orthogonal regardless of dimension count.
        float[] a = new float[length];

        float[] b = new float[length];

        a[0] = 1f;

        b[1] = 1f;

        float similarity = EmbeddingBlobCodec.CosineSimilarity(a, b);

        AssertApproximatelyEqual(0f, similarity);

    }

    [Fact]
    public void CosineSimilarity_MismatchedLengths_ReturnsZero_WithoutThrowing()
    {

        float[] a = MakeVector(16, seed: 1);

        float[] b = MakeVector(17, seed: 1);

        float similarity = EmbeddingBlobCodec.CosineSimilarity(a, b);

        Assert.Equal(0f, similarity);

    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero_WithoutThrowing()
    {

        float similarity = EmbeddingBlobCodec.CosineSimilarity([], []);

        Assert.Equal(0f, similarity);

    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero_WithoutThrowing()
    {

        float[] zero = new float[16];

        float[] nonZero = MakeVector(16, seed: 3);

        Assert.Equal(0f, EmbeddingBlobCodec.CosineSimilarity(zero, nonZero));

        Assert.Equal(0f, EmbeddingBlobCodec.CosineSimilarity(zero, zero));

    }

    [Fact]
    public void CosineSimilarity_NaNInVector_ReturnsZero_WithoutPropagatingNaN()
    {

        // A poisoned provider response (a NaN slipping into an otherwise finite-looking vector) must
        // not propagate: `NaN >= threshold` is always false (silently masking a real match as
        // "below threshold"), and sort order among NaN values is undefined for callers that rank by
        // similarity.
        float[] a = MakeVector(16, seed: 1);

        a[3] = float.NaN;

        float[] b = MakeVector(16, seed: 2);

        float similarity = EmbeddingBlobCodec.CosineSimilarity(a, b);

        Assert.Equal(0f, similarity);

        Assert.False(float.IsNaN(similarity));

    }

    [Fact]
    public void CosineSimilarity_InfinityInVector_ReturnsZero_WithoutPropagatingInfinity()
    {

        float[] a = MakeVector(16, seed: 1);

        a[5] = float.PositiveInfinity;

        float[] b = MakeVector(16, seed: 2);

        float similarity = EmbeddingBlobCodec.CosineSimilarity(a, b);

        Assert.Equal(0f, similarity);

        Assert.True(float.IsFinite(similarity));

    }

    [Fact]
    public void EncodeDecode_RoundTrips_PreservesVectorExactly()
    {

        float[] original = MakeVector(768, seed: 42);

        byte[] blob = EmbeddingBlobCodec.Encode(original);

        float[] decoded = EmbeddingBlobCodec.Decode(blob);

        Assert.Equal(original, decoded);

    }

    [Fact]
    public void Decode_LengthNotMultipleOfFloatSize_Throws()
    {

        byte[] invalid = new byte[7];

        Assert.Throws<InvalidOperationException>(() => EmbeddingBlobCodec.Decode(invalid));

    }

    private static float[] MakeVector(int length, int seed)
    {

        Random random = new(seed);

        float[] vector = new float[length];

        for (int i = 0; i < length; i++)
        {

            vector[i] = (float)(random.NextDouble() * 2.0 - 1.0);

        }

        return vector;

    }

    /// <summary>
    /// The pre-SIMD scalar implementation being replaced: a plain <c>for</c> loop accumulating dot
    /// product and both norms in <c>double</c>. Kept independent of <see cref="EmbeddingBlobCodec"/> so
    /// this is a true reference, not a copy of the code under test.
    /// </summary>
    private static float ReferenceCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {

        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;

        }

        double dot = 0;

        double normA = 0;

        double normB = 0;

        for (int i = 0; i < a.Length; i++)
        {

            dot += (double)a[i] * b[i];

            normA += (double)a[i] * a[i];

            normB += (double)b[i] * b[i];

        }

        if (normA <= 0 || normB <= 0)
        {
            return 0f;

        }

        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));

    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(768)]
    public void CosineSimilarity_WithHoistedQueryNorm_IsBitIdentical(int length)
    {

        // The managed brute-force scan hoists the invariant query vector's norm out of the per-row loop.
        // That is only legitimate if it changes nothing, so this asserts exact equality — not tolerance.
        float[] query = BuildVector(length, seed: 11);

        float[] candidate = BuildVector(length, seed: 29);

        double queryNormSquared = EmbeddingBlobCodec.NormSquared(query);

        Assert.Equal(
            EmbeddingBlobCodec.CosineSimilarity(query, candidate),
            EmbeddingBlobCodec.CosineSimilarity(query, queryNormSquared, candidate));

    }

    [Fact]
    public void AsVector_ViewsTheBlobWithoutCopying()
    {

        float[] vector = [1.5f, -2.25f, 0.125f, 4f];

        byte[] encoded = EmbeddingBlobCodec.Encode(vector);

        Assert.True(EmbeddingBlobCodec.AsVector(encoded).SequenceEqual(vector));

        // Same validation as Decode: a partial trailing float is a corrupt row, not a short vector.
        _ = Assert.Throws<InvalidOperationException>(() => _ = EmbeddingBlobCodec.AsVector(encoded.AsSpan(0, 5)).Length);

    }

    [Fact]
    public void NormSquared_MatchesTheSelfSimilarityPath()
    {

        float[] vector = BuildVector(768, seed: 7);

        // A vector is exactly similar to itself, which only holds if NormSquared accumulates the way
        // CosineSimilarity does internally.
        AssertApproximatelyEqual(
            1f,
            EmbeddingBlobCodec.CosineSimilarity(vector, EmbeddingBlobCodec.NormSquared(vector), vector));

    }

    private static float[] BuildVector(int length, int seed)
    {

        float[] vector = new float[length];

        for (int index = 0; index < length; index++)
        {

            vector[index] = MathF.Sin((index + seed) * 0.37f) * (index % 5 == 0 ? 2.5f : 1f);

        }

        return vector;

    }

    private static void AssertApproximatelyEqual(float expected, float actual)
    {

        const float tolerance = 1e-5f;

        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} but got {actual} (tolerance {tolerance}).");

    }

}
