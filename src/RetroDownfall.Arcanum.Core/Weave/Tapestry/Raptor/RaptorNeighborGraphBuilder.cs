using System.Collections.Immutable;
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
    int DescentIterations,
    ImmutableArray<string> StableIds = default)
{
    /// <summary>Logical pair requests, distinct from actual scalar distance calculations.</summary>
    public long DistanceRequests { get; init; }

    public long MemoLookups { get; init; }

    public long MemoHits { get; init; }

    public long MemoEvictions { get; init; }

    /// <summary>Actual forward Heap.Add checks, separate from scalar and memo work.</summary>
    public long ForwardHeapChecks { get; init; }

    /// <summary>Candidate membership visits, including enumeration without scalar work.</summary>
    public long PairEnumerationVisits { get; init; }

    /// <summary>Transpose, cursor, marker and target flat-array payload bytes.</summary>
    public long PairEnumerationAllocatedBytes { get; init; }

    /// <summary>Bounded flat-array payload bytes; excludes ordinary object/array headers.</summary>
    public long MemoAllocatedBytes { get; init; }
}

/// <summary>Content-free bounded search progress; optional observers do not participate in arithmetic.</summary>
public readonly record struct RaptorNeighborProgress(string Phase, long DistanceEvaluations, long WorkSteps = 0, long MemoHits = 0, long MemoEvictions = 0, int InitializedRows = 0, int ProcessedRows = 0, int Pass = 0, long PairEnumerationVisits = 0);

/// <summary>
/// First-party scalar cosine kNN. Large inputs use seeded median random-projection trees followed
/// by NN-descent local joins of new/old forward and bounded reverse neighbors. Algorithm reference:
/// Dong, Charikar and Li, WWW 2011, https://doi.org/10.1145/1963405.1963487. No upstream code is ported.
/// Storage is O(n(d+k)); there is no large-input all-pairs fallback. Approximate search is not UMAP.
/// </summary>
public sealed class RaptorNeighborGraphBuilder(IProgress<RaptorNeighborProgress>? progress = null)
{
    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    /// <summary>Copies complete logical round state only for same-runtime test comparisons.</summary>
    internal Action<int, bool, ReadOnlyMemory<int>, ReadOnlyMemory<int>, ReadOnlyMemory<double>, ReadOnlyMemory<bool>>? RoundStateForTesting { get; init; }

    /// <summary>Tests supply the frozen original pair visitation; production never selects a fallback.</summary>
    internal Func<ReadOnlyMemory<int>, ReadOnlyMemory<int>, ReadOnlyMemory<bool>, CancellationToken, IEnumerable<(int Left, int Right)>>? PairOrderForTesting { get; init; }

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
            return new RaptorNeighborGraph(0, 0, new int[1], Array.Empty<int>(), Array.Empty<double>(), 0, 0, []);
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

        Search search = new(vectors, width, !exact && neighbors > 1, cancellationToken, progress, PairOrderForTesting, RoundStateForTesting);

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

        return new RaptorNeighborGraph(count, neighbors, offsets, indices, distances, search.Evaluations, iterations, [.. points.Select(point => point.StableId)])
        {
            DistanceRequests = search.Requests,
            MemoLookups = search.MemoLookups,
            MemoHits = search.MemoHits,
            MemoEvictions = search.MemoEvictions,
            ForwardHeapChecks = search.ForwardHeapChecks,
            PairEnumerationVisits = search.PairEnumerationVisits,
            PairEnumerationAllocatedBytes = search.PairEnumerationAllocatedBytes,
            MemoAllocatedBytes = search.MemoAllocatedBytes,
        };
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

