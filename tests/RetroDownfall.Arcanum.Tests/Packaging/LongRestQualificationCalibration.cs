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

/// <summary>One leaf of the completed generation: the memory and the raw vector the embedding model gave it, as the Background Floor reads it.</summary>
internal sealed record CalibrationLeaf(string Id, float[] Vector);

/// <summary>One labelled memory's Background Floor and how the pairs it is the anchor of stand against it.</summary>
internal sealed record CalibrationAnchorFloor(
    string Label,
    string Text,
    int FloorPermille,
    double LowestWithinMargin,
    double LowestCrossMargin,
    int CrossAtOrAboveFloor);

/// <summary>
/// The Pairing Gate on the labelled corpus. A pair is admitted from an anchor's side when its cosine is at least that anchor's floor, so the
/// margin of a true pair is its cosine minus the floor (it is paired when the margin is not negative), and the margin of any other pair is the
/// floor minus its cosine (it is refused when the margin is positive). Pairs are ordered: each memory is the anchor of its pairs with every other.
/// </summary>
internal sealed record CalibrationFloors(
    CalibrationDistribution Floors,
    CalibrationDistribution WithinMargin,
    CalibrationDistribution CrossMargin,
    int WithinPairs,
    int WithinAtOrAboveFloor,
    int CrossPairs,
    int CrossAtOrAboveFloor,
    int CrossPairsAtOrAboveEitherFloor,
    IReadOnlyList<CalibrationAnchorFloor> Anchors,
    IReadOnlyList<double> WithinMargins,
    IReadOnlyList<double> CrossMargins)
{
    /// <summary>The share of true pairs the gate keeps, or NaN when there are none.</summary>
    public double WithinRetained => WithinPairs == 0 ? double.NaN : (double)WithinAtOrAboveFloor / WithinPairs;
}

internal sealed record CalibrationReport(
    int CorpusMemories,
    int LabelledClusters,
    CalibrationLane Lexical,
    CalibrationLane Vector,
    CalibrationSummaryGate? SummaryGate,
    CalibrationFloors? Floors = null);

/// <summary>
/// Calibrates Long Rest's discovery thresholds against the labelled corpus, using the product's own scoring function and the
/// real vectors the embedding model produced. It only measures: the thresholds it prints are recommendations, and nothing in
/// the product reads them.
/// </summary>
internal static class LongRestQualificationCalibration
{
    private static readonly double[] LexicalThresholds = [50, 100, 150, 200, 250, 300, 400, 500, 750];

    private static readonly double[] CosineThresholds = [0.50, 0.60, 0.70, 0.75, 0.80, 0.85, 0.88, 0.90, 0.92, 0.94, 0.96, 0.98];

    /// <param name="generationLeaves">
    /// Every leaf of the generation with a vector, in memory id order, which is what the product's Background Floor is read from; the labelled
    /// corpus alone when none is given.
    /// </param>
    public static CalibrationReport Calibrate(
        IReadOnlyList<CalibrationMemory> corpus,
        IReadOnlyList<CalibrationSummary> summaries,
        IReadOnlyDictionary<string, string?> summaryLeafMemory,
        int summaryBoundBytes,
        IReadOnlyList<CalibrationLeaf>? generationLeaves = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(summaryLeafMemory);

        CalibrationFloors floors = Floors(
            corpus,
            generationLeaves ?? [.. corpus.OrderBy(static memory => memory.Id, StringComparer.Ordinal).Select(static memory => new CalibrationLeaf(memory.Id, memory.Vector))]);

        CalibrationLane lexical = LexicalLane(corpus);

        CalibrationLane vector = VectorLane(corpus, floors.Floors.Median);

        CalibrationSummaryGate? gate = summaries.Count == 0
            ? null
            : SummaryGate(corpus, summaries, summaryLeafMemory, summaryBoundBytes);

        return new CalibrationReport(
            corpus.Count,
            corpus.Select(static memory => memory.Label).Where(static label => label != LongRestQualificationCorpus.SingleLabel).Distinct(StringComparer.Ordinal).Count(),
            lexical,
            vector,
            gate,
            floors);
    }

