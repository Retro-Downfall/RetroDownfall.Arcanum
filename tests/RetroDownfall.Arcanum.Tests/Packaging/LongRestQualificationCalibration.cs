using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>One memory of the labelled corpus as the calibration sees it.</summary>
internal sealed record CalibrationMemory(string Id, string Label, string Text, float[] Vector);

/// <summary>One summary node of the completed generation: its text, vector and the nodes directly under it.</summary>
internal sealed record CalibrationSummary(string NodeId, int Layer, string Text, float[] Vector, IReadOnlyList<string> ChildNodeIds);

/// <summary>Distribution of one set of scores.</summary>
internal sealed record CalibrationDistribution(int Count, double Minimum, double P10, double Median, double P90, double Maximum)
{
    public static CalibrationDistribution Of(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new CalibrationDistribution(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        double[] sorted = [.. values.Order()];

        return new CalibrationDistribution(
            sorted.Length,
            sorted[0],
            Percentile(sorted, 0.10),
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            sorted[^1]);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        double position = fraction * (sorted.Length - 1);

        int lower = (int)Math.Floor(position);

        int upper = (int)Math.Ceiling(position);

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}

/// <summary>What one candidate threshold keeps of the true pairs and lets through of the others.</summary>
internal sealed record CalibrationThresholdRow(double Threshold, double WithinRecall, double CrossRate, int WithinKept, int CrossKept);

/// <summary>One lane's calibration: both distributions, the threshold table and the thresholds the numbers support.</summary>
internal sealed record CalibrationLane(
    string Name,
    double ConfiguredThreshold,
    CalibrationDistribution Within,
    CalibrationDistribution Cross,
    IReadOnlyList<CalibrationThresholdRow> Table,
    CalibrationThresholdRow AtConfigured,
    CalibrationThresholdRow BestSeparating,
    CalibrationThresholdRow? HighestAtNinetyPercentRecall);

/// <summary>Cosines of the hierarchy's summaries to what they cover and to what they do not.</summary>
internal sealed record CalibrationSummaryGate(
    double ConfiguredGate,
    int SummaryCount,
    CalibrationDistribution OwnChildren,
    CalibrationDistribution OwnLeaves,
    CalibrationDistribution ForeignLeaves,
    CalibrationDistribution ForeignSummaries,
    int OwnLeavesAtOrAboveGate,
    int ForeignLeavesAtOrAboveGate,
    int ForeignSummariesAtOrAboveGate,
    IReadOnlyList<string> Composition,
    IReadOnlyList<int> SummaryBytes);

internal sealed record CalibrationReport(
    int CorpusMemories,
    int LabelledClusters,
    CalibrationLane Lexical,
    CalibrationLane Vector,
    CalibrationSummaryGate? SummaryGate);

/// <summary>
/// Calibrates Long Rest's discovery thresholds against the labelled corpus, using the product's own scoring function and the
/// real vectors the embedding model produced. It only measures: the thresholds it prints are recommendations, and nothing in
/// the product reads them.
/// </summary>
internal static class LongRestQualificationCalibration
{
    private static readonly double[] LexicalThresholds = [4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 144, 160, 176, 192, 256, 384];

    private static readonly double[] CosineThresholds = [0.50, 0.60, 0.70, 0.75, 0.80, 0.85, 0.88, 0.90, 0.92, 0.94, 0.96, 0.98];

    public static CalibrationReport Calibrate(
        IReadOnlyList<CalibrationMemory> corpus,
        IReadOnlyList<CalibrationSummary> summaries,
        IReadOnlyDictionary<string, string?> summaryLeafMemory,
        int summaryBoundBytes)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(summaryLeafMemory);

        CalibrationLane lexical = LexicalLane(corpus);

        CalibrationLane vector = VectorLane(corpus);

        CalibrationSummaryGate? gate = summaries.Count == 0
            ? null
            : SummaryGate(corpus, summaries, summaryLeafMemory, summaryBoundBytes);

        return new CalibrationReport(
            corpus.Count,
            corpus.Select(static memory => memory.Label).Where(static label => label != LongRestQualificationCorpus.SingleLabel).Distinct(StringComparer.Ordinal).Count(),
            lexical,
            vector,
            gate);
    }

    /// <summary>Whether two corpus memories say the same thing: they share a cluster label, and the label is not the unrelated one.</summary>
    internal static bool SameCluster(CalibrationMemory first, CalibrationMemory second) =>
        first.Label != LongRestQualificationCorpus.SingleLabel
        && string.Equals(first.Label, second.Label, StringComparison.Ordinal);

    /// <summary>
    /// The lexical lane's scores for every ordered pair, exactly as discovery computes them for one anchor: the anchor's query
    /// terms, the document frequencies over the whole corpus (the generation's leaves), the product's integer weights and score.
    /// </summary>
    internal static CalibrationLane LexicalLane(IReadOnlyList<CalibrationMemory> corpus)
    {
        List<double> within = [];

        List<double> cross = [];

        foreach (CalibrationMemory anchor in corpus)
        {
            ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms(anchor.Text);

            if (terms.Length == 0)
            {
                continue;
            }

            LongRestLexicalDocument[] profiles =
            [
                .. corpus.Select(memory => LongRestLexicalScorer.Profile(memory.Text, terms)),
            ];

            int[] frequencies = new int[terms.Length];

            foreach (LongRestLexicalDocument profile in profiles)
            {
                for (int term = 0; term < terms.Length; term++)
                {
                    if (profile.Frequencies[term] > 0)
                    {
                        frequencies[term]++;
                    }
                }
            }

            ImmutableArray<int> weights = LongRestLexicalScorer.Weights(corpus.Count, [.. frequencies]);

            for (int index = 0; index < corpus.Count; index++)
            {
                CalibrationMemory other = corpus[index];

                if (ReferenceEquals(other, anchor))
                {
                    continue;
                }

                double score = LongRestLexicalScorer.Score(weights, profiles[index]);

                (SameCluster(anchor, other) ? within : cross).Add(score);
            }
        }

        return Lane("Lexical (integer score, sixteenths of a bit)", LongRestDiscoveryLimits.LexicalMinimumScore, within, cross, LexicalThresholds);
    }

    /// <summary>Cosines of every unordered pair of corpus vectors: the vector lane compares a leaf with each other leaf.</summary>
    internal static CalibrationLane VectorLane(IReadOnlyList<CalibrationMemory> corpus)
    {
        List<double> within = [];

        List<double> cross = [];

        for (int first = 0; first < corpus.Count; first++)
        {
            for (int second = first + 1; second < corpus.Count; second++)
            {
                double cosine = Cosine(corpus[first].Vector, corpus[second].Vector);

                (SameCluster(corpus[first], corpus[second]) ? within : cross).Add(cosine);
            }
        }

        return Lane("Vector (leaf to leaf cosine)", LongRestDiscoveryLimits.MinimumVectorCosine, within, cross, CosineThresholds);
    }

    internal static CalibrationSummaryGate SummaryGate(
        IReadOnlyList<CalibrationMemory> corpus,
        IReadOnlyList<CalibrationSummary> summaries,
        IReadOnlyDictionary<string, string?> summaryLeafMemory,
        int summaryBoundBytes)
    {
        Dictionary<string, CalibrationMemory> memories = corpus.ToDictionary(static memory => memory.Id, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, CalibrationSummary> byNode = summaries.ToDictionary(static summary => summary.NodeId, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HashSet<string>> descendantLeaves = new(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, HashSet<string>> descendantSummaries = new(StringComparer.OrdinalIgnoreCase);

        foreach (CalibrationSummary summary in summaries)
        {
            HashSet<string> leaves = new(StringComparer.OrdinalIgnoreCase);

            HashSet<string> below = new(StringComparer.OrdinalIgnoreCase);

            Collect(summary, leaves, below);

            descendantLeaves[summary.NodeId] = leaves;

            descendantSummaries[summary.NodeId] = below;
        }

        List<double> ownChildren = [];

        List<double> ownLeaves = [];

        List<double> foreignLeaves = [];

        List<double> foreignSummaries = [];

        List<string> composition = [];

        foreach (CalibrationSummary summary in summaries)
        {
            foreach (string child in summary.ChildNodeIds)
            {
                if (byNode.TryGetValue(child, out CalibrationSummary? childSummary))
                {
                    ownChildren.Add(Cosine(summary.Vector, childSummary.Vector));
                }
                else if (summaryLeafMemory.TryGetValue(child, out string? memoryId) && memoryId is not null
                    && memories.TryGetValue(memoryId, out CalibrationMemory? leaf))
                {
                    ownChildren.Add(Cosine(summary.Vector, leaf.Vector));
                }
            }

            foreach (CalibrationMemory memory in corpus)
            {
                (descendantLeaves[summary.NodeId].Contains(memory.Id) ? ownLeaves : foreignLeaves)
                    .Add(Cosine(summary.Vector, memory.Vector));
            }

            foreach (CalibrationSummary other in summaries)
            {
                if (string.Equals(other.NodeId, summary.NodeId, StringComparison.OrdinalIgnoreCase)
                    || descendantSummaries[summary.NodeId].Contains(other.NodeId)
                    || descendantSummaries[other.NodeId].Contains(summary.NodeId))
                {
                    continue;
                }

                foreignSummaries.Add(Cosine(summary.Vector, other.Vector));
            }

            IEnumerable<string> labels = descendantLeaves[summary.NodeId]
                .Where(memories.ContainsKey)
                .Select(id => memories[id].Label)
                .GroupBy(static label => label, StringComparer.Ordinal)
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => $"{group.Key}x{group.Count()}");

            composition.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"layer {summary.Layer} node {Short(summary.NodeId)} covers {descendantLeaves[summary.NodeId].Count} memories: {string.Join(", ", labels)}"));
        }

        double gate = LongRestDiscoveryLimits.MinimumVectorCosine;

        return new CalibrationSummaryGate(
            gate,
            summaries.Count,
            CalibrationDistribution.Of(ownChildren),
            CalibrationDistribution.Of(ownLeaves),
            CalibrationDistribution.Of(foreignLeaves),
            CalibrationDistribution.Of(foreignSummaries),
            ownLeaves.Count(value => value >= gate),
            foreignLeaves.Count(value => value >= gate),
            foreignSummaries.Count(value => value >= gate),
            composition,
            [.. summaries.Select(summary => Encoding.UTF8.GetByteCount(summary.Text))]);

        void Collect(CalibrationSummary node, HashSet<string> leaves, HashSet<string> below)
        {
            foreach (string child in node.ChildNodeIds)
            {
                if (byNode.TryGetValue(child, out CalibrationSummary? childSummary))
                {
                    _ = below.Add(childSummary.NodeId);

                    Collect(childSummary, leaves, below);
                }
                else if (summaryLeafMemory.TryGetValue(child, out string? memoryId) && memoryId is not null)
                {
                    _ = leaves.Add(memoryId);
                }
            }
        }
    }

    public static string RenderText(CalibrationReport report, LongRestQualificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);

        StringBuilder text = new();

        _ = text.AppendLine("LONG REST CALIBRATION REPORT")
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Corpus: {report.CorpusMemories} eligible labelled memories in {report.LabelledClusters} clusters (plus exact duplicates and unrelated singles); the retired and the pinned memory are outside the sweep and outside these figures."))
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Models: chat {options.ChatModel} (FastModel), embeddings {options.EmbeddingModel} with a {options.EmbeddingWindow}-token window."))
            .AppendLine();

        AppendLane(text, report.Lexical, "{0:0}");

        AppendLane(text, report.Vector, "{0:0.000}");

        if (report.SummaryGate is { } gate)
        {
            AppendSummaryGate(text, gate);
        }
        else
        {
            _ = text.AppendLine("Summary gate: no summary nodes were available to measure.");
        }

        return text.ToString();
    }

