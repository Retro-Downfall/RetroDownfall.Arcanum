using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Performance;

/// <summary>
/// The pinned benchmark workload, read as the release gate reads it.
/// </summary>
/// <remarks>
/// The benchmark host is outside the solution and outside <c>dotnet test</c>, so nothing in the
/// ordinary suite compiles against this file. That is exactly why it is asserted here: a ceiling
/// reverted to null, an operation the host cannot run, or a corpus digest cleared during a rebase
/// would all leave a gate that runs, reports, and gates nothing — and the run would still be green.
/// </remarks>
public sealed class CovenantBenchmarkManifestTests
{

    /// <summary>The operations the host knows how to run, which the manifest may not exceed.</summary>
    /// <remarks>
    /// Restated here rather than reflected out of the host. The host is a separate, unreferenced
    /// assembly; a test that could see its switch statement would be a test of the same file.
    /// </remarks>
    private static readonly string[] RunnableOperations =
    [
        "turn.plan",
        "turn.admission",
        "mutation.prepare",
        "mutation.commit",
        "status.census",
    ];

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

    [Fact]
    public void The_manifest_names_only_operations_the_host_can_run()
    {

        foreach (JsonElement operation in Manifest().GetProperty("operations").EnumerateArray())
        {

            string id = operation.GetProperty("id").GetString()!;

            // The host throws on an operation it does not recognize, which fails the run rather than
            // skipping it. Catching the mismatch here says which name is wrong instead.
            Assert.Contains(id, RunnableOperations);

        }

    }

    [Fact]
    public void The_corpus_is_pinned_to_a_digest()
    {

        JsonElement corpus = Manifest().GetProperty("corpus");

        // Every recorded baseline was produced against one corpus. Without the digest a template edit
        // would change what is measured while the comparison kept reporting the difference as a code
        // regression.
        Assert.Equal(
            64,
            corpus.GetProperty("corpusDigest").GetString()?.Length);

        Assert.True(corpus.GetProperty("globalConfirmedEntries").GetInt32() > 0);

        Assert.True(corpus.GetProperty("campaigns").GetInt32() > 0);

    }

    [Fact]
    public void The_comparison_rule_in_the_manifest_matches_the_one_the_code_applies()
    {

        JsonElement comparison = Manifest().GetProperty("comparison");

        // The manifest restates the rule so a reader can see it without reading the code. A restatement
        // that drifted would be worse than none: it would describe a gate nobody is running.
        Assert.Equal(1.10, comparison.GetProperty("observedRatioThreshold").GetDouble(), 3);

        Assert.Equal(1.05, comparison.GetProperty("intervalLowerBoundThreshold").GetDouble(), 3);

        Assert.Equal(
            Core.Performance.BenchmarkComparison.Replicates,
            comparison.GetProperty("bootstrapReplicates").GetInt32());

        Assert.Equal(
            $"0x{Core.Performance.BenchmarkComparison.Seed:X16}",
            comparison.GetProperty("pairedBootstrapSeed").GetString());

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
