using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryRecursiveScaleProbe
{
    [SkippableFact]
    public Task Complete_recursive_weaver_at_ten_thousand() => RunOptInAsync(10000);

    [SkippableFact]
    public Task Complete_recursive_weaver_at_fifty_thousand() => RunOptInAsync(50000);

    [Fact]
    public async Task Small_real_recursive_consumer_publishes_verified_lineage_and_a_truthful_terminal_receipt()
    {
        using StringWriter receipt = new(CultureInfo.InvariantCulture);

        await RunRecursiveAsync(256, new(30, 120, 30, 180, 4L * 1024 * 1024 * 1024), receipt);

        string[] lines = receipt.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines, line => line.Contains("event=terminal", StringComparison.Ordinal));

        Assert.Contains(lines, line => line.Contains("event=terminal", StringComparison.Ordinal)
            && line.Contains("outcome=accepted", StringComparison.Ordinal));

        Assert.Contains(lines, line => line.Contains("neighbor_umap_phase=UNOBSERVED scalar=UNOBSERVED", StringComparison.Ordinal));

        string terminal = Assert.Single(lines, line => line.Contains("event=terminal", StringComparison.Ordinal));

        string attempts = Assert.Single(terminal.Split(' '), field => field.StartsWith("bic_attempted=", StringComparison.Ordinal));

        Assert.True(long.Parse(attempts.Split('=')[1], CultureInfo.InvariantCulture) > 0);

        Assert.Contains("bic_measurement=observed", terminal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9999)]
    [InlineData(50001)]
    public void Unknown_scale_size_cannot_select_an_unbounded_consumer_limit(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsumerLimits.ForScale(count));

    private async Task RunOptInAsync(int count)
    {
        Skip.IfNot(System.Environment.GetEnvironmentVariable("ARCANUM_RAPTOR_RECURSIVE_SCALE_PROBE") == "1",
            "Explicit offline recursive scale qualification only.");

        string? directory = System.Environment.GetEnvironmentVariable("ARCANUM_RAPTOR_SCALE_RECEIPT_DIRECTORY");

        Assert.False(string.IsNullOrWhiteSpace(directory), "Set the explicit test-only scale receipt directory.");

        _ = Directory.CreateDirectory(directory!);

        using StreamWriter receipt = new(new FileStream(Path.Combine(directory!, $"recursive-{count}.progress.log"),
            FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false))
        { AutoFlush = true };

        await RunRecursiveAsync(count, ConsumerLimits.ForScale(count), receipt);
    }

    private Task RunRecursiveAsync(int count, ConsumerLimits limits, TextWriter receipt) =>
        RunRecursiveAsync(count, limits, receipt, static (seconds, ceiling, caller) => new ConsumerBudget(seconds, ceiling, caller));

    private async Task RunRecursiveAsync(int count, ConsumerLimits limits, TextWriter receipt,
        Func<int, long, CancellationToken, ConsumerBudget> budgetFactory)
    {
        using ConsumerBudget whole = budgetFactory(limits.TotalSeconds, limits.MemoryCeiling, CancellationToken.None);

        using RecursiveJournal journal = new(this, whole, receipt);

        journal.Record(FormattableString.Invariant($"event=identity n={count} dimensions=768 scope_hash={TapestryHash.OfContent(Scope.Id)} manifest={RaptorAlgorithmManifest.Current.Fingerprint()} default_clusterer_seeds=identity-derived leaf_generator_state=177 summary_embedding_recipe=sha256-block24-byte-minus127-div128-v1 summary_text_recipe=ordered-child-digest-v1 max_summary_layers=5 max_children=24 max_summary_tokens=512 paid_calls=0 memory_ceiling={limits.MemoryCeiling} prior_50k_graph_ceiling=4294967296"));

        try
        {
            await PhaseAsync("seed", limits.SeedSeconds, async token =>
            {
                await SeedAsync(count, token);

                string corpus = RaptorScaleProbe.CorpusHash(_embedding.Leaves);

                if (count == 10000) Assert.Equal("1666be3372195f72acc9e450f84082b34e4b94f4335255d488fe1e6838a03123", corpus);

                else if (count == 50000) Assert.Equal("e9a8589f0e8a63d3c96daf663a6eddce62d1b97488582bcb7ed2be5a1b3e6ee0", corpus);

                journal.Record($"event=corpus hash={corpus}");
            });

            TapestryWeaveOutcome? outcome = null;

            await PhaseAsync("build", limits.BuildSeconds, async token =>
            {
                outcome = await BuildAsync(token);

                Assert.Equal(TapestryWeaveStatus.Woven, outcome.Status);

                Assert.True(outcome.LayerCount >= 3, "Recursive acceptance needs at least two summary layers above the leaves.");

                Assert.True(_clusterer.MaxLayer >= 2, "A higher summary layer must invoke the actual default numerical clusterer.");
            });

            await PhaseAsync("validate-and-unchanged", limits.ValidationSeconds, async token =>
            {
                await ValidatePublishedAsync(outcome!, count, token);

                _clusterer.AssertFitMeasurementsComplete();

                foreach (string sample in _clusterer.FitSamples) journal.Record(sample);

                TapestryGeneration before = Assert.IsType<TapestryGeneration>(await _store.GetCurrentGenerationAsync(Scope, token));

                IReadOnlyList<string> roots = await _store.GetRootIdsAsync(before.GenerationId, token);

                Effects effects = CaptureEffects();

                TapestryWeaveOutcome unchanged = await BuildAsync(token);

                Assert.Equal(TapestryWeaveStatus.UpToDate, unchanged.Status);

                ValidateNoNewEffects(effects);

                Assert.Equal(before, await _store.GetCurrentGenerationAsync(Scope, token));

                Assert.Equal(roots, await _store.GetRootIdsAsync(before.GenerationId, token));

                journal.Record(FormattableString.Invariant($"event=verified generation_hash={TapestryHash.OfContent(before.GenerationId)} layers={outcome!.LayerCount} nodes={outcome.NodeCount} roots={outcome.RootNodeCount} terminal={outcome.TerminalReason} source_leaves={outcome.LeavesEmbedded} summaries={outcome.SummaryCallsMade} reused={outcome.SummariesReused} quarantined={outcome.QuarantinedVectors} batch_calls={_embedding.BatchCalls} batch_inputs={_embedding.BatchInputs} single_embeddings={_embedding.SingleCalls} summary_calls={_summary.Calls} unchanged=UpToDate unchanged_effects=0"));
            });

            whole.Token.ThrowIfCancellationRequested();

            journal.Finish("accepted", "none");
        }
        catch (OperationCanceledException)
        {
            journal.Finish("incomplete", whole.Reason == "none" ? journal.PhaseReason : whole.Reason);

            throw;
        }
        catch
        {
            journal.Finish("failed", "acceptance-or-runtime");

            throw;
        }

        async Task PhaseAsync(string name, int seconds, Func<CancellationToken, Task> work)
        {
            using ConsumerBudget phase = budgetFactory(seconds, limits.MemoryCeiling, whole.Token);

            journal.Stage(name);

            Stopwatch watch = Stopwatch.StartNew();

            long allocated = GC.GetTotalAllocatedBytes(false);

            bool completed = false;

            try
            {
                await work(phase.Token);

                phase.Token.ThrowIfCancellationRequested();

                completed = true;
            }
            finally
            {
                journal.PhaseReason = phase.Reason;

                journal.Record(FormattableString.Invariant($"event=phase-end phase={name} completed={completed} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={GC.GetTotalAllocatedBytes(false) - allocated} peak_working_set={phase.PeakWorkingSet} reason={phase.Reason} cancel_latency_ms={phase.CancelLatencyMilliseconds:F3}"));
            }
        }
    }

    private sealed record ConsumerLimits(int SeedSeconds, int BuildSeconds, int ValidationSeconds, int TotalSeconds, long MemoryCeiling)
    {
        internal static ConsumerLimits ForScale(int count) => count switch
        {
            10000 => new(120, 1800, 180, 2100, 4L * 1024 * 1024 * 1024),

            50000 => new(300, 7200, 300, 7800, 6L * 1024 * 1024 * 1024),

            _ => throw new ArgumentOutOfRangeException(nameof(count)),
        };
    }

    private sealed class RecursiveJournal : IDisposable
    {
        private readonly TapestryRecursiveScaleProbe _owner;

        private readonly ConsumerBudget _budget;

        private readonly TextWriter _receipt;

        private readonly object _gate = new();

        private readonly Stopwatch _watch = Stopwatch.StartNew();

        private readonly long _allocated = GC.GetTotalAllocatedBytes(false);

        private readonly Timer _heartbeat;

        private string _phase = "start";

        private bool _terminal;

        internal RecursiveJournal(TapestryRecursiveScaleProbe owner, ConsumerBudget budget, TextWriter receipt)
        {
            _owner = owner;

            _budget = budget;

            _receipt = receipt;

            _heartbeat = new Timer(_ =>
            {
                lock (_gate) if (!_terminal) Write("heartbeat", "running", "none");
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        internal string PhaseReason { get; set; } = "none";

        internal void Stage(string phase)
        {
            lock (_gate)
            {
                _phase = phase;

                Write("phase", "running", "none");
            }
        }

        internal void Record(string line)
        {
            lock (_gate)
            {
                _receipt.WriteLine(line);

                _receipt.Flush();
            }
        }

        internal void Finish(string outcome, string reason)
        {
            lock (_gate)
            {
                if (_terminal) return;

                if (outcome == "accepted") _budget.Token.ThrowIfCancellationRequested();

                _terminal = true;

                Write("terminal", outcome, reason);
            }
        }

        private void Write(string kind, string outcome, string reason)
        {
            string line = FormattableString.Invariant($"event={kind} phase={_phase} outcome={outcome} reason={reason} elapsed_ms={_watch.Elapsed.TotalMilliseconds:F3} start_working_set={_budget.StartWorkingSet} peak_working_set={_budget.PeakWorkingSet} budget_callback_failure={_budget.CallbackFailed} allocated_bytes={GC.GetTotalAllocatedBytes(false) - _allocated} {_owner._clusterer.Describe()}");

            _receipt.WriteLine(line);

            _receipt.Flush();

            if (kind == "terminal") _owner.WriteTerminal(line);
        }

        public void Dispose() => _heartbeat.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void WriteTerminal(string line) => output.WriteLine(line);
}