    public static string RenderJson(CalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteNumber("corpusMemories", report.CorpusMemories);

            writer.WriteNumber("labelledClusters", report.LabelledClusters);

            WriteLane(writer, "lexical", report.Lexical);

            WriteLane(writer, "vector", report.Vector);

            if (report.SummaryGate is { } gate)
            {
                writer.WriteStartObject("summaryGate");

                writer.WriteNumber("gate", gate.ConfiguredGate);

                writer.WriteNumber("summaries", gate.SummaryCount);

                WriteDistribution(writer, "ownChildren", gate.OwnChildren);

                WriteDistribution(writer, "ownLeaves", gate.OwnLeaves);

                WriteDistribution(writer, "foreignLeaves", gate.ForeignLeaves);

                WriteDistribution(writer, "foreignSummaries", gate.ForeignSummaries);

                writer.WriteNumber("ownLeavesAtOrAboveGate", gate.OwnLeavesAtOrAboveGate);

                writer.WriteNumber("foreignLeavesAtOrAboveGate", gate.ForeignLeavesAtOrAboveGate);

                writer.WriteNumber("foreignSummariesAtOrAboveGate", gate.ForeignSummariesAtOrAboveGate);

                writer.WriteStartArray("composition");

                foreach (string line in gate.Composition)
                {
                    writer.WriteStringValue(line);
                }

                writer.WriteEndArray();

                writer.WriteStartArray("summaryBytes");

                foreach (int bytes in gate.SummaryBytes)
                {
                    writer.WriteNumberValue(bytes);
                }

                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static double Cosine(float[] first, float[] second) =>
        EmbeddingBlobCodec.CosineSimilarity(first, second);

    private static CalibrationLane Lane(
        string name,
        double configured,
        IReadOnlyList<double> within,
        IReadOnlyList<double> cross,
        double[] thresholds)
    {
        List<CalibrationThresholdRow> table =
        [
            .. thresholds.Select(threshold => Row(threshold, within, cross)),
        ];

        CalibrationThresholdRow atConfigured = Row(configured, within, cross);

        CalibrationThresholdRow best = table
            .Append(atConfigured)
            .OrderByDescending(static row => row.WithinRecall - row.CrossRate)
            .ThenByDescending(static row => row.Threshold)
            .First();

        CalibrationThresholdRow? highestAtNinety = table
            .Append(atConfigured)
            .Where(static row => row.WithinRecall >= 0.9)
            .OrderByDescending(static row => row.Threshold)
            .Cast<CalibrationThresholdRow?>()
            .FirstOrDefault();

        return new CalibrationLane(
            name,
            configured,
            CalibrationDistribution.Of(within),
            CalibrationDistribution.Of(cross),
            table,
            atConfigured,
            best,
            highestAtNinety);
    }

    private static CalibrationThresholdRow Row(double threshold, IReadOnlyList<double> within, IReadOnlyList<double> cross)
    {
        int withinKept = within.Count(value => value >= threshold);

        int crossKept = cross.Count(value => value >= threshold);

        return new CalibrationThresholdRow(
            threshold,
            within.Count == 0 ? double.NaN : (double)withinKept / within.Count,
            cross.Count == 0 ? double.NaN : (double)crossKept / cross.Count,
            withinKept,
            crossKept);
    }

    private static void AppendLane(StringBuilder text, CalibrationLane lane, string valueFormat)
    {
        _ = text.AppendLine(lane.Name)
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  within-cluster  n={lane.Within.Count}  {Describe(lane.Within, valueFormat)}"))
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  cross-cluster   n={lane.Cross.Count}  {Describe(lane.Cross, valueFormat)}"))
            .AppendLine("  threshold  within-kept  cross-kept");

        foreach (CalibrationThresholdRow row in lane.Table.Append(lane.AtConfigured).OrderBy(static row => row.Threshold).DistinctBy(static row => row.Threshold))
        {
            string marker = row.Threshold == lane.ConfiguredThreshold ? "  <- configured" : string.Empty;

            _ = text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {string.Format(CultureInfo.InvariantCulture, valueFormat, row.Threshold),9}  {row.WithinRecall,11:P1}  {row.CrossRate,10:P2}  ({row.WithinKept}/{lane.Within.Count} true, {row.CrossKept}/{lane.Cross.Count} other){marker}"));
        }

        if (lane.Within.Count > 0 && lane.Cross.Count > 0)
        {
            string lowest = string.Format(CultureInfo.InvariantCulture, valueFormat, lane.Within.Minimum);

            string highest = string.Format(CultureInfo.InvariantCulture, valueFormat, lane.Cross.Maximum);

            _ = text.AppendLine(
                lane.Within.Minimum > lane.Cross.Maximum
                    ? $"  Separation: the lowest true pair ({lowest}) is above the highest other pair ({highest}), so any threshold above {highest} up to {lowest} separates them on this corpus."
                    : $"  Separation: the lowest true pair ({lowest}) is not above the highest other pair ({highest}), so no threshold separates them on this corpus.");
        }

        _ = text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Recommendation: configured {string.Format(CultureInfo.InvariantCulture, valueFormat, lane.ConfiguredThreshold)} keeps {lane.AtConfigured.WithinRecall:P1} of true pairs and lets {lane.AtConfigured.CrossRate:P2} of other pairs through; the best separating threshold in the table is {string.Format(CultureInfo.InvariantCulture, valueFormat, lane.BestSeparating.Threshold)} ({lane.BestSeparating.WithinRecall:P1} kept, {lane.BestSeparating.CrossRate:P2} through)."));

        if (lane.HighestAtNinetyPercentRecall is { } ninety)
        {
            _ = text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"                  The highest threshold that still keeps at least 90% of true pairs is {string.Format(CultureInfo.InvariantCulture, valueFormat, ninety.Threshold)} ({ninety.CrossRate:P2} of other pairs through)."));
        }

        _ = text.AppendLine();
    }

