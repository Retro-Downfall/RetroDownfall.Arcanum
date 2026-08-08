using System.Numerics;
using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// Shared little-endian <c>float32[]</c> &lt;-&gt; <c>BLOB</c> conversion for The Weave's
/// durable storage tables (for example <c>entry_embeddings.Embedding</c>), for binding a query
/// vector to a sqlite-vec <c>MATCH</c> parameter, and for OpenAI-compatible <c>base64</c>
/// <c>encoding_format</c> on <c>POST /v1/embeddings</c> (§8.24). All realistic Arcanum deployment
/// targets (x64, Arm64) are little-endian, so no explicit byte-swapping is performed — see
/// <c>docs/Arcanum.DESIGN.md</c> §21.
/// </summary>
/// <remarks>
/// Lives in Core (rather than Infrastructure, where it originated) because it is a pure,
/// dependency-free numeric utility consumed by both Infrastructure (The Weave/Divination storage)
/// and Api (the OpenAI <c>/v1/embeddings</c> endpoint and <c>SemanticSpellRouter</c>) — Infrastructure
/// has no project reference from Api that would let an Infrastructure-`internal` type be shared
/// the other way.
/// </remarks>
public static class EmbeddingBlobCodec
{

    public static byte[] Encode(ReadOnlySpan<float> vector) =>
        MemoryMarshal.AsBytes(vector).ToArray();

    public static float[] Decode(byte[] bytes)
    {

        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidOperationException(
                $"Embedding blob length {bytes.Length} is not a multiple of {sizeof(float)} bytes.");

        }

        float[] result = new float[bytes.Length / sizeof(float)];

        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(result);

        return result;

    }

    /// <summary>
    /// A zero-copy <c>float32</c> view over a stored embedding BLOB, for scan paths that only read the
    /// vector and would otherwise pay <see cref="Decode"/>'s allocation and copy for every candidate
    /// row. Validation is identical to <see cref="Decode"/>'s: a blob whose length is not a whole number
    /// of floats is a corrupt row, not a short vector, so it throws rather than silently truncating the
    /// way a bare <see cref="MemoryMarshal.Cast{TFrom, TTo}(ReadOnlySpan{TFrom})"/> would.
    /// </summary>
    public static ReadOnlySpan<float> AsVector(ReadOnlySpan<byte> bytes)
    {

        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidOperationException(
                $"Embedding blob length {bytes.Length} is not a multiple of {sizeof(float)} bytes.");

        }

        return MemoryMarshal.Cast<byte, float>(bytes);

    }

    /// <summary>
    /// The squared L2 norm of <paramref name="vector"/>, accumulated over exactly the lanes and in
    /// exactly the order <see cref="CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/> uses
    /// internally. Hoisting an invariant query vector's norm out of a scan loop with this is therefore
    /// bit-identical to letting every row recompute it.
    /// </summary>
    public static double NormSquared(ReadOnlySpan<float> vector)
    {

        double norm = 0;

        int simdWidth = Vector<float>.Count;

        int i = 0;

        if (vector.Length >= simdWidth)
        {

            for (; i <= vector.Length - simdWidth; i += simdWidth)
            {

                Vector<float> v = new(vector.Slice(i, simdWidth));

                norm += Vector.Dot(v, v);

            }

        }

        for (; i < vector.Length; i++)
        {

            norm += (double)vector[i] * vector[i];

        }

        return norm;

    }

    /// <summary>
    /// Cosine similarity in <c>[-1, 1]</c> (imprinted embeddings are typically near-unit vectors, so
    /// results cluster in <c>[0, 1]</c> in practice). Returns <c>0</c> for mismatched or zero-length
    /// vectors rather than throwing — the managed fallback path treats that as "no signal", not a
    /// search failure.
    ///
    /// SIMD-vectorized via <see cref="Vector{T}"/> (hardware width chosen by the JIT at startup —
    /// SSE/AVX on x64, NEON on Arm64 — with no platform-specific code and no extra NuGet dependency,
    /// keeping this Native AOT friendly). This is the hot path for the managed brute-force fallback in
    /// <c>DivinationService.SearchManagedAsync</c> when sqlite-vec is unavailable — see
    /// <c>docs/Arcanum.DESIGN.md</c> §16.
    /// Each lane's dot/norm products are summed horizontally in <c>float</c> via <see cref="Vector.Dot"/>,
    /// then accumulated across lanes in <c>double</c> to bound precision loss on long vectors; the scalar
    /// remainder (when <c>a.Length</c> isn't a multiple of <see cref="Vector{T}.Count"/>) is folded in the
    /// same way as the original scalar loop.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {

        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;

        }

        return CosineSimilarity(a, NormSquared(a), b);

    }

    /// <summary>
    /// <see cref="CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/> with
    /// <paramref name="a"/>'s squared norm supplied by the caller, for brute-force scans where
    /// <paramref name="a"/> is one fixed query vector scored against every row in the corpus. Recomputing
    /// its norm per row spends a third of the vector math re-deriving a constant; pass
    /// <see cref="NormSquared"/> of the query once instead. Results are bit-identical either way.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, double normA, ReadOnlySpan<float> b)
    {

        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;

        }

        double dot = 0;

        double normB = 0;

        int simdWidth = Vector<float>.Count;

        int i = 0;

        if (a.Length >= simdWidth)
        {

            for (; i <= a.Length - simdWidth; i += simdWidth)
            {

                Vector<float> va = new(a.Slice(i, simdWidth));

                Vector<float> vb = new(b.Slice(i, simdWidth));

                dot += Vector.Dot(va, vb);

                normB += Vector.Dot(vb, vb);

            }

        }

        for (; i < a.Length; i++)
        {

            dot += (double)a[i] * b[i];

            normB += (double)b[i] * b[i];

        }

        if (normA <= 0 || normB <= 0)
        {
            return 0f;

        }

        float similarity = (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));

        // A poisoned provider response (a NaN/Infinity float slipping into an otherwise
        // finite-looking vector) can still yield a non-finite result even with both norms positive.
        // Propagating NaN/Infinity downstream is actively dangerous: `NaN >= threshold` is always
        // false (silently masking a real match as "below threshold" rather than surfacing the
        // corruption), and sort order among NaN values is undefined, so callers that sort by
        // similarity (SemanticSpellRouter, Divination) could rank results unpredictably. Treating
        // it as "no signal" (0) is the same safe default already used for mismatched/zero vectors.
        return float.IsFinite(similarity) ? similarity : 0f;

    }

}
