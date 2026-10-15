namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// Vectors for a corpus that wants a group of related memories among many. A generation of more than fifteen other leaves has a distribution of
/// its own, and a memory is paired with an anchor only when it stands out of that distribution, so a corpus of many memories that all look alike
/// has no neighbour at all. A group is therefore a tight cluster among memories that each have a vector of their own.
/// </summary>
internal static class LongRestClusters
{
    /// <summary>The width of the vectors: wide enough that two unrelated ones are nearly orthogonal, as real embeddings are.</summary>
    internal const int Width = 64;

    /// <summary>
    /// The vector of the <paramref name="number"/>th memory (from one) when the first memories form clusters of <paramref name="sizes"/>: a shared axis
    /// per cluster and an axis of each member's own, so two of a cluster are 0.96 alike and a member of one cluster is orthogonal to a member of
    /// another. Every memory past the clusters has a vector of its own, as <see cref="Background"/> draws it.
    /// </summary>
    internal static float[] Vector(int[] sizes, int number)
    {
        int start = 1;

        for (int cluster = 0; cluster < sizes.Length; cluster++)
        {
            if (number < start + sizes[cluster])
            {
                float[] vector = new float[Width];

                vector[cluster] = 0.98f;

                vector[sizes.Length + number] = (float)Math.Sqrt(1 - 0.98 * 0.98);

                return vector;
            }

            start += sizes[cluster];
        }

        return Background(number);
    }

    /// <summary>
    /// A vector of its own: SplitMix64 from the number, so two numbers that differ by one are as unrelated as any two, which the framework's
    /// seeded generator does not promise. In sixty-four dimensions the cosine of two such vectors sits near zero, far below any floor.
    /// </summary>
    internal static float[] Background(int number)
    {
        ulong state = checked((ulong)number) * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL;

        float[] vector = new float[Width];

        for (int index = 0; index < vector.Length; index++)
        {
            state += 0x9E3779B97F4A7C15UL;

            ulong mixed = state;

            mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;

            mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;

            mixed ^= mixed >> 31;

            vector[index] = (float)((mixed >> 11) * (1.0 / (1UL << 53)) * 2.0 - 1.0);
        }

        return vector;
    }
}
