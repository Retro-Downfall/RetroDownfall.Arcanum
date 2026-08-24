using System.Globalization;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Core.Performance;

/// <summary>
/// The pinned workload every Covenant benchmark number is produced against.
/// </summary>
/// <remarks>
/// The model and the arithmetic over it live here rather than in the benchmark host because the host
/// is outside the solution and outside <c>dotnet test</c>. A ceiling comparison, a corpus expansion,
/// or a digest computed there would be arithmetic no lane ever runs against an assertion, and the
/// gate would keep reporting whatever it was last edited to report.
/// </remarks>
internal sealed record WorkloadManifest(
    int SchemaVersion,
    string WorkloadId,
    WorkloadCorpus Corpus,
    WorkloadMeasurement Measurement,
    WorkloadOperation[] Operations,
    WorkloadComparison Comparison);

internal sealed record WorkloadCorpus(
    int GlobalConfirmedEntries,
    int CampaignConfirmedEntriesPerCampaign,
    int CampaignProposedEntriesPerCampaign,
    int Campaigns,
    string KeyTemplate,
    string ContentTemplate,
    string[] FillerWords,
    int FillerWordsPerEntry,
    string? CorpusDigest);

internal sealed record WorkloadMeasurement(
    int WarmupIterations,
    int Batches,
    int IterationsPerBatch,
    int AllocationControlIterations);

/// <summary>
/// One measured operation and the absolute ceilings it may not cross.
/// </summary>
/// <remarks>
/// A null ceiling is an unset ceiling, and the gate refuses a run rather than treating it as
/// unlimited. Absent and infinite look identical to a reader of the file and opposite to a reader of
/// the results, and the one that ships silently is the wrong one.
/// </remarks>
internal sealed record WorkloadOperation(
    string Id,
    double? P95CeilingMicroseconds,
    double? P99CeilingMicroseconds,
    long? AllocationCeilingBytes);

internal sealed record WorkloadComparison(
    double ObservedRatioThreshold,
    double IntervalLowerBoundThreshold,
    int BootstrapReplicates,
    string PairedBootstrapSeed);

/// <summary>One seeded corpus entry, expanded from the manifest's templates.</summary>
internal sealed record BenchmarkCorpusEntry(int CampaignOrdinal, string ScopeLabel, int Ordinal, string Key, string Content);

/// <summary>
/// Expands the manifest's corpus description into the exact entries a run seeds, and digests them.
/// </summary>
/// <remarks>
/// One implementation, called by the bed that seeds and by the test that checks the pinned digest. A
/// digest recomputed by a second copy of this expansion would agree with the manifest and disagree
/// with whatever the bed actually wrote, which is the failure the digest exists to catch.
/// </remarks>
internal static class BenchmarkCorpus
{

    /// <summary>
    /// The corpus in seeding order: every Global entry, then every Campaign's entries by ordinal.
    /// </summary>
    /// <remarks>
    /// The order is the contract. Filler words are drawn from one PCG32 stream shared across the whole
    /// corpus, so moving an entry changes the content of every entry after it — which is exactly why
    /// the digest is taken over the expansion rather than over the template text.
    /// </remarks>
    internal static IEnumerable<BenchmarkCorpusEntry> Entries(WorkloadCorpus corpus)
    {

        ArgumentNullException.ThrowIfNull(corpus);

        Pcg32 random = new(BenchmarkComparison.Seed);

        for (int ordinal = 0; ordinal < corpus.GlobalConfirmedEntries; ordinal++)
        {

            yield return Entry(corpus, random, campaignOrdinal: -1, "global", ordinal);

        }

        for (int campaign = 0; campaign < corpus.Campaigns; campaign++)
        {

            for (int ordinal = 0; ordinal < corpus.CampaignConfirmedEntriesPerCampaign; ordinal++)
            {

                yield return Entry(corpus, random, campaign, $"campaign{campaign}", ordinal);

            }

        }

    }

    /// <summary>The digest of the expanded corpus, over each entry's key and authored content in order.</summary>
    internal static string Digest(WorkloadCorpus corpus)
    {

        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (BenchmarkCorpusEntry entry in Entries(corpus))
        {

            digest.AppendData(Encoding.UTF8.GetBytes(entry.Key));

            digest.AppendData(Encoding.UTF8.GetBytes(entry.Content));

        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());

    }

    private static BenchmarkCorpusEntry Entry(
        WorkloadCorpus corpus,
        Pcg32 random,
        int campaignOrdinal,
        string scopeLabel,
        int ordinal)
    {

        string key = Expand(corpus.KeyTemplate, scopeLabel, ordinal, filler: null);

        string content = Expand(corpus.ContentTemplate, scopeLabel, ordinal, Filler(corpus, random));

        return new BenchmarkCorpusEntry(campaignOrdinal, scopeLabel, ordinal, key, content);

    }

    private static string Filler(WorkloadCorpus corpus, Pcg32 random)
    {

        StringBuilder builder = new();

        for (int word = 0; word < corpus.FillerWordsPerEntry; word++)
        {

            if (word > 0)
            {

                _ = builder.Append(' ');

            }

            _ = builder.Append(corpus.FillerWords[(int)random.NextBelow((uint)corpus.FillerWords.Length)]);

        }

        return builder.ToString();

    }

    private static string Expand(string template, string scope, int ordinal, string? filler) =>
        template
            .Replace("{scope}", scope, StringComparison.Ordinal)
            .Replace("{ordinal:00}", ordinal.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{filler}", filler ?? string.Empty, StringComparison.Ordinal);

}

/// <summary>
/// The digest of everything about a workload that changes what a number means but not what is seeded.
/// </summary>
/// <remarks>
/// The corpus digest is taken at seed time, so it pins the bytes and nothing else. Two runs measured
/// under different batch counts, a different operation order, or different ceilings would carry the
/// same corpus digest and compare silently — and the manifest's own order comment says a reorder
/// changes what the operations after <c>mutation.commit</c> measure. This is the second half of the
/// fingerprint, and a comparison refuses on it exactly as it refuses on the corpus.
/// </remarks>
internal static class BenchmarkManifestDigest
{

    internal static string Of(WorkloadManifest manifest)
    {

        ArgumentNullException.ThrowIfNull(manifest);

        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(digest, manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));

        Append(digest, manifest.WorkloadId);

        Append(digest, manifest.Measurement.WarmupIterations.ToString(CultureInfo.InvariantCulture));

        Append(digest, manifest.Measurement.Batches.ToString(CultureInfo.InvariantCulture));

        Append(digest, manifest.Measurement.IterationsPerBatch.ToString(CultureInfo.InvariantCulture));

        Append(digest, manifest.Measurement.AllocationControlIterations.ToString(CultureInfo.InvariantCulture));

        foreach (WorkloadOperation operation in manifest.Operations)
        {

            Append(digest, operation.Id);

            Append(digest, Number(operation.P95CeilingMicroseconds));

            Append(digest, Number(operation.P99CeilingMicroseconds));

            Append(digest, Number(operation.AllocationCeilingBytes));

        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());

    }

    // A separator no field can contain, so that moving a character across a boundary cannot leave the
    // concatenation unchanged — two adjacent fields "ab" and "c" have to digest apart from "a" and "bc".
    /// <summary>ASCII unit separator, which no manifest field can contain.</summary>
    private static readonly byte[] Separator = [0x1F];

    private static void Append(IncrementalHash digest, string value)
    {

        digest.AppendData(Encoding.UTF8.GetBytes(value));

        digest.AppendData(Separator);

    }

    private static string Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";

}
