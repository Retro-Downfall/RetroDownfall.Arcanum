using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>
/// CSR rows and column indices use the caller's original input indices. Neighbors includes self,
/// always first at zero distance; remaining entries sort by cosine distance then ordinal stable id.
/// Empty input has one zero offset and no edges. Equal vectors never collapse sample identities.
/// </summary>
public sealed record RaptorNeighborGraph(
    int PointCount,
    int Neighbors,
    ReadOnlyMemory<int> RowOffsets,
    ReadOnlyMemory<int> SourceIndices,
    ReadOnlyMemory<double> Distances,
    long DistanceEvaluations,
    int DescentIterations);

/// <summary>Content-free bounded search progress; optional observers do not participate in arithmetic.</summary>
public readonly record struct RaptorNeighborProgress(string Phase, long DistanceEvaluations);

/// <summary>
/// First-party scalar cosine kNN. Large inputs use seeded median random-projection trees followed
/// by NN-descent local joins of new/old forward and bounded reverse neighbors. Algorithm reference:
/// Dong, Charikar and Li, WWW 2011, https://doi.org/10.1145/1963405.1963487. No upstream code is ported.
/// Storage is O(n(d+k)); there is no large-input all-pairs fallback. Approximate search is not UMAP.
/// </summary>
public sealed class RaptorNeighborGraphBuilder(IProgress<RaptorNeighborProgress>? progress = null)
{
    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    /// <summary>
    /// Requires distinct nonblank ids and finite nonzero equal-width vectors. k is in [1,n], or
    /// any positive k for empty input (effective k=0). Invalid input returns a content-free Result;
    /// cancellation throws OperationCanceledException, including inside candidate evaluation loops.
    /// </summary>
    public Result<RaptorNeighborGraph> Build(IReadOnlyList<RaptorPoint> points, int neighbors, ulong seed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (points is null)
        {
            return new Error("Raptor.InvalidInput", "Neighbor input is invalid.");
        }

        int count = points.Count;

        if (neighbors < 1 || (count > 0 && neighbors > count))
        {
            return new Error("Raptor.InvalidNeighbors", "Neighbor count is outside the input bounds.");
        }

        if (count == 0)
        {
            return new RaptorNeighborGraph(0, 0, new int[1], Array.Empty<int>(), Array.Empty<double>(), 0, 0);
        }

        if ((long)count * neighbors > Array.MaxLength)
        {
            return new Error("Raptor.TooLarge", "Neighbor output exceeds managed array bounds.");
        }

        int[] order = Enumerable.Range(0, count).ToArray();

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (points[index] is null || !IsValidId(points[index].StableId))
            {
                return new Error("Raptor.InvalidInput", "Neighbor input is invalid.");
            }
        }

        Array.Sort(order, (left, right) => StringComparer.Ordinal.Compare(points[left].StableId, points[right].StableId));

        int dimensions = points[order[0]].Vector.Length;

        double[][] vectors = new double[count][];

        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RaptorPoint point = points[order[row]];

            if (dimensions == 0 || point.Vector.Length != dimensions || (row > 0 && StringComparer.Ordinal.Equals(point.StableId, points[order[row - 1]].StableId)))
            {
                return new Error("Raptor.InvalidInput", "Neighbor input is invalid.");
            }

            ReadOnlySpan<float> source = point.Vector.Span;

            double norm = 0;

            for (int dimension = 0; dimension < dimensions; dimension++)
            {
                if (!float.IsFinite(source[dimension]))
                {
                    return new Error("Raptor.InvalidInput", "Neighbor input is invalid.");
                }

                norm += (double)source[dimension] * source[dimension];
            }

            if (norm == 0)
            {
                return new Error("Raptor.InvalidInput", "Neighbor input is invalid.");
            }

            norm = Math.Sqrt(norm);

            vectors[row] = new double[dimensions];