    private sealed class Search(
        double[][] vectors,
        int width,
        bool useMemo,
        CancellationToken cancellationToken,
        IProgress<RaptorNeighborProgress>? observer,
        Func<ReadOnlyMemory<int>, ReadOnlyMemory<int>, ReadOnlyMemory<bool>, CancellationToken, IEnumerable<(int Left, int Right)>>? pairOrder,
        Action<int, bool, ReadOnlyMemory<int>, ReadOnlyMemory<int>, ReadOnlyMemory<double>, ReadOnlyMemory<bool>>? roundState)
    {
        internal Heap[] Heaps { get; } = Enumerable.Range(0, vectors.Length).Select(_ => new Heap(width)).ToArray();

        internal long Evaluations { get; private set; }

        private readonly PairMemo? _memo = useMemo ? new(vectors.Length, width, vectors[0].Length) : null;

        internal long Requests { get; private set; }

        internal long ForwardHeapChecks { get; private set; }

        internal long PairEnumerationVisits { get; private set; }

        internal long PairEnumerationAllocatedBytes { get; private set; }

        internal long MemoLookups { get; private set; }

        internal long MemoHits { get; private set; }

        internal long MemoAllocatedBytes => _memo?.AllocatedBytes ?? 0;

        internal long MemoEvictions => _memo?.Evictions ?? 0;

        private int _processedRows;

        private int _pass;

        private string _phase = "exact";

        private double Distance(int left, int right, out bool reused)
        {
            cancellationToken.ThrowIfCancellationRequested();

            reused = false;

            Requests++;

            // The normalized buffers are immutable for this Search. Finite scalar multiplication
            // is symmetric, with the same dimension/accumulation order in either orientation.
            // Cache keys identify exact canonical indices, never vector/content equality.
            ulong key = ((ulong)(uint)Math.Min(left, right) << 32 | (uint)Math.Max(left, right)) + 1;

            if (_memo is not null)
            {
                MemoLookups++;

                if (_memo.TryGet(key, out double cached))
                {
                    MemoHits++;

                    reused = true;

                    ReportWork();

                    return cached;
                }
            }

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

            double distance = identical ? 0 : Math.Clamp(1 - dot, 0, 2);

            _memo?.Store(key, distance);

            ReportWork();

            return distance;
        }

        private void ReportWork()
        {
            // Logical requests still advance on a long hit-only run. Actual scalar evaluations
            // remain truthful and must not be used as the sole progress/cancellation clock.
            if (observer is not null && Requests % Options.NeighborProgressInterval == 0)
            {
                observer.Report(new(_phase, Evaluations, Requests, MemoHits, MemoEvictions, vectors.Length, _processedRows, _pass, PairEnumerationVisits));

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private void ReportRows(string phase, int rows, int pass)
        {
            _phase = phase;

            _processedRows = rows;

            _pass = pass;

            if (observer is not null && rows % Options.NeighborProgressInterval == 0)
            {
                observer.Report(new(_phase, Evaluations, Requests, MemoHits, MemoEvictions, vectors.Length, rows, pass, PairEnumerationVisits));

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private int Join(int left, int right)
        {
            if (left == right)
            {
                return 0;
            }

            double distance = Distance(left, right, out bool reused);

            // A cached pair completed a prior Join in this continuing Search. Forward heaps
            // never clear and their total distance/index thresholds only tighten: a resident
            // pair is a duplicate, and an evicted/rejected pair cannot improve either heap now.
            // Its two Add calls are therefore proven no-ops, including their IsNew flags.
            if (reused) return 0;

            ForwardHeapChecks++;

            int changes = Heaps[left].Add(new(right, distance, true)) ? 1 : 0;

            ForwardHeapChecks++;

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
                    ReportRows("exact", left, 0);

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
                ReportRows("initialization", 0, tree + 1);

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
                ReportRows("initialization", row, Options.NeighborProjectionTrees + 1);

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

                        ReportRows("initialization", _processedRows + 1, _pass);
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
                    ReportRows("initialization", row, 0);

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
            ReportRows("descent-allocation", 0, 0);

            Heap[] reverse = Enumerable.Range(0, vectors.Length).Select(_ => new Heap(width)).ToArray();

            Entry[] candidates = new Entry[checked(vectors.Length * width * 2)];

            int[] sizes = new int[vectors.Length];

            // One reference per candidate slot plus four linear arrays, never an n-by-n table.
            // The checked candidate length already bounds every prefix sum and encoded slot.
            int[] memberships = new int[candidates.Length];

            int[] offsets = new int[checked(vectors.Length + 1)];

            int[] cursors = new int[vectors.Length];

            int[] markers = new int[vectors.Length];

            int[] targets = new int[vectors.Length];

            PairEnumerationAllocatedBytes = ((long)candidates.Length + 4L * vectors.Length + 1) * sizeof(int);

            for (int iteration = 0; iteration < Options.NeighborMaximumIterations; iteration++)
            {
                for (int row = 0; row < vectors.Length; row++)
                {
                    ReportRows("descent-clear", row, iteration + 1);

                    reverse[row].Clear();
                }

                for (int row = 0; row < vectors.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ReportRows("descent-reverse", row, iteration + 1);

                    foreach (Entry entry in Heaps[row].Entries)
                    {
                        reverse[entry.Index].Add(new(row, entry.Distance, entry.IsNew));
                    }
                }

                for (int row = 0; row < vectors.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ReportRows("descent-candidates", row, iteration + 1);

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

                if (pairOrder is { } reference)
                {
                    var snapshot = CandidateSnapshot(candidates, sizes);

                    roundState?.Invoke(iteration + 1, true, snapshot.Offsets, snapshot.Indices, snapshot.Distances, snapshot.New);

                    _phase = "descent";

                    _processedRows = 0;

                    _pass = iteration + 1;

                    foreach ((int left, int right) in reference(snapshot.Offsets, snapshot.Indices, snapshot.New, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        changes += Join(left, right);
                    }
                }
                else
                {
                    if (roundState is not null)
                    {
                        var snapshot = CandidateSnapshot(candidates, sizes);

                        roundState(iteration + 1, true, snapshot.Offsets, snapshot.Indices, snapshot.Distances, snapshot.New);
                    }

                    changes = JoinFrozenPairs(candidates, sizes, memberships, offsets, cursors, markers, targets, iteration + 1);
                }

                if (roundState is not null)
                {
                    var snapshot = ForwardSnapshot();

                    roundState(iteration + 1, false, snapshot.Offsets, snapshot.Indices, snapshot.Distances, snapshot.New);
                }

                if (changes == 0)
                {
                    return iteration + 1;
                }
            }

            return Options.NeighborMaximumIterations;
        }

        private long JoinFrozenPairs(Entry[] candidates, int[] sizes, int[] memberships,
            int[] offsets, int[] cursors, int[] markers, int[] targets, int iteration)
        {
            int stride = checked(width * 2);

            Array.Clear(offsets);

            Array.Clear(markers);

            _pass = iteration;

            _processedRows = 0;

            for (int row = 0; row < vectors.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int item = 0; item < sizes[row]; item++)
                {
                    VisitPairMembership();

                    offsets[candidates[row * stride + item].Index + 1]++;
                }
            }

            for (int row = 0; row < vectors.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                offsets[row + 1] = checked(offsets[row + 1] + offsets[row]);

                cursors[row] = offsets[row];
            }

            for (int row = 0; row < vectors.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int item = 0; item < sizes[row]; item++)
                {
                    VisitPairMembership();

                    int slot = row * stride + item;

                    memberships[cursors[candidates[slot].Index]++] = slot;
                }
            }

            long changes = 0;

            for (int left = 0; left < vectors.Length; left++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _processedRows = left;

                int count = 0;

                int stamp = left + 1;

                for (int position = offsets[left]; position < offsets[left + 1]; position++)
                {
                    int slot = memberships[position];

                    int pool = slot / stride;

                    bool fresh = candidates[slot].IsNew;

                    for (int item = 0; item < sizes[pool]; item++)
                    {
                        VisitPairMembership();

                        Entry other = candidates[pool * stride + item];

                        if (other.Index <= left || !(fresh || other.IsNew) || markers[other.Index] == stamp) continue;

                        markers[other.Index] = stamp;

                        targets[count++] = other.Index;
                    }
                }

                // Exact union of the original frozen pools: every eligible unordered pair once.
                // The total heap comparator makes top-width membership independent of visitation.
                // MarkOld precedes the round, so final new flags and zero-change stopping agree too.
                Array.Sort(targets, 0, count);

                cancellationToken.ThrowIfCancellationRequested();

                _phase = "descent";

                for (int item = 0; item < count; item++) changes += Join(left, targets[item]);
            }

            return changes;
        }

        private void VisitPairMembership()
        {
            PairEnumerationVisits++;

            if (PairEnumerationVisits % Options.NeighborProgressInterval != 0) return;

            _phase = "descent-pair-enumeration";

            observer?.Report(new(_phase, Evaluations, Requests, MemoHits, MemoEvictions,
                vectors.Length, _processedRows, _pass, PairEnumerationVisits));

            // Enumeration may perform many marker hits without another Distance request.
            cancellationToken.ThrowIfCancellationRequested();
        }

        private (int[] Offsets, int[] Indices, double[] Distances, bool[] New) CandidateSnapshot(Entry[] candidates, int[] sizes)
        {
            int[] offsets = new int[vectors.Length + 1];

            for (int row = 0; row < vectors.Length; row++) offsets[row + 1] = checked(offsets[row] + sizes[row]);

            int[] indices = new int[offsets[^1]];

            double[] distances = new double[indices.Length];

            bool[] fresh = new bool[indices.Length];

            for (int row = 0; row < vectors.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int item = 0; item < sizes[row]; item++)
                {
                    Entry entry = candidates[row * width * 2 + item];

                    indices[offsets[row] + item] = entry.Index;

                    distances[offsets[row] + item] = entry.Distance;

                    fresh[offsets[row] + item] = entry.IsNew;
                }
            }

            return (offsets, indices, distances, fresh);
        }

        private (int[] Offsets, int[] Indices, double[] Distances, bool[] New) ForwardSnapshot()
        {
            int[] offsets = new int[vectors.Length + 1];

            for (int row = 0; row < vectors.Length; row++) offsets[row + 1] = checked(offsets[row] + Heaps[row].Count);

            int[] indices = new int[offsets[^1]];

            double[] distances = new double[indices.Length];

            bool[] fresh = new bool[indices.Length];

            for (int row = 0; row < vectors.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int item = offsets[row];

                foreach (Entry entry in Heaps[row].Entries)
                {
                    indices[item] = entry.Index;

                    distances[item] = entry.Distance;

                    fresh[item++] = entry.IsNew;
                }
            }

            return (offsets, indices, distances, fresh);
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

    /// <summary>
    /// Four-way exact-key memo. Eviction/collision only loses reuse, never changes a distance.
    /// Payload is at most 260 MiB, O(n*k), also scaled down for narrow input vectors. Sizing
    /// saturates before multiplication, and power-of-two growth stays below the explicit cap.
    /// These are implementation storage bounds, not numerical/search recipe constants.
    /// </summary>
    private sealed class PairMemo
    {
        private const int MaximumEntries = 1 << 24;

        private readonly ulong[] _keys;

        private readonly double[] _distances;

        private readonly byte[] _replacement;

        private readonly int _bucketMask;

        internal PairMemo(int count, int width, int dimensions)
        {
            long byNeighbors = Math.Min((long)count * width, MaximumEntries / 16) * 16;

            long byDimensions = Math.Min((long)count * dimensions, MaximumEntries / 8) * 8;

            long requested = Math.Min(byNeighbors, byDimensions);

            int entries = 4;

            while (entries < requested) entries *= 2;

            _keys = new ulong[entries];

            _distances = new double[entries];

            _replacement = new byte[entries / 4];

            _bucketMask = _replacement.Length - 1;
        }

        internal long Evictions { get; private set; }

        internal long AllocatedBytes => (long)_keys.Length * (sizeof(ulong) + sizeof(double)) + _replacement.Length;

        internal bool TryGet(ulong key, out double distance)
        {
            int start = Bucket(key) * 4;

            for (int offset = 0; offset < 4; offset++)
            {
                if (_keys[start + offset] == key)
                {
                    distance = _distances[start + offset];

                    return true;
                }
            }

            distance = 0;

            return false;
        }

        internal void Store(ulong key, double distance)
        {
            int bucket = Bucket(key);

            int start = bucket * 4;

            int offset = 0;

            while (offset < 4 && _keys[start + offset] != 0) offset++;

            if (offset == 4)
            {
                Evictions++;

                offset = _replacement[bucket];

                _replacement[bucket] = (byte)((offset + 1) & 3);
            }

            _keys[start + offset] = key;

            _distances[start + offset] = distance;
        }

        private int Bucket(ulong key)
        {
            // Integer mixing affects eviction only; every hit still checks the complete pair key.
            ulong mixed = key;

            mixed = unchecked((mixed ^ (mixed >> 33)) * 0xff51afd7ed558ccdUL);

            mixed = unchecked((mixed ^ (mixed >> 33)) * 0xc4ceb9fe1a85ec53UL);

            return (int)((mixed ^ (mixed >> 33)) & (uint)_bucketMask);
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