    /// <summary>
    /// The Background Floor of every labelled memory, read by the product's own sampler from the generation's leaves in memory id order, and the
    /// margin of every pair against the floor of the memory it is judged from. Reports how much room the embedding model leaves between the
    /// true pairs and the rest: a model that does not separate topics shows cross pairs at or above a floor, and those can reach the model.
    /// </summary>
    internal static CalibrationFloors Floors(IReadOnlyList<CalibrationMemory> corpus, IReadOnlyList<CalibrationLeaf> leaves)
    {
        int[] floor = new int[corpus.Count];

        for (int index = 0; index < corpus.Count; index++)
        {
            LongRestBackgroundSampler sampler = new([.. corpus[index].Vector]);

            foreach (CalibrationLeaf leaf in leaves)
            {
                _ = sampler.Observe(string.Equals(leaf.Id, corpus[index].Id, StringComparison.OrdinalIgnoreCase), [.. leaf.Vector]);
            }

            floor[index] = sampler.FloorPermille();
        }

        List<double> withinMargins = [];

        List<double> crossMargins = [];

        int withinAtOrAbove = 0;

        int crossAtOrAbove = 0;

        int crossEither = 0;

        List<CalibrationAnchorFloor> anchors = [];

        for (int anchor = 0; anchor < corpus.Count; anchor++)
        {
            double lowestWithin = double.PositiveInfinity;

            double lowestCross = double.PositiveInfinity;

            int crossAbove = 0;

            for (int other = 0; other < corpus.Count; other++)
            {
                if (other == anchor)
                {
                    continue;
                }

                double cosine = Cosine(corpus[anchor].Vector, corpus[other].Vector);

                if (SameCluster(corpus[anchor], corpus[other]))
                {
                    double margin = cosine - (floor[anchor] / 1000.0);

                    withinMargins.Add(margin);

                    lowestWithin = Math.Min(lowestWithin, margin);

                    // A byte-identical memory is paired whatever its vector says.
                    if (margin >= 0 || string.Equals(corpus[anchor].Text, corpus[other].Text, StringComparison.Ordinal))
                    {
                        withinAtOrAbove++;
                    }
                }
                else
                {
                    double margin = (floor[anchor] / 1000.0) - cosine;

                    crossMargins.Add(margin);

                    lowestCross = Math.Min(lowestCross, margin);

                    if (margin <= 0)
                    {
                        crossAtOrAbove++;

                        crossAbove++;
                    }

                    if (other > anchor && cosine >= Math.Min(floor[anchor], floor[other]) / 1000.0)
                    {
                        crossEither++;
                    }
                }
            }

            anchors.Add(new CalibrationAnchorFloor(
                corpus[anchor].Label,
                corpus[anchor].Text,
                floor[anchor],
                double.IsInfinity(lowestWithin) ? double.NaN : lowestWithin,
                double.IsInfinity(lowestCross) ? double.NaN : lowestCross,
                crossAbove));
        }

        return new CalibrationFloors(
            CalibrationDistribution.Of([.. floor.Select(static value => value / 1000.0)]),
            CalibrationDistribution.Of(withinMargins),
            CalibrationDistribution.Of(crossMargins),
            withinMargins.Count,
            withinAtOrAbove,
            crossMargins.Count,
            crossAtOrAbove,
            crossEither,
            anchors,
            withinMargins,
            crossMargins);
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

            // What the anchor scores against itself under the same weights is the bar's unit: a hit is nominated when it scores at least
            // LexicalMinimumCoveragePermille thousandths of that, so the bar does not move with the size of the corpus.
            double self = LongRestLexicalScorer.Score(weights, LongRestLexicalScorer.Profile(anchor.Text, terms));

            if (self <= 0)
            {
                continue;
            }

            for (int index = 0; index < corpus.Count; index++)
            {
                CalibrationMemory other = corpus[index];

                if (ReferenceEquals(other, anchor))
                {
                    continue;
                }

                double coverage = LongRestLexicalScorer.Score(weights, profiles[index]) * 1000.0 / self;

                (SameCluster(anchor, other) ? within : cross).Add(coverage);
            }
        }

