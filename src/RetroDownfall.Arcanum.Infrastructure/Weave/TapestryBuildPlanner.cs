using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Pure layer admission: no provider effects, storage or production registration. Every accepted
/// plan covers the exact input identities and strictly contracts before any summary can be spent.
/// WorkLimit is explicitly incomplete: callers must retain the old generation and back off.
/// </summary>
public sealed class TapestryBuildPlanner(int maximumChildren = 24)
{
    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    public Result<TapestryLayerPlan> Plan(TapestryScope scope, IReadOnlyList<TapestryWorkingNode> nodes, int layer, ITapestryClusterer clusterer, ITapestrySummarizer summarizer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (nodes is null || clusterer is null || summarizer is null || layer < 0 || maximumChildren < 2 || nodes.Count > TapestryLimits.MaxLeavesPerScope || !Enum.IsDefined(scope.Kind) || string.IsNullOrWhiteSpace(scope.Id)) return InvalidInput();

        var byId = new Dictionary<string, TapestryWorkingNode>(StringComparer.Ordinal);

        int width = nodes.Count == 0 ? 0 : nodes[0]?.Embedding.Length ?? 0;

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is null || string.IsNullOrWhiteSpace(node.StableId) || node.Content is null || !byId.TryAdd(node.StableId, node) || width == 0 || node.Embedding.Length != width) return InvalidInput();

            bool nonzero = false;