    private static void AppendSummaryGate(StringBuilder text, CalibrationSummaryGate gate)
    {
        _ = text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Summary gate (hierarchy lane: a summary and another summary off its own chain, accepted at cosine >= {gate.ConfiguredGate:0.00})"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {gate.SummaryCount} summary nodes; summary sizes in bytes: {string.Join(", ", gate.SummaryBytes)}"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  summary to its direct children   n={gate.OwnChildren.Count}  {Describe(gate.OwnChildren, "{0:0.000}")}"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  summary to its leaves            n={gate.OwnLeaves.Count}  {Describe(gate.OwnLeaves, "{0:0.000}")}  (>= gate: {gate.OwnLeavesAtOrAboveGate})"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  summary to leaves it does not cover n={gate.ForeignLeaves.Count}  {Describe(gate.ForeignLeaves, "{0:0.000}")}  (>= gate: {gate.ForeignLeavesAtOrAboveGate})"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  summary to foreign summaries     n={gate.ForeignSummaries.Count}  {Describe(gate.ForeignSummaries, "{0:0.000}")}  (>= gate: {gate.ForeignSummariesAtOrAboveGate})"));

        foreach (string line in gate.Composition)
        {
            _ = text.AppendLine("  " + line);
        }

        double ownMedian = gate.OwnLeaves.Median;

        double foreignMax = gate.ForeignSummaries.Count == 0 ? gate.ForeignLeaves.Maximum : gate.ForeignSummaries.Maximum;

        _ = text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Recommendation: the gate of {gate.ConfiguredGate:0.00} sits {(foreignMax < gate.ConfiguredGate ? "above" : "at or below")} the highest foreign score seen ({foreignMax:0.000}); a summary covers its own leaves with a median cosine of {ownMedian:0.000}. A gate between the highest foreign score and the own-leaf median separates them on this corpus."))
            .AppendLine();
    }

    private static string Describe(CalibrationDistribution distribution, string format)
    {
        if (distribution.Count == 0)
        {
            return "no pairs";
        }

        string F(double value) => string.Format(CultureInfo.InvariantCulture, format, value);

        return $"min {F(distribution.Minimum)}  p10 {F(distribution.P10)}  median {F(distribution.Median)}  p90 {F(distribution.P90)}  max {F(distribution.Maximum)}";
    }

    private static string Short(string nodeId) => nodeId.Length > 8 ? nodeId[..8] : nodeId;

    private static void WriteLane(Utf8JsonWriter writer, string name, CalibrationLane lane)
    {
        writer.WriteStartObject(name);

        writer.WriteNumber("configuredThreshold", lane.ConfiguredThreshold);

        WriteDistribution(writer, "within", lane.Within);

        WriteDistribution(writer, "cross", lane.Cross);

        writer.WriteStartArray("table");

        foreach (CalibrationThresholdRow row in lane.Table)
        {
            WriteRow(writer, row);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("atConfigured");

        WriteRowBody(writer, lane.AtConfigured);

        writer.WritePropertyName("bestSeparating");

        WriteRowBody(writer, lane.BestSeparating);

        writer.WriteEndObject();
    }

    private static void WriteRow(Utf8JsonWriter writer, CalibrationThresholdRow row) => WriteRowBody(writer, row);

    private static void WriteRowBody(Utf8JsonWriter writer, CalibrationThresholdRow row)
    {
        writer.WriteStartObject();

        writer.WriteNumber("threshold", row.Threshold);

        WriteFinite(writer, "withinRecall", row.WithinRecall);

        WriteFinite(writer, "crossRate", row.CrossRate);

        writer.WriteNumber("withinKept", row.WithinKept);

        writer.WriteNumber("crossKept", row.CrossKept);

        writer.WriteEndObject();
    }

    private static void WriteDistribution(Utf8JsonWriter writer, string name, CalibrationDistribution distribution)
    {
        writer.WriteStartObject(name);

        writer.WriteNumber("count", distribution.Count);

        WriteFinite(writer, "min", distribution.Minimum);

        WriteFinite(writer, "p10", distribution.P10);

        WriteFinite(writer, "median", distribution.Median);

        WriteFinite(writer, "p90", distribution.P90);

        WriteFinite(writer, "max", distribution.Maximum);

        writer.WriteEndObject();
    }

    private static void WriteFinite(Utf8JsonWriter writer, string name, double value)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumber(name, value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
