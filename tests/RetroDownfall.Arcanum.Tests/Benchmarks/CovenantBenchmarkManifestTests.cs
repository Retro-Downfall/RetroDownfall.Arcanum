using System.Text.Json;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

/// <summary>
/// The pinned benchmark workload, read as the release gate reads it.
/// </summary>
/// <remarks>
/// The benchmark host is outside the solution and outside <c>dotnet test</c>, so nothing in the
/// ordinary suite compiles its <c>Program.cs</c>. That is exactly why the manifest is asserted here: a
/// ceiling reverted to null, an operation the host cannot run, or a corpus digest cleared during a
/// rebase would all leave a gate that runs, reports, and gates nothing, and the run would still be
/// green.
///
/// <para>Every assertion below is written so that a disarming edit reds it. A subset check in one
/// direction, a ceiling asserted only to be non-null, and a digest asserted only to be sixty-four
/// characters long each let the manifest be gutted without a single test moving.</para>
/// </remarks>
public sealed partial class CovenantBenchmarkManifestTests
{

    /// <summary>Matches one arm of the host's operation switch, which is the list of what it can run.</summary>
    [GeneratedRegex("\"(?<id>[a-z][a-z.]*)\"\\s*=>", RegexOptions.ExplicitCapture)]
    private static partial Regex SwitchArm();

    [Fact]
    public void Every_measured_operation_states_all_three_ceilings()
    {

        foreach (JsonElement operation in Manifest().GetProperty("operations").EnumerateArray())
        {

            string id = operation.GetProperty("id").GetString()!;

            // An unset ceiling and an unlimited one read identically in the file and oppositely in the
            // results. The gate refuses a run that states none; this refuses a manifest that ships one.
            Assert.False(
                operation.GetProperty("p95CeilingMicroseconds").ValueKind is JsonValueKind.Null,
                $"{id} states no p95 ceiling, so a run that measured it would gate nothing.");

            Assert.False(
                operation.GetProperty("p99CeilingMicroseconds").ValueKind is JsonValueKind.Null,
                $"{id} states no p99 ceiling.");

            Assert.False(
                operation.GetProperty("allocationCeilingBytes").ValueKind is JsonValueKind.Null,
                $"{id} states no allocation ceiling.");

        }

    }

    [Theory]

    [InlineData("turn.plan", 6_000, 15_000, 1_600_000)]

    [InlineData("turn.admission", 150, 300, 480_000)]

    [InlineData("mutation.prepare", 3_000, 7_000, 400_000)]

    [InlineData("mutation.commit", 60_000, 120_000, 960_000)]

    [InlineData("status.census", 1_000, 2_000, 80_000)]

    public void No_ceiling_may_be_widened_past_the_point_where_it_stops_gating(
        string id,
        double p95Bound,
        double p99Bound,
        long allocationBound)
    {

        JsonElement operation = Operation(id);

        // These are bounds, not the ceilings. Ten times the values this test was written against, so
        // an ordinary tightening or a modest loosening never touches this file, and widening a ceiling
        // until the gate cannot fail reds it. A null ceiling refuses a run loudly; a ceiling of six
        // million microseconds disarms the gate in silence with no code change at all.
        Assert.True(
            operation.GetProperty("p95CeilingMicroseconds").GetDouble() <= p95Bound,
            $"{id} states a p95 ceiling above {p95Bound}us, which no longer bounds anything.");

        Assert.True(
            operation.GetProperty("p99CeilingMicroseconds").GetDouble() <= p99Bound,
            $"{id} states a p99 ceiling above {p99Bound}us.");

        Assert.True(
            operation.GetProperty("allocationCeilingBytes").GetInt64() <= allocationBound,
            $"{id} states an allocation ceiling above {allocationBound}B.");

    }