            foreach (float value in node.Embedding.Span)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!float.IsFinite(value)) return InvalidInput();

                nonzero |= value != 0;
            }

            if (!nonzero) return InvalidInput();
        }

        string[] rootIds = byId.Keys.Order(StringComparer.Ordinal).ToArray();

        TapestryLayerPlan Terminal(TapestryPlanTerminalReason reason) => new([], rootIds.Select(id => new TapestryPlannedCarry(id, TapestryPlanReason.None, [])).ToImmutableArray(), reason,
            reason == TapestryPlanTerminalReason.WorkLimit ? TapestryPlanDisposition.Incomplete : TapestryPlanDisposition.Terminal, []);

        if (nodes.Count <= Options.ReductionDimensions + 1) return Terminal(TapestryPlanTerminalReason.SmallLayer);

        long remaining = checked((long)Options.PlannerMemberVisitsPerInput * nodes.Count);

        int calls = 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var evidence = ImmutableArray.CreateBuilder<TapestrySplitEvidence>();

        var pending = new Stack<Candidate>();

        var summaries = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        var carries = new Dictionary<string, TapestryPlannedCarry>(StringComparer.Ordinal);

        bool exhausted = false;

        Error? failure = null;

        var root = new Candidate(rootIds, new Dictionary<string, ImmutableArray<TapestryMembershipPath>>(StringComparer.Ordinal), TapestryPlanReason.None);

        Split(root);

        while (pending.Count > 0 && !exhausted && failure is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Candidate candidate = pending.Pop();

            if (candidate.Ids.Length == 1)
            {
                Carry(candidate.Ids[0], candidate, Fits(candidate.Ids) ? TapestryPlanReason.SingletonCarry : TapestryPlanReason.OversizedCarry);
            }
            else if (candidate.Ids.Length <= maximumChildren && Fits(candidate.Ids))
            {
                AddSummary(candidate);
            }
            else
            {
                Split(candidate);
            }
        }

        if (failure is not null) return failure;

        if (exhausted) return Terminal(TapestryPlanTerminalReason.WorkLimit);

        var coverage = new HashSet<string>(summaries.Values.SelectMany(c => c.Ids), StringComparer.Ordinal);

        coverage.UnionWith(carries.Keys);

        if (!coverage.SetEquals(rootIds)) return InvalidCoverage();

        if (summaries.Count + carries.Count >= nodes.Count) return Terminal(TapestryPlanTerminalReason.NonContracting);

        return new TapestryLayerPlan(summaries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new TapestryPlannedCluster(pair.Key,
            pair.Value.Ids.Select(id => new TapestryPlannedChild(id, Paths(pair.Value, id))).ToImmutableArray(), pair.Value.Reason)).ToImmutableArray(),
            carries.Values.OrderBy(c => c.StableId, StringComparer.Ordinal).ToImmutableArray(), null, TapestryPlanDisposition.Ready, evidence.ToImmutable());

        bool Fits(string[] ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool fits = summarizer.FitsOneRequest(new(scope.Kind, scope.Id, layer, ids.Select(id => byId[id].Content).ToArray()));

            cancellationToken.ThrowIfCancellationRequested();

            return fits;
        }

        TapestryClusterIdentity Identity(string[] ids) => new(scope, layer, RaptorStage.Split, "bounded-split-v1", ids);

        void Split(Candidate candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = Identity(candidate.Ids);

            if (!seen.Add(identity.MemberDigest))
            {
                Partition(candidate, TapestryPlanReason.NonContractingPartition);

                return;
            }

            if (calls >= nodes.Count || remaining < candidate.Ids.Length)
            {
                exhausted = true;

                return;
            }
            remaining -= candidate.Ids.Length;

            calls++;

            var points = candidate.Ids.Select(id => new RaptorPoint(id, byId[id].Embedding)).ToArray();

            var clustered = clusterer.Cluster(points, identity, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (clustered.IsFailure)
            {
                failure = clustered.Error;

                return;
            }

            if (clustered.Value.Clusters.IsDefault || clustered.Value.Unassigned.IsDefault)
            {
                failure = InvalidCoverage();

                return;
            }

            var covered = new HashSet<int>();

            var next = new Dictionary<string, Candidate>(StringComparer.Ordinal);

            foreach (var cluster in clustered.Value.Clusters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (cluster is null || string.IsNullOrWhiteSpace(cluster.StableId) || cluster.Members.IsDefaultOrEmpty)
                {
                    failure = InvalidCoverage();

                    return;
                }

                var paths = new Dictionary<string, List<TapestryMembershipPath>>(StringComparer.Ordinal);

                foreach (var member in cluster.Members)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (--remaining < 0)
                    {
                        exhausted = true;

                        return;
                    }

                    if (member is null || member.PointIndex < 0 || member.PointIndex >= points.Length || !double.IsFinite(member.GlobalPosterior) || !double.IsFinite(member.LocalPosterior) || member.GlobalPosterior < 0 || member.GlobalPosterior > 1 || member.LocalPosterior < 0 || member.LocalPosterior > 1)
                    {
                        failure = InvalidCoverage();

                        return;
                    }
                    covered.Add(member.PointIndex);

                    string id = points[member.PointIndex].StableId;

                    if (!paths.TryGetValue(id, out var list)) paths[id] = list = [];

                    list.Add(new(identity.MemberDigest, cluster.StableId, member));
                }

                string[] ids = paths.Keys.Order(StringComparer.Ordinal).ToArray();

                var nextCandidate = new Candidate(ids, paths.ToDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal), candidate.Reason);

                string digest = Identity(ids).MemberDigest;

                if (next.TryGetValue(digest, out var existing)) next[digest] = Merge(existing, nextCandidate);
                else next[digest] = nextCandidate;
            }

            foreach (int index in clustered.Value.Unassigned)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (--remaining < 0) { exhausted = true; return; }
                if (index < 0 || index >= points.Length) { failure = InvalidCoverage(); return; }
                covered.Add(index);

                Carry(points[index].StableId, candidate, TapestryPlanReason.SingletonCarry);
            }

            if (covered.Count != points.Length) { failure = InvalidCoverage(); return; }
            evidence.Add(new(identity.MemberDigest, candidate.Ids.ToImmutableArray(), clustered.Value));

            if (next.Values.Any(c => c.Ids.Length == candidate.Ids.Length) && (candidate.Ids.Length > maximumChildren || !Fits(candidate.Ids)))
            {
                var reason = points.All(p => p.Vector.Span.SequenceEqual(points[0].Vector.Span)) ? TapestryPlanReason.DegeneratePartition : TapestryPlanReason.None;

                var full = next.Values.First(c => c.Ids.Length == candidate.Ids.Length);

                Partition(full, reason | TapestryPlanReason.NonContractingPartition);

                return;
            }

            foreach (var child in next.OrderByDescending(p => p.Key, StringComparer.Ordinal)) pending.Push(child.Value);
        }

        // Stable greedy partitions require at most two fit probes per member, and never cut source text.
        void Partition(Candidate candidate, TapestryPlanReason reason)
        {
            var part = new List<string>();

            foreach (string id in candidate.Ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Fits([id]))
                {
                    Flush();

                    Carry(id, candidate with { Reason = candidate.Reason | reason }, TapestryPlanReason.OversizedCarry);

                    continue;
                }

                if (part.Count > 0 && (part.Count == maximumChildren || !Fits([.. part, id]))) Flush();

                part.Add(id);
            }
            Flush();

            void Flush()
            {
                if (part.Count == 0) return;

                var bounded = new Candidate(part.ToArray(), candidate.Paths, candidate.Reason | reason);

                if (part.Count == 1) Carry(part[0], bounded, TapestryPlanReason.SingletonCarry);
                else AddSummary(bounded);

                part.Clear();
            }
        }

        void AddSummary(Candidate candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string id = RaptorClusterer.ClusterId(Identity(candidate.Ids), candidate.Ids);

            if (summaries.TryGetValue(id, out var existing)) summaries[id] = Merge(existing, candidate);
            else summaries[id] = candidate;
        }

        void Carry(string id, Candidate candidate, TapestryPlanReason reason)
        {
            var value = new TapestryPlannedCarry(id, reason | candidate.Reason, Paths(candidate, id));

            if (carries.TryGetValue(id, out var existing)) value = value with { Reason = value.Reason | existing.Reason, Paths = existing.Paths.Union(value.Paths).ToImmutableArray() };

            carries[id] = value;
        }
    }

    private static Candidate Merge(Candidate left, Candidate right) => new(left.Ids, left.Ids.ToDictionary(id => id,
        id => Paths(left, id).Union(Paths(right, id)).ToImmutableArray(), StringComparer.Ordinal), left.Reason | right.Reason | TapestryPlanReason.DuplicatePaths);

    private static ImmutableArray<TapestryMembershipPath> Paths(Candidate candidate, string id) => candidate.Paths.TryGetValue(id, out var paths) ? paths : [];

    private static Error InvalidInput() => new("Tapestry.InvalidPlanInput", "Layer input or child bound is invalid.");

    private static Error InvalidCoverage() => new("Tapestry.InvalidClusterCoverage", "Clustering did not preserve the exact input identities.");

    private sealed record Candidate(string[] Ids, Dictionary<string, ImmutableArray<TapestryMembershipPath>> Paths, TapestryPlanReason Reason);
}
