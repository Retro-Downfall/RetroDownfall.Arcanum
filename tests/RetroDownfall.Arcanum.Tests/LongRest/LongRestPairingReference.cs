using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The Background Floor the plain way, for tests to hold the streaming implementation to. Every cosine of the generation is computed and
/// sorted, the quantile is the sorted list's ceil(n * q)th value, and only then is that value placed in its bin and read as the bin's upper
/// edge. The production scan never sorts; it counts into 1,024 fixed bins as it streams, and the two must agree on every generation.
/// </summary>
internal static class LongRestPairingReference
{
    /// <summary>The anchor's floor over the captured vectors in memory id order (the anchor is one of them).</summary>
    internal static int Floor(IReadOnlyList<float[]> captured, int anchor)
    {
        float[] direction = SphericalKMeans.NormalizedDirection(captured[anchor]);

        if (direction.Length == 0) return 1000;

        List<double> background = [];

        List<float[]> sample = [];

        List<double> pairs = [];

        for (int index = 0; index < captured.Count; index++)
        {
            if (captured[index].Length != direction.Length) continue;

            float[] normalized = SphericalKMeans.NormalizedDirection(captured[index]);

            if (normalized.Length == 0) continue;

            if (index != anchor) background.Add(Math.Clamp(SphericalKMeans.DirectionCosine(direction, normalized), -1, 1));

            if (sample.Count < 64)
            {
                foreach (float[] held in sample) pairs.Add(Math.Clamp(SphericalKMeans.DirectionCosine(held, normalized), -1, 1));

                sample.Add(normalized);
            }
        }

        static double Edge(List<double> values, int rank)
        {
            values.Sort();

            int bin = Math.Min(1023, (int)((values[rank - 1] + 1.0) * 512.0));

            return -1.0 + (bin + 1.0) / 512.0;
        }

        // Nothing was seen to judge by: the fallback. A generation too small for a distribution of its own still has its median and its spread
        // read, because a collapsed one is recognised by them and not by its size.
        if (background.Count == 0) return LongRestDiscoveryLimits.PairingFallbackPermille;

        double median = Edge(background, (background.Count + 1) / 2);

        double spread = pairs.Count == 0 ? double.NaN : (Edge(pairs, (3 * pairs.Count + 3) / 4) - Edge(pairs, (pairs.Count + 3) / 4)) / 1.349;

        return LongRestPairing.BackgroundFloor(median, spread, background.Count);
    }
}