            for (int dimension = 0; dimension < dimensions; dimension++)
            {
                vectors[row][dimension] = source[dimension] / norm;
            }
        }

        bool exact = count <= Options.ExactNeighborLimit;

        int width = exact ? neighbors - 1 : (int)Math.Min(count - 1L, Math.Max(Options.NeighborMinimumSearchWidth, (long)(neighbors - 1) * Options.NeighborSearchMultiplier));

        Search search = new(vectors, width, cancellationToken, progress);

        int iterations = 0;

        if (neighbors > 1)
        {
            if (exact)
            {
                search.Exact();
            }
            else
            {
                search.Initialize(seed);

                iterations = search.Descend();
            }
        }

        int[] offsets = new int[count + 1];

        int[] indices = new int[count * neighbors];

        double[] distances = new double[indices.Length];

        Entry[] sorted = new Entry[width];

        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int original = order[row];

            int offset = original * neighbors;

            offsets[original] = offset;

            indices[offset] = original;

            search.Heaps[row].CopyTo(sorted);

            Array.Sort(sorted, EntryComparer.Instance);

            for (int column = 1; column < neighbors; column++)
            {
                indices[offset + column] = order[sorted[column - 1].Index];

                distances[offset + column] = sorted[column - 1].Distance;
            }
        }

        offsets[count] = indices.Length;

        return new RaptorNeighborGraph(count, neighbors, offsets, indices, distances, search.Evaluations, iterations);
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        for (int index = 0; index < id.Length; index++)
        {
            if (char.IsHighSurrogate(id[index]))
            {
                if (++index == id.Length || !char.IsLowSurrogate(id[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(id[index]))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Search(double[][] vectors, int width, CancellationToken cancellationToken, IProgress<RaptorNeighborProgress>? observer)
    {
        internal Heap[] Heaps { get; } = Enumerable.Range(0, vectors.Length).Select(_ => new Heap(width)).ToArray();

        internal long Evaluations { get; private set; }

        private string _phase = "exact";

        private double Distance(int left, int right)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double dot = 0;

            bool identical = true;

            double[] a = vectors[left];

            double[] b = vectors[right];

            for (int dimension = 0; dimension < a.Length; dimension++)
            {
                dot += a[dimension] * b[dimension];

                identical &= a[dimension] == b[dimension];
            }

            Evaluations++;

            if (observer is not null && Evaluations % Options.NeighborProgressInterval == 0)
            {
                observer.Report(new(_phase, Evaluations));

                cancellationToken.ThrowIfCancellationRequested();
            }

            return identical ? 0 : Math.Clamp(1 - dot, 0, 2);
        }

        private int Join(int left, int right)
        {
            if (left == right)
            {
                return 0;
            }

            double distance = Distance(left, right);

            int changes = Heaps[left].Add(new(right, distance, true)) ? 1 : 0;

            return changes + (Heaps[right].Add(new(left, distance, true)) ? 1 : 0);
        }

        internal void Exact()
        {
            // Blocking bounds the working traversal; only the n*k heaps retain distances.
            for (int block = 0; block < vectors.Length; block += Options.NeighborExactBlockSize)
            {
                int end = Math.Min(vectors.Length, block + Options.NeighborExactBlockSize);

                for (int left = block; left < end; left++)
                {
                    for (int right = left + 1; right < vectors.Length; right++)
                    {
                        Join(left, right);
                    }
                }
            }
        }

        internal void Initialize(ulong seed)
        {
            _phase = "initialization";

            InitializeIdenticalVectors();

            TapestryDeterministicRandom random = new(seed);

            Projection[] projections = new Projection[vectors.Length];

            double[] direction = new double[vectors[0].Length];

            int leafSize = Math.Max(Options.NeighborMinimumLeafSize, width);

            for (int tree = 0; tree < Options.NeighborProjectionTrees; tree++)
            {
                for (int row = 0; row < vectors.Length; row++)
                {
                    projections[row] = new(row, 0);
                }

                Partition(0, vectors.Length);
            }

            // A seeded coprime stride visits each index at most once; fill is always bounded and
            // cannot spin on duplicate vectors or a nearly full neighborhood.
            for (int row = 0; row < vectors.Length; row++)
            {
                int candidate = (int)(random.NextUInt64() % (ulong)vectors.Length);

                int stride = 1 + (int)(random.NextUInt64() % (ulong)(vectors.Length - 1));

                while (GreatestCommonDivisor(stride, vectors.Length) != 1)
                {
                    stride = stride == vectors.Length - 1 ? 1 : stride + 1;
                }

                for (int visited = 0; visited < vectors.Length && Heaps[row].Count < width; visited++)
                {
                    Join(row, candidate);

                    candidate = (int)(((long)candidate + stride) % vectors.Length);
                }
            }

            void Partition(int start, int length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (length <= leafSize)
                {
                    for (int left = start; left < start + length; left++)
                    {
                        for (int right = left + 1; right < start + length; right++)
                        {
                            Join(projections[left].Index, projections[right].Index);
                        }
                    }

                    return;
                }

                int first = (int)(random.NextUInt64() % (ulong)length);

                int second = (first + 1 + (int)(random.NextUInt64() % (ulong)(length - 1))) % length;

                double[] a = vectors[projections[start + first].Index];

                double[] b = vectors[projections[start + second].Index];

                for (int dimension = 0; dimension < direction.Length; dimension++)
                {
                    direction[dimension] = a[dimension] - b[dimension];
                }

                for (int position = start; position < start + length; position++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int index = projections[position].Index;

                    double projection = 0;

                    for (int dimension = 0; dimension < direction.Length; dimension++)
                    {
                        projection += vectors[index][dimension] * direction[dimension];
                    }

                    projections[position] = new(index, projection);
                }

                // Median splitting terminates even if every vector/projection is identical.
                Array.Sort(projections, start, length, ProjectionComparer.Instance);

                int half = length / 2;

                Partition(start, half);

                Partition(start + half, length - half);
            }
        }

        private void InitializeIdenticalVectors()
        {
            // This is bounded seeding, not deduplication: every sample keeps its own heap and
            // original index. Canonical anchors prevent zero-distance groups from getting stuck
            // in disconnected equal-distance neighborhoods after reverse-list truncation.
            int[] equalOrder = Enumerable.Range(0, vectors.Length).ToArray();

            Array.Sort(equalOrder, (left, right) =>
            {
                for (int dimension = 0; dimension < vectors[left].Length; dimension++)
                {
                    int comparison = vectors[left][dimension].CompareTo(vectors[right][dimension]);

                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }

                return left.CompareTo(right);
            });

            for (int start = 0; start < equalOrder.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int end = start + 1;

                while (end < equalOrder.Length && vectors[equalOrder[start]].AsSpan().SequenceEqual(vectors[equalOrder[end]]))
                {
                    end++;
                }

                int anchorEnd = Math.Min(end, start + width + 1);

                for (int row = start; row < end; row++)
                {
                    for (int anchor = start; anchor < anchorEnd; anchor++)
                    {
                        Join(equalOrder[row], equalOrder[anchor]);
                    }
                }

                start = end;
            }
        }

        internal int Descend()
        {
            _phase = "descent";

            Heap[] reverse = Enumerable.Range(0, vectors.Length).Select(_ => new Heap(width)).ToArray();

            Entry[] candidates = new Entry[checked(vectors.Length * width * 2)];

            int[] sizes = new int[vectors.Length];

            for (int iteration = 0; iteration < Options.NeighborMaximumIterations; iteration++)
            {
                for (int row = 0; row < vectors.Length; row++)
                {
                    reverse[row].Clear();
                }

                for (int row = 0; row < vectors.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (Entry entry in Heaps[row].Entries)
                    {
                        reverse[entry.Index].Add(new(row, entry.Distance, entry.IsNew));
                    }
                }

                for (int row = 0; row < vectors.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int offset = row * width * 2;

                    int size = 0;

                    foreach (Entry entry in Heaps[row].Entries)
                    {
                        candidates[offset + size++] = entry;
                    }

                    foreach (Entry entry in reverse[row].Entries)
                    {
                        int existing = 0;

                        while (existing < size && candidates[offset + existing].Index != entry.Index)
                        {
                            existing++;
                        }

                        if (existing == size)
                        {
                            candidates[offset + size++] = entry;
                        }
                        else if (entry.IsNew)
                        {
                            candidates[offset + existing] = candidates[offset + existing] with { IsNew = true };
                        }
                    }

                    sizes[row] = size;

                    Heaps[row].MarkOld();
                }

                long changes = 0;

                for (int row = 0; row < vectors.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int offset = row * width * 2;

                    for (int left = 0; left < sizes[row]; left++)
                    {
                        for (int right = left + 1; right < sizes[row]; right++)
                        {
                            Entry a = candidates[offset + left];

                            Entry b = candidates[offset + right];

                            if (a.IsNew || b.IsNew)
                            {
                                changes += Join(a.Index, b.Index);
                            }
                        }
                    }
                }

                if (changes == 0)
                {
                    return iteration + 1;
                }
            }

            return Options.NeighborMaximumIterations;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return a;
        }
    }

    private readonly record struct Projection(int Index, double Value);

    private sealed class ProjectionComparer : IComparer<Projection>
    {
        internal static readonly ProjectionComparer Instance = new();

        public int Compare(Projection left, Projection right)
        {
            int comparison = left.Value.CompareTo(right.Value);

            return comparison == 0 ? left.Index.CompareTo(right.Index) : comparison;
        }
    }

    private readonly record struct Entry(int Index, double Distance, bool IsNew);

    private sealed class EntryComparer : IComparer<Entry>
    {
        internal static readonly EntryComparer Instance = new();

        public int Compare(Entry left, Entry right)
        {
            int comparison = left.Distance.CompareTo(right.Distance);

            return comparison == 0 ? left.Index.CompareTo(right.Index) : comparison;
        }
    }

    private sealed class Heap(int capacity)
    {
        private readonly Entry[] _entries = new Entry[capacity];

        internal int Count { get; private set; }

        internal ReadOnlySpan<Entry> Entries => _entries.AsSpan(0, Count);

        internal void Clear() => Count = 0;

        internal void CopyTo(Entry[] target) => Entries.CopyTo(target);

        internal void MarkOld()
        {
            for (int index = 0; index < Count; index++)
            {
                _entries[index] = _entries[index] with { IsNew = false };
            }
        }

        internal bool Add(Entry entry)
        {
            if (capacity == 0 || (Count == capacity && EntryComparer.Instance.Compare(entry, _entries[0]) >= 0))
            {
                return false;
            }

            for (int index = 0; index < Count; index++)
            {
                if (_entries[index].Index == entry.Index)
                {
                    return false;
                }
            }

            if (Count < capacity)
            {
                int child = Count++;

                while (child > 0)
                {
                    int parent = (child - 1) / 2;

                    if (EntryComparer.Instance.Compare(_entries[parent], entry) >= 0)
                    {
                        break;
                    }

                    _entries[child] = _entries[parent];

                    child = parent;
                }

                _entries[child] = entry;
            }
            else
            {
                int parent = 0;

                while (parent * 2 + 1 < Count)
                {
                    int child = parent * 2 + 1;

                    if (child + 1 < Count && EntryComparer.Instance.Compare(_entries[child + 1], _entries[child]) > 0)
                    {
                        child++;
                    }

                    if (EntryComparer.Instance.Compare(entry, _entries[child]) >= 0)
                    {
                        break;
                    }

                    _entries[parent] = _entries[child];

                    parent = child;
                }

                _entries[parent] = entry;
            }

            return true;
        }
    }
}