        return Lane("Lexical coverage (share of the anchor's own score, in thousandths; nomination only)", LongRestDiscoveryLimits.LexicalMinimumCoveragePermille, within, cross, LexicalThresholds);
    }

    /// <summary>Cosines of every unordered pair of corpus vectors: the vector lane compares a leaf with each other leaf.</summary>
    internal static CalibrationLane VectorLane(IReadOnlyList<CalibrationMemory> corpus, double medianFloor)
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

        return Lane("Vector (leaf to leaf cosine; the gate is each anchor's own floor, the marker is the median floor)", medianFloor, within, cross, CosineThresholds);
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

        double gate = (LongRestDiscoveryLimits.SummaryMinimumCosinePermille / 1000.0);

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

        if (report.Floors is { } floors)
        {
            AppendFloors(text, floors);
        }

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

            if (report.Floors is { } floors)
            {
                WriteFloors(writer, floors);
            }

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

    private static void AppendFloors(StringBuilder text, CalibrationFloors floors)
    {
        _ = text.AppendLine("Pairing Gate (each memory's Background Floor, read by the product's own sampler from every leaf of the generation)")
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"  per-anchor floors   n={floors.Floors.Count}  {Describe(floors.Floors, "{0:0.000}")}"))
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  margin of a true pair above its anchor's floor   n={floors.WithinMargin.Count}  {Describe(floors.WithinMargin, "{0:0.000}")}"))
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  margin of another pair below its anchor's floor   n={floors.CrossMargin.Count}  {Describe(floors.CrossMargin, "{0:0.000}")}"))
            .AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  true pairs paired: {floors.WithinAtOrAboveFloor} of {floors.WithinPairs} ({floors.WithinRetained:P1}); other pairs at or above the floor of the anchor they are judged from: {floors.CrossAtOrAboveFloor} of {floors.CrossPairs} ({floors.CrossPairsAtOrAboveEitherFloor} unordered pairs at or above the lower floor of the two)"));

        foreach (CalibrationAnchorFloor anchor in floors.Anchors)
        {
            _ = text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  [{anchor.Label}] floor {anchor.FloorPermille / 1000.0:0.000}  lowest true-pair margin {anchor.LowestWithinMargin:0.000}  lowest other-pair margin {anchor.LowestCrossMargin:0.000}  other pairs at or above: {anchor.CrossAtOrAboveFloor}  \"{Truncate(anchor.Text)}\""));
        }

        double lowestCross = floors.CrossMargin.Count == 0 ? double.NaN : floors.CrossMargin.Minimum;

        _ = text.AppendLine(
            floors.CrossAtOrAboveFloor == 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"  Verdict: every other pair is below its anchor's floor (the smallest margin is {lowestCross:0.000}), so on this embedding model the gate alone keeps every cross-topic pair from the model, whatever the model answers.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"  Verdict: {floors.CrossAtOrAboveFloor} other pairs are at or above their anchor's floor, so on this embedding model they can reach the model as a pair or within a clique, and only the model's own answer and Number Agreement stand between them and a fold. This is the documented residual of an embedding model that does not separate topics."))
            .AppendLine();
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");

    private static void WriteFloors(Utf8JsonWriter writer, CalibrationFloors floors)
    {
        writer.WriteStartObject("pairingGate");

        WriteDistribution(writer, "floors", floors.Floors);

        WriteDistribution(writer, "withinMargin", floors.WithinMargin);

        WriteDistribution(writer, "crossMargin", floors.CrossMargin);

        writer.WriteNumber("withinPairs", floors.WithinPairs);

        writer.WriteNumber("withinAtOrAboveFloor", floors.WithinAtOrAboveFloor);

        WriteFinite(writer, "withinRetained", floors.WithinRetained);

        writer.WriteNumber("crossPairs", floors.CrossPairs);

        writer.WriteNumber("crossAtOrAboveFloor", floors.CrossAtOrAboveFloor);

        writer.WriteNumber("crossUnorderedAtOrAboveEitherFloor", floors.CrossPairsAtOrAboveEitherFloor);

        writer.WriteStartArray("anchors");

        foreach (CalibrationAnchorFloor anchor in floors.Anchors)
        {
            writer.WriteStartObject();

            writer.WriteString("label", anchor.Label);

            writer.WriteNumber("floorPermille", anchor.FloorPermille);

            WriteFinite(writer, "lowestWithinMargin", anchor.LowestWithinMargin);

            WriteFinite(writer, "lowestCrossMargin", anchor.LowestCrossMargin);

            writer.WriteNumber("crossAtOrAboveFloor", anchor.CrossAtOrAboveFloor);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("withinMargins");

        foreach (double margin in floors.WithinMargins)
        {
            writer.WriteNumberValue(Math.Round(margin, 4));
        }

        writer.WriteEndArray();

        writer.WriteStartArray("crossMargins");

        foreach (double margin in floors.CrossMargins)
        {
            writer.WriteNumberValue(Math.Round(margin, 4));
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
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

        bool foreignSummariesMeasured = gate.ForeignSummaries.Count > 0;

        CalibrationDistribution foreign = foreignSummariesMeasured ? gate.ForeignSummaries : gate.ForeignLeaves;

        int foreignAtOrAbove = foreignSummariesMeasured ? gate.ForeignSummariesAtOrAboveGate : gate.ForeignLeavesAtOrAboveGate;

        string foreignName = foreignSummariesMeasured ? "foreign summaries" : "uncovered leaves";

        string verdict = foreign.Count == 0
            ? "no foreign score was measured, so the gate cannot be judged on this corpus."
            : foreign.Maximum < gate.ConfiguredGate
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"the gate of {gate.ConfiguredGate:0.00} sits above the highest cosine to {foreignName} seen ({foreign.Maximum:0.000}), with {gate.ConfiguredGate - foreign.Maximum:0.000} of headroom, and admits {foreignAtOrAbove} of {foreign.Count}; a gate as low as {Math.Ceiling(foreign.Maximum * 100d) / 100d:0.00} would still admit none of them on this corpus.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"the gate of {gate.ConfiguredGate:0.00} is at or below the highest cosine to {foreignName} seen ({foreign.Maximum:0.000}) and admits {foreignAtOrAbove} of {foreign.Count}; no gate that keeps the summaries it should join can exclude them here.");

        _ = text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  Recommendation: {verdict} A summary's own leaves have a median cosine of {ownMedian:0.000} ({gate.OwnLeavesAtOrAboveGate} of {gate.OwnLeaves.Count} at or above the gate), which a gate does not apply to: it judges two summaries, not a summary and its own leaves."))
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
