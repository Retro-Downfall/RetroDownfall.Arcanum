using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class HostedProducerRootWorkers
{
    // The cleanup value-flow and provenance walks recurse natively to the depth their work budgets bound
    // (256 units, several frames each), which an unoptimized build cannot hold in the runtime's default
    // 1.5 MB secondary-thread stack. Discovery and every root worker therefore run on a dedicated stack
    // sized for that bound, so the budget, never the caller's stack, ends a walk.
    internal const int AnalysisStackBytes = 64 * 1024 * 1024;

    internal static T OnAnalysisStack<T>(Func<T> analysis) =>
        StartOnAnalysisStack(analysis).GetAwaiter().GetResult();

    private static Task<T> StartOnAnalysisStack<T>(Func<T> analysis)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(
            () =>
            {
                try
                {
                    completion.SetResult(analysis());
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            },
            AnalysisStackBytes)
        {
            IsBackground = true,
            Name = "Hosted producer analysis",
        };

        thread.Start();

        return completion.Task;
    }

    // The unit a worker takes whole: one root, or every root of a service that declares a lease-handoff root (its roots cut to that root,
    // and only the state cache of the worker that runs them shares the closure between them).
    internal static string FamilyOf(string rootType, string rootOperation, bool serviceDeclaresLeaseHandoff) =>
        serviceDeclaresLeaseHandoff ? rootType + "|<lease-handoff>" : rootType + "|" + rootOperation;

    internal static int[][] OrderFamilies(IEnumerable<(string Family, int Occurrence)> roots) => roots
        .GroupBy(static root => root.Family, StringComparer.Ordinal)
        .OrderBy(static family => FamilyPriority(family.Key))
        .Select(static family => family.Select(static root => root.Occurrence).ToArray()).ToArray();

    // Advisory measured costs only: unmatched families keep their original relative order.
    private static int FamilyPriority(string family) => family switch
    {
        "RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery|RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions.GrimoireOfflineTransitionStartupRecovery.RecoverBeforeBootstrapAsync" => 0,
        "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService|RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService.ApplyFullUnderMaintenanceLockAsync" => 1,
        "GrimoireDatabaseHostedService|RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync" => 2,
        _ => 3,
    };

    internal static int ParseCount(string? value) => value switch
    {
        null or "1" => 1,
        "2" => 2,
        "3" => 3,
        _ => throw new ArgumentException("ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS must be exactly 1, 2 or 3."),
    };

    private static HostedProducerAnalysisMetrics CompleteWorkerMetrics(HostedProducerDiscovery<HostedProducerSite> work)
    {
        HostedProducerAnalysisMetrics metrics = work.AnalysisMetrics
            ?? throw new InvalidOperationException("A traversal worker did not produce its metrics.");

        if (metrics.RootTraversals.GroupBy(static root => (root.RootType, root.RootOperation))
            .Any(static family => family.Count() != 1))
        {
            throw new InvalidOperationException("A traversal worker reported a family more than once.");
        }

        HostedProducerRootTraversalMetric[] joined = [.. metrics.RootTraversals.Where(static root => root.SelfAdmittedWorkKind.HasValue
            || HostedGrimoireProducerInventory.IsGuildRevalidationChildRoot(root.RootType, root.RootOperation))];

        HostedProducerSite[] links = [.. work.Items.Where(static site => site.Kind == HostedProducerSiteKind.JoinedSelfAdmission)];

        bool hasChild = work.Items.Any(HostedGrimoireProducerInventory.IsGuildRevalidationChildSite);

        if (links.Length == 0 && !hasChild && joined.Length == 0)
        {
            return metrics;
        }

        if (links.Length == 0 || !hasChild
            || !links.All(HostedGrimoireProducerInventory.IsGuildRevalidationJoinedEdge)
            || joined is not [HostedProducerRootTraversalMetric
                {
                    SelfAdmittedWorkKind: GrimoireWorkKind.GuildStorage, TraversalCalls: > 0, AnalyzedStates: > 0,
                } admitted]
            || !HostedGrimoireProducerInventory.IsGuildRevalidationChildRoot(admitted.RootType, admitted.RootOperation))
        {
            throw new InvalidOperationException("A traversal worker reported incomplete or mismatched joined GuildStorage evidence.");
        }

        return metrics;
    }

    private static HostedProducerRootTraversalMetric MergeRootTraversal(
        IGrouping<(string RootType, string RootOperation), HostedProducerRootTraversalMetric> family, int limit)
    {
        HostedProducerRootTraversalMetric first = family.First();

        if (!first.SelfAdmittedWorkKind.HasValue)
        {
            return family.Count() == 1
                ? first
                : throw new InvalidOperationException("A traversal budget family was split across workers.");
        }

        // Count repeated work on different workers against the same whole-operation hard cap.
        long total = family.Sum(static metric => (long)metric.AnalyzedStates);

        if (total > limit)
        {
            throw new InvalidOperationException($"HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED: {first.RootOperation} exceeded {limit} aggregate states.");
        }

        return first with
        {
            TraversalCalls = family.Sum(static metric => metric.TraversalCalls),
            AnalyzedStates = (int)total,
            Elapsed = TimeSpan.FromTicks(family.Sum(static metric => metric.Elapsed.Ticks)),
        };
    }

    internal static HostedProducerAnalysisMetrics MergeMetrics(
        IReadOnlyList<HostedProducerDiscovery<HostedProducerSite>> workers, int maximumAnalyzedStatesPerRoot)
    {
        HostedProducerAnalysisMetrics[] metrics = [.. workers.Select(CompleteWorkerMetrics)];

        HostedProducerRootTraversalMetric[] rootTraversals = [.. metrics.SelectMany(static metric => metric.RootTraversals)
            .GroupBy(static metric => (metric.RootType, metric.RootOperation))
            .OrderBy(static family => family.Key.RootType, StringComparer.Ordinal)
            .ThenBy(static family => family.Key.RootOperation, StringComparer.Ordinal)
            .Select(family => MergeRootTraversal(family, maximumAnalyzedStatesPerRoot))];

        return new(
            metrics.SelectMany(static metric => metric.RecoveryConditions)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerRecoveryConditionMetric(
                    group.Key,
                    group.Max(static item => item.MaximumEvaluationsPerTraversalState),
                    group.Sum(static item => item.TotalEvaluationsAcrossTraversalStates),
                    group.Sum(static item => item.FeasibilityQueries),
                    group.Sum(static item => item.PlanBuilds),
                    group.Sum(static item => item.StructuralInspections),
                    group.Sum(static item => item.UnreachableIntervals),
                    group.Sum(static item => item.ProbeComparisons),
                    group.Sum(static item => item.ParityComparisons),
                    group.Sum(static item => item.ParityMismatches),
                    group.Sum(static item => item.RecoveryValueQueries))).ToArray(),
            metrics.SelectMany(static metric => metric.RetainedAdmissions)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerRetainedAdmissionMetric(
                    group.Key,
                    group.Sum(static item => item.AuthorityQueries),
                    group.Sum(static item => item.OriginFreeFastPathReturns),
                    group.Sum(static item => item.OriginPossibilityRequests),
                    group.Sum(static item => item.OriginPossibilityBuilds),
                    group.Sum(static item => item.SemanticQueries),
                    group.Sum(static item => item.DistinctQueryPositions),
                    group.Sum(static item => item.SourceIndexBuilds),
                    group.Sum(static item => item.LifetimeNodeInspections))).ToArray(),
            metrics.SelectMany(static metric => metric.EvaluationEnvironments)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerEvaluationEnvironmentMetric(
                    group.Key,
                    group.Sum(static item => item.FingerprintRequests),
                    group.Sum(static item => item.FingerprintBuilds),
                    group.Sum(static item => item.EquivalentKeyBuilds),
                    group.Sum(static item => item.EquivalentKeyNodes),
                    group.Sum(static item => item.InternerRequests),
                    group.Sum(static item => item.InternerContextBuilds),
                    group.Sum(static item => item.InternerIdentityBuilds),
                    group.Sum(static item => item.InternerHits),
                    group.Sum(static item => item.InternerRegistrations),
                    group.Sum(static item => item.InternerTraversalIdentityBuilds),
                    group.Sum(static item => item.StableLocalProofRequests),
                    group.Sum(static item => item.StableLocalProofBuilds),
                    group.Sum(static item => item.StableLocalProofHits),
                    group.Sum(static item => item.ExactValueProvenanceVisits),
                    group.Sum(static item => item.ValueNormalizationRequests),
                    group.Sum(static item => item.ValueNormalizationBuilds),
                    group.Sum(static item => item.ValueNormalizationHits))).ToArray(),
            metrics.SelectMany(static metric => metric.TraversalRecoveryProjections)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerTraversalRecoveryProjectionMetric(
                    group.Key,
                    group.Sum(static item => item.Requests),
                    group.Sum(static item => item.Builds),
                    group.Sum(static item => item.CacheHits),
                    group.Sum(static item => item.WholeMemberScans),
                    group.Sum(static item => item.ExpressionsInspected),
                    group.Sum(static item => item.AssignmentScans),
                    group.Sum(static item => item.AssignmentInspections),
                    group.Sum(static item => item.DependencyNodes),
                    group.Sum(static item => item.DependencyCacheHits))).ToArray(),
            metrics.SelectMany(static metric => metric.InvocationExecutions)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerInvocationExecutionMetric(
                    group.Key,
                    group.Sum(static item => item.PlanRequests),
                    group.Sum(static item => item.PlanBuilds))).ToArray(),
            metrics.SelectMany(static metric => metric.ClosedCallables)
                .GroupBy(static item => item.Symbol)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerClosedCallableMetric(
                    group.Key,
                    group.Sum(static item => item.ResolutionRequests),
                    group.Sum(static item => item.ResolutionBuilds))).ToArray(),
            rootTraversals,
            metrics.SelectMany(static metric => metric.PublicationRegions)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerPublicationRegionMetric(
                    group.Key,
                    group.Sum(static item => item.IndexRequests),
                    group.Sum(static item => item.IndexBuilds),
                    group.Sum(static item => item.CandidateInspections))).ToArray(),
            metrics.SelectMany(static metric => metric.CleanupCaches)
                .GroupBy(static item => item.Member)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new HostedProducerCleanupCacheMetric(
                    group.Key,
                    group.Sum(static item => item.ProvenanceRequests),
                    group.Sum(static item => item.ProvenanceBuilds),
                    group.Sum(static item => item.ProvenanceStableHits),
                    group.Sum(static item => item.ValueFlowRequests),
                    group.Sum(static item => item.ValueFlowBuilds),
                    group.Sum(static item => item.ValueFlowStableHits))).ToArray(),
            metrics.Sum(static metric => metric.RepeatedRegistrationSemanticQueries),
            metrics.Sum(static metric => metric.RegistrationCandidateInspections))
        {
            CleanupSources = metrics.SelectMany(static metric => metric.CleanupSources)
                .GroupBy(static source => (source.Kind, source.Compilation, source.Symbol))
                .OrderBy(static group => group.Key.Kind, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.Compilation)
                .ThenBy(static group => group.Key.Symbol, StringComparer.Ordinal)
                .Select(static group => new HostedProducerCleanupSourceMetric(
                    group.Key.Kind,
                    group.Key.Compilation,
                    group.Key.Symbol,
                    group.Sum(static source => source.Requests),
                    group.Sum(static source => source.Builds))).ToArray(),
        };
    }

    internal static int[] Run(
        int occurrenceCount,
        IReadOnlyList<int[]> families,
        int workerCount,
        Func<int, int[], int[]> executeFamily)
    {
        if (workerCount is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }

        int[] planned = families.SelectMany(static family => family).Order().ToArray();

        if (!planned.SequenceEqual(Enumerable.Range(0, occurrenceCount))
            || families.Any(static family => family.Length == 0))
        {
            throw new InvalidOperationException("The root plan must contain every occurrence exactly once.");
        }

        int[] owners = Enumerable.Repeat(-1, occurrenceCount).ToArray();

        int next = -1;

        int failed = 0;

        void Work(int worker)
        {
            try
            {
                while (Volatile.Read(ref failed) == 0)
                {
                    int index = Interlocked.Increment(ref next);

                    if (index >= families.Count)
                    {
                        break;
                    }

                    int[] family = families[index];

                    int[] completed = executeFamily(worker, family);

                    if (!family.SequenceEqual(completed))
                    {
                        throw new InvalidOperationException("A root worker returned missing, duplicate, or reordered occurrences.");
                    }

                    foreach (int occurrence in completed)
                    {
                        if (Interlocked.CompareExchange(ref owners[occurrence], worker, -1) != -1)
                        {
                            throw new InvalidOperationException("A root occurrence completed more than once.");
                        }
                    }
                }
            }
            catch
            {
                Interlocked.Exchange(ref failed, 1);

                throw;
            }
        }

        if (workerCount == 1)
        {
            Work(0);
        }
        else
        {
            Task[] workers = Enumerable.Range(0, workerCount)
                .Select(worker => (Task)StartOnAnalysisStack(() =>
                {
                    Work(worker);

                    return worker;
                }))
                .ToArray();

            try
            {
                Task.WaitAll(workers);
            }
            catch (AggregateException error)
            {
                throw new InvalidOperationException("Root workers failed; no partial discovery is available.", error.Flatten());
            }
        }

        if (owners.Any(static owner => owner < 0))
        {
            throw new InvalidOperationException("Root workers did not complete the entire plan.");
        }

        return owners;
    }
}
