using System.Globalization;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal sealed partial class LongRestQualificationRun
{
    private const int AttemptStateSettledLow = 4;

    private readonly List<string> _findings = [];

    private readonly HashSet<string> _gradedMemories = new(StringComparer.Ordinal);

    /// <summary>
    /// Product-level findings the run recorded and kept going past, so that one run reports every scenario's result; the test fails with
    /// all of them after the last scenario.
    /// </summary>
    public IReadOnlyList<string> Findings => _findings;

    /// <summary>What the host's metrics endpoint exposes, by series name only: how many series, and which Arcanum families carry Long Rest or budget names.</summary>
    private static string DescribeMetrics(IReadOnlyDictionary<string, double> metrics)
    {
        string[] names = [.. metrics.Keys.Select(static key => key.Contains('{', StringComparison.Ordinal) ? key[..key.IndexOf('{', StringComparison.Ordinal)] : key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        string[] relevant = [.. names.Where(static name => name.Contains("long_rest", StringComparison.Ordinal) || name.Contains("budget", StringComparison.Ordinal) || name.Contains("operation", StringComparison.Ordinal))];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{metrics.Count} series in {names.Length} families; Long Rest, budget or operation families: {(relevant.Length == 0 ? "none" : string.Join(", ", relevant))}");
    }

    /// <summary>Dumps a few rows of a diagnostic table into the observations, so a surprising figure can be traced to its rows.</summary>
    private async Task ObserveRowsAsync(string title, string select, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string[]> rows = await Grimoire.DumpAsync(select, cancellationToken);

            Observe($"{title}: {(rows.Count == 0 ? "no rows" : string.Join(" | ", rows.Select(static row => string.Join(", ", row))))}");
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Observe($"{title}: unreadable ({exception.GetType().Name})");
        }
    }

    /// <summary>A product-level expectation: when it does not hold, the run records it and goes on, so the later scenarios still report.</summary>
    private void Check(bool holds, string finding)
    {
        if (!holds)
        {
            RecordFinding(finding);
        }
    }

    private void RecordFinding(string finding)
    {
        _findings.Add(finding);

        _log.Info("FINDING " + finding);
    }

    /// <summary>
    /// Grades every consolidation the sweeps applied against the corpus's labels. A memory belongs under a survivor when both say the same
    /// thing (one cluster, or the same text); a memory folded into a survivor of another topic is suppressed from recall for good, so a
    /// sweep that does that has destroyed data. The decision is the model's, over a pack discovery assembled, so this is where a weak
    /// model, a loose gate or an all-or-nothing group decision shows.
    /// </summary>
    private void GradeConsolidations(string scenario, IReadOnlyList<LongRestApiMemory> memories)
    {
        Dictionary<string, LongRestSeededMemory> seeded = _corpus.ToDictionary(static memory => NormalizeIdentity(memory.Id), StringComparer.Ordinal);

        List<string> wrong = [];

        int right = 0;

        foreach (LongRestApiMemory memory in memories.Where(static memory => memory.ConsolidatedIntoMemoryId is not null))
        {
            if (!_gradedMemories.Add(memory.Id)
                || !seeded.TryGetValue(NormalizeIdentity(memory.Id), out LongRestSeededMemory? source)
                || !seeded.TryGetValue(NormalizeIdentity(memory.ConsolidatedIntoMemoryId!), out LongRestSeededMemory? survivor)
                || source.Label == LongRestQualificationCorpus.UnmatchedLabel
                || survivor.Label == LongRestQualificationCorpus.UnmatchedLabel)
            {
                continue;
            }

            bool sameThing = string.Equals(source.Content, survivor.Content, StringComparison.Ordinal)
                || (source.Label == survivor.Label && source.Label != LongRestQualificationCorpus.SingleLabel);

            if (sameThing)
            {
                right++;
            }
            else
            {
                wrong.Add($"\"{source.Content}\" ({source.Label}) into \"{survivor.Content}\" ({survivor.Label})");
            }
        }

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"{scenario} consolidation quality: {right} memories folded into a survivor that says the same thing, {wrong.Count} folded into a survivor about something else"));

        if (wrong.Count > 0)
        {
            RecordFinding(string.Create(
                CultureInfo.InvariantCulture,
                $"{scenario}: Long Rest folded {wrong.Count} memories into survivors of a different topic ({right} correctly), which suppresses them from recall. For example: {string.Join("; ", wrong.Take(5))}."));
        }
    }

    /// <summary>The calibration, once the clean sweep has completed: printed whatever happens afterwards.</summary>
    public string? CalibrationText { get; private set; }

    public string? CalibrationJson { get; private set; }

    /// <summary>
    /// The text of a memory the operator excluded must appear in no request the provider received: neither a chat prompt nor an
    /// embedding input. The check reads the decoded text the provider was shown, so JSON escaping cannot hide a hit.
    /// </summary>
    private async Task AssertNeverSentAsync(IReadOnlyList<LongRestRecordedRequest> traffic, LongRestCorpusRole role, CancellationToken cancellationToken)
    {
        LongRestSeededMemory memory = _corpus.Single(candidate => candidate.Role == role);

        // The disclosure journal agrees: the memory is no input of any unit and no leaf of any generation.
        long journaled = await Grimoire.CountJournaledMemoriesAsync([memory.Id], cancellationToken);

        Check(journaled == 0, $"The {role} memory appears {journaled} time(s) in the disclosure journal; a sweep must leave it out of its corpus.");

        foreach (LongRestRecordedRequest request in traffic)
        {
            Check(
                !request.ProviderVisibleText.Contains(memory.Content, StringComparison.OrdinalIgnoreCase),
                $"The {role} memory's text reached the provider in request #{request.Sequence} ({request.Kind}); it must never be sent.");
        }

        _log.Info($"The {role} memory's text appears in none of the {traffic.Count} requests the provider received");
    }

    /// <summary>The fast model serves every Long Rest chat call, the embedding model every embedding, each through its own provider entry's credential.</summary>
    private void AssertServedBy(IReadOnlyList<LongRestRecordedRequest> traffic, bool requireBothKinds)
    {
        LongRestRecordedRequest[] chat = [.. traffic.Where(static request => request.IsLongRestChat)];

        LongRestRecordedRequest[] embeddings = [.. traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Embedding)];

        // A second sweep over what the first one left may need no adjudication at all (what remains has no neighbours), so only the
        // first sweep must show both kinds; every sweep must show some Long Rest chat call.
        Assert.NotEmpty(chat);

        if (requireBothKinds)
        {
            Check(chat.Any(static request => request.Kind == LongRestProviderRequestKind.Summary), "No Long Rest summary request reached the provider.");

            Check(chat.Any(static request => request.Kind == LongRestProviderRequestKind.Adjudication), "No Long Rest adjudication request reached the provider.");
        }

        foreach (LongRestRecordedRequest request in chat)
        {
            Check(
                string.Equals(request.Model, _options.ChatModel, StringComparison.Ordinal),
                $"Long Rest chat request #{request.Sequence} was served by '{request.Model}', not the fast model '{_options.ChatModel}'.");

            Check(
                request.Credential == LongRestRequestCredential.Chat,
                $"Long Rest chat request #{request.Sequence} carried the {request.Credential} credential, not the chat entry's.");
        }

        foreach (LongRestRecordedRequest request in embeddings)
        {
            Check(
                string.Equals(request.Model, _options.EmbeddingModel, StringComparison.Ordinal),
                $"Embedding request #{request.Sequence} named '{request.Model}', not the embedding model '{_options.EmbeddingModel}'.");

            Check(
                request.Credential == LongRestRequestCredential.Embedding,
                $"Embedding request #{request.Sequence} carried the {request.Credential} credential, not the embedding entry's.");
        }

        Check(
            !traffic.Any(request => string.Equals(request.Model, _options.DefaultModel, StringComparison.Ordinal)),
            $"The default model '{_options.DefaultModel}' served a Long Rest call; the fast model must serve them all.");

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Served by: {chat.Length} Long Rest chat calls ({chat.Count(static request => request.Kind == LongRestProviderRequestKind.Summary)} summary, "
            + $"{chat.Count(static request => request.Kind == LongRestProviderRequestKind.Adjudication)} adjudication) all on '{_options.ChatModel}' with the chat entry's credential; "
            + $"{embeddings.Length} embeddings on '{_options.EmbeddingModel}' with the embedding entry's credential; the default model '{_options.DefaultModel}' served none of them"));
    }

    /// <summary>
    /// What the real models did with the requests: how many calls of each kind, how long they took, how much they wrote, and how many
    /// were cut off at the output cap. A reasoning-capable model spends its cap on thought, so this is where a malformed or truncated
    /// answer shows its cause.
    /// </summary>
    private void ObserveModelBehavior(string scenario, IReadOnlyList<LongRestRecordedRequest> traffic)
    {
        foreach (LongRestProviderRequestKind kind in new[]
        {
            LongRestProviderRequestKind.Summary,
            LongRestProviderRequestKind.Adjudication,
            LongRestProviderRequestKind.Embedding,
        })
        {
            LongRestRecordedRequest[] calls = [.. traffic.Where(request => request.Kind == kind && request.CompletedAt is not null)];

            if (calls.Length == 0)
            {
                continue;
            }

            CalibrationDistribution seconds = CalibrationDistribution.Of(
                [.. calls.Select(static request => (request.CompletedAt!.Value - request.StartedAt).TotalSeconds)]);

            CalibrationDistribution written = CalibrationDistribution.Of(
                [.. calls.Where(static request => request.CompletionTokens is not null).Select(static request => (double)request.CompletionTokens!.Value)]);

            int cut = calls.Count(static request => string.Equals(request.FinishReason, "length", StringComparison.Ordinal));

            int refused = calls.Count(static request => request.StatusCode is not (>= 200 and < 300));

            Observe(string.Create(
                CultureInfo.InvariantCulture,
                $"{scenario} model behavior, {kind}: {calls.Length} calls, {seconds.Median:0.0} s median ({seconds.P90:0.0} s p90, {seconds.Maximum:0.0} s max), "
                + $"{written.Median:0} completion tokens median ({written.P90:0} p90, {written.Maximum:0} max), {cut} cut off at the output cap, {refused} refused"));
        }
    }

    /// <summary>
    /// How well discovery grouped the corpus: each adjudication request names the memories it compares, and the corpus knows which
    /// cluster each belongs to, so the packs the sweep paid to adjudicate can be graded by whether they hold one topic or several.
    /// </summary>
    private void ObservePacks(string scenario, IReadOnlyList<LongRestRecordedRequest> traffic)
    {
        Dictionary<string, string> labels = _corpus.ToDictionary(
            static memory => NormalizeIdentity(memory.Id),
            static memory => memory.Label,
            StringComparer.Ordinal);

        List<string[]> packs = [];

        foreach (LongRestRecordedRequest request in traffic.Where(static request => request.Kind == LongRestProviderRequestKind.Adjudication))
        {
            packs.Add([.. ReadPackMemories(request.ProviderVisibleText).Select(id => labels.GetValueOrDefault(NormalizeIdentity(id), LongRestQualificationCorpus.UnmatchedLabel))]);
        }

        if (packs.Count == 0)
        {
            return;
        }

        CalibrationDistribution sizes = CalibrationDistribution.Of([.. packs.Select(static pack => (double)pack.Length)]);

        int mixed = packs.Count(static pack => pack.Where(static label => label != LongRestQualificationCorpus.SingleLabel).Distinct(StringComparer.Ordinal).Count() > 1);

        int withUnrelated = packs.Count(static pack => pack.Contains(LongRestQualificationCorpus.SingleLabel, StringComparer.Ordinal) && pack.Length > 1);

        int pure = packs.Count(static pack => pack.Distinct(StringComparer.Ordinal).Count() == 1);

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"{scenario} adjudication packs: {packs.Count} requests, {sizes.Median:0} memories median ({sizes.Maximum:0} max); {pure} hold one cluster, {mixed} hold memories of more than one cluster, {withUnrelated} hold an unrelated single beside others"));
    }

    /// <summary>
    /// Whether the models' answers were ones Long Rest accepts: an adjudication is valid when it names a kind and, only for a
    /// supersession, one of the versions the request listed; a summary is valid JSON within the recipe's byte bound. The grading
    /// is the rule the request's own prompt states, so a count of invalid answers says how often the model ignored its instructions.
    /// </summary>
    private void ObserveAnswerQuality(string scenario, IReadOnlyList<LongRestRecordedRequest> traffic)
    {
        Dictionary<string, int> adjudications = new(StringComparer.Ordinal);

        Dictionary<string, int> summaries = new(StringComparer.Ordinal);

        int bound = LongRestPromptLayout.SummaryBoundUtf8Bytes(_options.EmbeddingWindow);

        foreach (LongRestRecordedRequest request in traffic.Where(static request => request.IsLongRestChat && request.StatusCode is >= 200 and < 300 && request.Delivered))
        {
            Dictionary<string, int> tally = request.Kind == LongRestProviderRequestKind.Adjudication ? adjudications : summaries;

            string verdict = request.Kind == LongRestProviderRequestKind.Adjudication
                ? GradeAdjudication(request)
                : GradeSummary(request.ResponseContent, bound);

            tally[verdict] = tally.GetValueOrDefault(verdict) + 1;
        }

        foreach ((string kind, Dictionary<string, int> tally) in new[] { ("adjudication", adjudications), ("summary", summaries) })
        {
            if (tally.Count > 0)
            {
                Observe($"{scenario} {kind} answers: {string.Join(", ", tally.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key} x{pair.Value}"))}");
            }
        }
    }

    internal static string GradeAdjudication(LongRestRecordedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ResponseContent is not { } content)
        {
            return "invalid: no content";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);

            JsonElement root = document.RootElement;

            string? kind = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out JsonElement kindValue) ? kindValue.GetString() : null;

            string? survivor = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("survivorVersionId", out JsonElement survivorValue) && survivorValue.ValueKind == JsonValueKind.String
                ? survivorValue.GetString()
                : null;

            HashSet<string> versions = new(
                request.ProviderVisibleText.Split('\n').Where(static line => line.StartsWith("Version ", StringComparison.Ordinal)).Select(static line => line[8..].Trim()),
                StringComparer.OrdinalIgnoreCase);

            return kind switch
            {
                "NoChange" or "EquivalentObservations" when survivor is null => "valid " + kind,
                "NoChange" or "EquivalentObservations" => "invalid: " + kind + " names a survivor",
                "Supersession" when survivor is not null && versions.Contains(survivor) => "valid Supersession",
                "Supersession" => "invalid: Supersession without a listed survivor",
                _ => "invalid: unknown kind",
            };
        }
        catch (JsonException)
        {
            return request.FinishReason == "length" ? "invalid: cut off at the output cap" : "invalid: not JSON";
        }
    }

    internal static string GradeSummary(string? content, int boundBytes)
    {
        if (content is null)
        {
            return "invalid: no content";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("summary", out JsonElement summary)
                || summary.ValueKind != JsonValueKind.String)
            {
                return "invalid: no summary";
            }

            return System.Text.Encoding.UTF8.GetByteCount(summary.GetString() ?? string.Empty) <= boundBytes ? "valid" : "valid but over the byte bound";
        }
        catch (JsonException)
        {
            return "invalid: not JSON";
        }
    }

    internal static IEnumerable<string> ReadPackMemories(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("Memory ", StringComparison.Ordinal) && Guid.TryParse(line.AsSpan(7), out Guid identity))
            {
                yield return identity.ToString("D");
            }
        }
    }

    /// <summary>No paid unit of a finished sweep is left mid-attempt, and a clean one needed no failed attempt.</summary>
    private void AssertEffectsSettled(IReadOnlyList<LongRestEffectRow> effects)
    {
        Assert.NotEmpty(effects);

        foreach (LongRestEffectRow row in effects)
        {
            Check(
                row.StateCode >= AttemptStateSettledLow,
                $"Work item {Short(row.WorkItemId)} ended the sweep unsettled in attempt state {row.StateCode}.");
        }

        IEnumerable<string> states = effects
            .GroupBy(static row => row.StateCode)
            .OrderBy(static group => group.Key)
            .Select(static group => $"state {group.Key} x{group.Count()}");

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"Provider-effects journal: {effects.Count} work items ({string.Join(", ", states)}); sent {effects.Sum(static row => row.SentCount)}, paid {effects.Sum(static row => row.PaidCount)}, "
            + $"invalid {effects.Sum(static row => row.InvalidCount)}, failed {effects.Sum(static row => row.FailureCount)}; "
            + $"{effects.Sum(static row => row.InputTokens)} input and {effects.Sum(static row => row.OutputTokens)} output tokens"));
    }

    /// <summary>
    /// The host's accounting must equal what the provider actually reported: every paid call the journal counts is a response that reached
    /// the host, once, and the tokens and the budget's spend are those responses' tokens. A call that went out and never came back (the
    /// killed attempt) is counted by neither side as paid.
    /// </summary>
    private void AssertAccounting(
        string scenario,
        IReadOnlyList<LongRestEffectRow> effects,
        IReadOnlyList<LongRestRecordedRequest> traffic,
        decimal spendDelta,
        LongRestRecordedRequest? killedRequest)
    {
        LongRestRecordedRequest[] delivered =
        [
            .. traffic.Where(static request =>
                request.Kind is LongRestProviderRequestKind.Summary or LongRestProviderRequestKind.Adjudication or LongRestProviderRequestKind.Embedding
                && request.Delivered
                && request.StatusCode is >= 200 and < 300),
        ];

        if (killedRequest is not null)
        {
            Assert.DoesNotContain(delivered, request => request.Sequence == killedRequest.Sequence);
        }

        long paidCalls = effects.Sum(static row => row.PaidCount);

        long promptTokens = delivered.Sum(static request => request.PromptTokens ?? 0);

        long completionTokens = delivered.Sum(static request => request.CompletionTokens ?? 0);

        long cachedTokens = delivered.Sum(static request => request.CachedPromptTokens ?? 0);

        long journalInput = effects.Sum(static row => row.InputTokens);

        long journalOutput = effects.Sum(static row => row.OutputTokens);

        long journalCached = effects.Sum(static row => row.CachedInputTokens);

        // The host bills the prompt tokens the provider served from its cache at the cached rate and the rest at the input rate.
        decimal expectedSpend = (((promptTokens - cachedTokens) * LongRestQualificationConfiguration.InputPricePerMillion)
            + (cachedTokens * LongRestQualificationConfiguration.CachedPricePerMillion)
            + (completionTokens * LongRestQualificationConfiguration.OutputPricePerMillion)) / 1_000_000m;

        Observe(string.Create(
            CultureInfo.InvariantCulture,
            $"{scenario} accounting: provider delivered {delivered.Length} paid responses ({promptTokens} prompt of which {cachedTokens} cached, {completionTokens} completion tokens); "
            + $"journal counts {paidCalls} paid calls ({journalInput} input of which {journalCached} cached, {journalOutput} output tokens); budget spend {spendDelta:0.000000} USD against {expectedSpend:0.000000} USD from the provider's figures"));

        Check(
            paidCalls == delivered.Length,
            $"{scenario}: the journal counts {paidCalls} paid calls but the provider delivered {delivered.Length} paid responses; a paid send was counted twice or not at all.");

        Check(
            journalInput == promptTokens,
            $"{scenario}: the journal records {journalInput} input tokens but the provider reported {promptTokens}.");

        Check(
            journalCached == cachedTokens,
            $"{scenario}: the journal records {journalCached} cached input tokens but the provider reported {cachedTokens}.");

        Check(
            journalOutput == completionTokens,
            $"{scenario}: the journal records {journalOutput} output tokens but the provider reported {completionTokens}.");

        decimal tolerance = Math.Max(0.0005m, expectedSpend * 0.02m);

        Check(
            Math.Abs(spendDelta - expectedSpend) <= tolerance,
            $"{scenario}: the budget's spend moved by {spendDelta:0.000000} USD but the provider's reported usage prices at {expectedSpend:0.000000} USD.");
    }

    /// <summary>
    /// Reads the clean sweep's completed generation, which the next completed sweep deletes, and calibrates the discovery thresholds
    /// against the labelled corpus with the real vectors. The report is written before anything later in the run can fail.
    /// </summary>
    private async Task CaptureCalibrationAsync(
        string generationId,
        IReadOnlyList<LongRestApiMemory> memories,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<LongRestNodeRow> nodes, IReadOnlyList<(string Parent, string Child)> edges) =
            await Grimoire.ReadHierarchyAsync(generationId, cancellationToken);

        IReadOnlyDictionary<string, float[]> vectors = await Grimoire.ReadMemoryVectorsAsync(cancellationToken);

        HashSet<string> leafMemories = new(
            nodes.Where(static node => node.Layer == 0 && node.MemoryId is not null).Select(static node => NormalizeIdentity(node.MemoryId!)),
            StringComparer.Ordinal);

        // The corpus the generation read: the labelled memories that were leaves of it (the retired and the pinned were not captured).
        List<CalibrationMemory> corpus = [];

        foreach (LongRestSeededMemory seeded in _corpus)
        {
            if (seeded.Fact is null
                || seeded.Role != LongRestCorpusRole.Ordinary
                || !leafMemories.Contains(NormalizeIdentity(seeded.Id))
                || !vectors.TryGetValue(seeded.Id, out float[]? vector))
            {
                continue;
            }

            corpus.Add(new CalibrationMemory(seeded.Id, seeded.Label, memories.Single(memory => memory.Id == seeded.Id).Content, vector));
        }

        Dictionary<string, string?> leafByNode = nodes
            .Where(static node => node.Layer == 0)
            .ToDictionary(static node => node.NodeId, static node => node.MemoryId, StringComparer.OrdinalIgnoreCase);

        List<CalibrationSummary> summaries = [];

        foreach (LongRestNodeRow node in nodes.Where(static node => node.Layer > 0 && node.Vector is not null && node.Summary is not null))
        {
            summaries.Add(new CalibrationSummary(
                node.NodeId,
                node.Layer,
                node.Summary!,
                node.Vector!,
                [.. edges.Where(edge => string.Equals(edge.Parent, node.NodeId, StringComparison.OrdinalIgnoreCase)).Select(static edge => edge.Child)]));
        }

        CalibrationReport report = LongRestQualificationCalibration.Calibrate(
            corpus,
            summaries,
            leafByNode,
            LongRestPromptLayout.SummaryBoundUtf8Bytes(_options.EmbeddingWindow));

        CalibrationText = LongRestQualificationCalibration.RenderText(report, _options);

        CalibrationJson = LongRestQualificationCalibration.RenderJson(report);

        await WriteResultsAsync("long-rest-calibration.txt", CalibrationText);

        await WriteResultsAsync("long-rest-calibration.json", CalibrationJson);

        foreach (string line in CalibrationText.Split('\n'))
        {
            _log.Info("CALIBRATION " + line.TrimEnd('\r'));
        }

        // Loosely: the report must have measured something, and the real models must at least rank true pairs above the others.
        Check(report.Lexical.Within.Count > 0 && report.Lexical.Cross.Count > 0, "The calibration found no lexical pairs to compare.");

        Check(report.Vector.Within.Count > 0 && report.Vector.Cross.Count > 0, "The calibration found no vector pairs to compare.");

        Check(
            report.Vector.Within.Count == 0 || report.Vector.Cross.Count == 0 || report.Vector.Within.Median > report.Vector.Cross.Median,
            $"The embedding model does not rank true pairs (median {report.Vector.Within.Median:0.000}) above the others (median {report.Vector.Cross.Median:0.000}).");

        Check(
            report.Lexical.Within.Count == 0 || report.Lexical.Cross.Count == 0 || report.Lexical.Within.Median > report.Lexical.Cross.Median,
            $"The lexical score does not rank true pairs (median {report.Lexical.Within.Median:0}) above the others (median {report.Lexical.Cross.Median:0}).");
    }

    private async Task WriteResultsAsync(string name, string contents)
    {
        if (_options.ResultsDirectory is not { } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, name), contents);
    }

    private static string Short(string identity) => identity.Length > 8 ? identity[..8] : identity;

    private static string NormalizeIdentity(string identity) =>
        identity.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