    [Fact]
    public void The_manifest_and_the_host_name_the_same_operations()
    {

        HashSet<string> declared = new(
            Manifest().GetProperty("operations").EnumerateArray()
                .Select(static operation => operation.GetProperty("id").GetString()!),
            StringComparer.Ordinal);

        HashSet<string> runnable = new(RunnableOperations(), StringComparer.Ordinal);

        // Set equality, both directions, against the host's own source rather than a restatement. A
        // subset check in the manifest-to-host direction survives deleting four of the five entries:
        // the ceilings test iterates the survivors, and the gate only walks what the manifest names,
        // so a gutted workload gates one operation and every lane stays green. A restated list would
        // drift the other way, leaving an arm the host can run that the workload never exercises.
        Assert.Equal(runnable.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));

    }

    [Fact]
    public void Every_operation_the_manifest_declares_is_bounded_by_this_file()
    {

        HashSet<string> declared = new(
            Manifest().GetProperty("operations").EnumerateArray()
                .Select(static operation => operation.GetProperty("id").GetString()!),
            StringComparer.Ordinal);

        // The ceiling bounds above are a table, and a table with a missing row bounds nothing. An
        // operation added to the workload without a row here would carry any ceiling at all.
        HashSet<string> bounded = new(
            [
                "turn.plan",
                "turn.admission",
                "mutation.prepare",
                "mutation.commit",
                "status.census",
            ],
            StringComparer.Ordinal);

        Assert.Equal(bounded.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));

    }

    [Fact]
    public void The_pinned_corpus_digest_is_the_digest_of_what_the_manifest_describes()
    {

        JsonElement corpus = Manifest().GetProperty("corpus");

        // Recomputed, not measured for length. Any sixty-four characters passed the old assertion, so
        // a template edit, a filler word, or an entry count could change what is seeded while the file
        // kept advertising a digest that matched nothing, and every recorded baseline was produced
        // against the corpus this digest names.
        Assert.Equal(
            corpus.GetProperty("corpusDigest").GetString(),
            BenchmarkCorpus.Digest(CorpusShape()));

        Assert.True(corpus.GetProperty("globalConfirmedEntries").GetInt32() > 0);

        Assert.True(corpus.GetProperty("campaigns").GetInt32() > 0);

    }

    [Fact]
    public void The_manifest_digest_separates_runs_the_corpus_digest_cannot()
    {

        WorkloadManifest pinned = PinnedManifest();

        WorkloadManifest rebatched = pinned with
        {
            Measurement = pinned.Measurement with { Batches = pinned.Measurement.Batches + 1 },
        };

        WorkloadOperation[] reordered = [.. pinned.Operations];

        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        WorkloadManifest swapped = pinned with { Operations = reordered };

        // The corpus digest is taken at seed time, and the manifest's own order comment admits that a
        // reorder changes what the operations after mutation.commit measure. All three of these seed
        // exactly the same bytes and are not comparable with each other.
        string[] digests =
        [
            BenchmarkManifestDigest.Of(pinned),
            BenchmarkManifestDigest.Of(rebatched),
            BenchmarkManifestDigest.Of(swapped),
        ];

        Assert.Equal(digests.Length, digests.Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public void The_comparison_rule_in_the_manifest_matches_the_one_the_code_applies()
    {

        JsonElement comparison = Manifest().GetProperty("comparison");

        // Against the constants the rule is written from, not against literals restated here. Asserting
        // 1.10 against 1.10 says the test agrees with itself: the threshold could move to 1.20 in the
        // code while the manifest kept advertising 1.10 to every reader and nothing went red.
        Assert.Equal(
            BenchmarkRatioInterval.ObservedRatioThreshold,
            comparison.GetProperty("observedRatioThreshold").GetDouble(),
            3);

        Assert.Equal(
            BenchmarkRatioInterval.IntervalLowerBoundThreshold,
            comparison.GetProperty("intervalLowerBoundThreshold").GetDouble(),
            3);

        Assert.Equal(
            BenchmarkComparison.Replicates,
            comparison.GetProperty("bootstrapReplicates").GetInt32());

        Assert.Equal(
            $"0x{BenchmarkComparison.Seed:X16}",
            comparison.GetProperty("pairedBootstrapSeed").GetString());

    }

    /// <summary>The operation ids the host's switch actually has arms for, read from its source.</summary>
    /// <remarks>
    /// Scanned rather than restated. The host is a separate, unreferenced assembly, so a test cannot
    /// call into it; a hand-copied list would drift in both directions unnoticed, which is what an
    /// earlier version of this file did.
    /// </remarks>
    private static IEnumerable<string> RunnableOperations()
    {

        string source = File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "tests",
                "RetroDownfall.Arcanum.Covenant.Benchmarks",
                "Program.cs"));

        const string marker = "Func<Task> Operation(string id) => id switch";

        int start = source.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, "The benchmark host no longer declares its operation switch where this test reads it.");

        int end = source.IndexOf("\n};", start, StringComparison.Ordinal);

        Assert.True(end > start, "The benchmark host's operation switch has no recognizable end.");

        return SwitchArm().Matches(source[start..end])
            .Select(static match => match.Groups["id"].Value);

    }

    private static WorkloadCorpus CorpusShape()
    {

        JsonElement corpus = Manifest().GetProperty("corpus");

        return new WorkloadCorpus(
            corpus.GetProperty("globalConfirmedEntries").GetInt32(),
            corpus.GetProperty("campaignConfirmedEntriesPerCampaign").GetInt32(),
            corpus.GetProperty("campaignProposedEntriesPerCampaign").GetInt32(),
            corpus.GetProperty("campaigns").GetInt32(),
            corpus.GetProperty("keyTemplate").GetString()!,
            corpus.GetProperty("contentTemplate").GetString()!,
            [.. corpus.GetProperty("fillerWords").EnumerateArray().Select(static word => word.GetString()!)],
            corpus.GetProperty("fillerWordsPerEntry").GetInt32(),
            corpus.GetProperty("corpusDigest").GetString());

    }

    private static WorkloadManifest PinnedManifest()
    {

        JsonElement root = Manifest();

        JsonElement measurement = root.GetProperty("measurement");

        JsonElement comparison = root.GetProperty("comparison");

        return new WorkloadManifest(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("workloadId").GetString()!,
            CorpusShape(),
            new WorkloadMeasurement(
                measurement.GetProperty("warmupIterations").GetInt32(),
                measurement.GetProperty("batches").GetInt32(),
                measurement.GetProperty("iterationsPerBatch").GetInt32(),
                measurement.GetProperty("allocationControlIterations").GetInt32()),
            [.. root.GetProperty("operations").EnumerateArray().Select(static operation => new WorkloadOperation(
                operation.GetProperty("id").GetString()!,
                operation.GetProperty("p95CeilingMicroseconds").GetDouble(),
                operation.GetProperty("p99CeilingMicroseconds").GetDouble(),
                operation.GetProperty("allocationCeilingBytes").GetInt64()))],
            new WorkloadComparison(
                comparison.GetProperty("observedRatioThreshold").GetDouble(),
                comparison.GetProperty("intervalLowerBoundThreshold").GetDouble(),
                comparison.GetProperty("bootstrapReplicates").GetInt32(),
                comparison.GetProperty("pairedBootstrapSeed").GetString()!));

    }

    private static JsonElement Operation(string id)
    {

        foreach (JsonElement operation in Manifest().GetProperty("operations").EnumerateArray())
        {

            if (string.Equals(operation.GetProperty("id").GetString(), id, StringComparison.Ordinal))
            {

                return operation;

            }

        }

        throw new InvalidOperationException($"The pinned benchmark workload no longer declares '{id}'.");

    }

    private static JsonElement Manifest()
    {

        string path = Path.Combine(
            RepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.Covenant.Benchmarks",
            "covenant-workload-v1.json");

        Assert.True(File.Exists(path), $"The pinned benchmark workload is missing from {path}.");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    }

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("The repository root could not be located from the test output.");

    }

}
