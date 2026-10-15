using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>When an operator retry may answer a park while a scenario waits for a sweep to finish.</summary>
internal enum LongRestRetryScope
{
    /// <summary>Any park that only an operator clears: the sweep is run to its end whatever the real model did.</summary>
    AnyFault = 0,

    /// <summary>
    /// Only once the worker has readmitted the crashed sweep. A crash must be recovered by the worker alone, so an operator retry before
    /// that is a finding even though the run gives it to carry on; a park after it belongs to the sweep running again.
    /// </summary>
    AfterWorkerReadmission = 1,
}

/// <summary>
/// The judgements the Long Rest qualification makes about what it observed, as pure functions of that evidence so that each one is
/// pinned by a deterministic test without a host, a model or a clock.
/// </summary>
internal static class LongRestQualificationChecks
{
    /// <summary>
    /// The finding for answering a needs-operator park with an operator retry before the worker had readmitted the crashed sweep, or
    /// <see langword="null"/> when the scope permits the answer. The run still retries (it must go on to report the rest), but a
    /// recovery that needed an operator has not been shown to need none. After the readmission a park is the sweep running again, and
    /// in a real-model run that is the model's own invalid answer, whichever code the product gives it.
    /// </summary>
    public static string? OperatorRetryFinding(LongRestRetryScope scope, string what, string? code, bool workerHasReadmitted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        if (scope == LongRestRetryScope.AnyFault || workerHasReadmitted)
        {
            return null;
        }

        return $"{what}: the sweep was parked as '{code ?? "(no code)"}', a fault only an operator clears, before the worker had readmitted it, in a run where the worker must recover the crash without an operator. "
            + "The run answered it with an operator retry to go on, which proves nothing about automatic recovery.";
    }

    /// <summary>
    /// The finding for the crashed sweep's first park when its code is not one the worker resumes by itself, or <see langword="null"/> when
    /// there is no such park in the observations (a stale lease is parked and readmitted in one pass, which a poll can miss) or it is
    /// resumable. Only a park seen before the worker's readmission (running two or more attempts beyond the dead owner's) is the crash's:
    /// a later one is the sweep running again.
    /// </summary>
    public static string? CrashParkFinding(IReadOnlyList<LongRestOperationSnapshot> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        if (observed.Count == 0)
        {
            return null;
        }

        int deadOwnersAttempt = observed[0].AttemptCount;

        LongRestOperationSnapshot? first = observed
            .TakeWhile(snapshot => !(snapshot.State == LongRestOperationState.Running && snapshot.AttemptCount >= deadOwnersAttempt + 2))
            .FirstOrDefault(static snapshot => snapshot.State == LongRestOperationState.ReconciliationRequired);

        if (first is null)
        {
            return null;
        }

        LongRestFaultClass fault = LongRestFaultPolicy.Classify(first.TerminalErrorCode);

        if (fault == LongRestFaultClass.ResumeLater)
        {
            return null;
        }

        string meaning = fault == LongRestFaultClass.NeedsOperator
            ? "a fault only an operator clears"
            : "a fault that retires the generation instead of resuming it";

        return $"Scenario B: the crashed sweep's first park was '{first.TerminalErrorCode ?? "(no code)"}', {meaning}, not a resumable code the worker readmits by itself "
            + $"(a killed owner's stale lease is '{LongRestParkCodes.Interrupted}').";
    }

    /// <summary>
    /// True when the sweep was seen running two or more attempts beyond the dead owner's (the takeover that parks it, and the readmission)
    /// before the run first answered a park as an operator. That readmission is the worker's. An operator's readmission also stamps a park
    /// and counts an attempt, so those alone prove nothing.
    /// </summary>
    public static bool ReadmittedBeforeAnyOperator(
        IReadOnlyList<(TimeSpan At, LongRestOperationSnapshot Snapshot)> timeline,
        IReadOnlyList<TimeSpan> operatorRetries)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        ArgumentNullException.ThrowIfNull(operatorRetries);

        if (timeline.Count == 0)
        {
            return false;
        }

        int deadOwnersAttempt = timeline[0].Snapshot.AttemptCount;

        TimeSpan? firstOperator = operatorRetries.Count == 0 ? null : operatorRetries.Min();

        return timeline.Any(entry => entry.Snapshot.State == LongRestOperationState.Running
            && entry.Snapshot.AttemptCount >= deadOwnersAttempt + 2
            && (firstOperator is null || entry.At < firstOperator));
    }

    /// <summary>An adjudication kind by the product's own enum name, so a renumbering cannot be misread.</summary>
    public static string DescribeAdjudicationKind(int code) =>
        code is >= 0 and <= byte.MaxValue && Enum.IsDefined((LongRestAdjudicationKind)(byte)code)
            ? ((LongRestAdjudicationKind)(byte)code).ToString()
            : string.Create(CultureInfo.InvariantCulture, $"unknown kind {code}");

    /// <summary>The summaries of a generation that exceed the Embedding-Bounded Summary limit, with their size in UTF-8 bytes.</summary>
    public static IReadOnlyList<(string NodeId, int Bytes)> OversizedSummaries(IReadOnlyList<LongRestNodeRow> nodes, int boundBytes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        return
        [
            .. nodes
                .Where(static node => node.Layer > 0 && node.Summary is not null)
                .Select(static node => (node.NodeId, Bytes: Encoding.UTF8.GetByteCount(node.Summary!)))
                .Where(node => node.Bytes > boundBytes),
        ];
    }

    /// <summary>
    /// The finding for an unhealthy report, or for the Long Rest component being missing or not healthy while Long Rest is on. A component
    /// that is merely degraded elsewhere is a warning of its own and is not this run's concern. Only the Long Rest component's detail is
    /// quoted: it is documented to carry configuration keys and model names, never an endpoint or a credential.
    /// </summary>
    public static string? HealthFinding(string scenario, LongRestApiHealth health)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        ArgumentNullException.ThrowIfNull(health);

        List<string> problems = [];

        if (string.Equals(health.Status, "Unhealthy", StringComparison.Ordinal))
        {
            string components = string.Join(
                ", ",
                health.Components
                    .Where(static component => !string.Equals(component.Status, "Healthy", StringComparison.Ordinal))
                    .Select(static component => $"{component.Name}={component.Status}"));

            problems.Add($"the report is Unhealthy ({(components.Length == 0 ? "no component named" : components)})");
        }

        LongRestApiHealthComponent? longRest = health.Components.FirstOrDefault(static component => component.Name == "LongRest");

        if (longRest is null)
        {
            problems.Add("the report has no LongRest component although Long Rest is on");
        }
        else if (!string.Equals(longRest.Status, "Healthy", StringComparison.Ordinal))
        {
            problems.Add($"the LongRest component is {longRest.Status}: {longRest.Detail ?? "no detail"}");
        }

        // A sweep that has settled (completed, or cancelled by the operator, or recovered after a crash) leaves nothing awaiting repair, so the
        // durable-operations row is Healthy again. Degraded here would be the reconciliation pass's last count outliving the work it counted.
        LongRestApiHealthComponent? durable = health.Components.FirstOrDefault(static component => component.Name == "DurableOperations");

        if (durable is not null && !string.Equals(durable.Status, "Healthy", StringComparison.Ordinal))
        {
            problems.Add($"the DurableOperations component is {durable.Status} with every sweep settled: {durable.Detail ?? "no detail"}");
        }

        return problems.Count == 0 ? null : $"{scenario}: GET /api/health: {string.Join("; ", problems)}.";
    }

    /// <summary>
    /// The finding for a first scrape that carries none of the HTTP request series the host had already recorded, which is what a metrics
    /// listener that only starts at the first scrape produces. A host that listens from startup has them by then.
    /// </summary>
    public static string? ExporterStartFinding(string description, IReadOnlyDictionary<string, double> firstScrape)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        ArgumentNullException.ThrowIfNull(firstScrape);

        bool recorded = firstScrape.Any(static pair => pair.Value > 0
            && (pair.Key.StartsWith("arcanum_http_requests_total", StringComparison.Ordinal)
                || pair.Key.StartsWith("http_server_request_duration", StringComparison.Ordinal)));

        return recorded
            ? null
            : $"Metrics ({description}): the first scrape carried no HTTP request series although the host had answered authenticated requests before it, "
                + "so its exporter only starts listening at the first scrape and loses everything measured earlier, Long Rest counters included "
                + "(known product defect: the exporter should start with the host).";
    }

    /// <summary>
    /// The provider answers that completed (and were billed) but never reached the host: the withheld answers of a host killed mid-send.
    /// The host cannot know their usage, so it must count them as unknown, never as paid.
    /// </summary>
    public static LongRestRecordedRequest[] BilledButUndelivered(IReadOnlyList<LongRestRecordedRequest> traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);

        return
        [
            .. traffic.Where(static request =>
                request.Kind is LongRestProviderRequestKind.Summary or LongRestProviderRequestKind.Adjudication or LongRestProviderRequestKind.Embedding
                && request.UpstreamCompleted
                && !request.Delivered
                && request.StatusCode is >= 200 and < 300),
        ];
    }

    /// <summary>
    /// What is wrong with a receipt fetched through the API for a consolidated memory: not the receipt that memory names, not naming that
    /// memory among its targets, a survivor that is not the one the memory was folded into, or any text of the corpus. A problem never quotes memory text.
    /// </summary>
    public static IReadOnlyList<string> ReceiptProblems(LongRestApiReceipt receipt, LongRestApiMemory memory, IEnumerable<string> corpusContents)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        ArgumentNullException.ThrowIfNull(memory);

        ArgumentNullException.ThrowIfNull(corpusContents);

        List<string> problems = [];

        if (!string.Equals(receipt.ReceiptId, memory.LongRestReceiptId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the receipt fetched for memory {Short(memory.Id)} is {Short(receipt.ReceiptId)}, not the {Short(memory.LongRestReceiptId ?? string.Empty)} the memory names");
        }

        if (!receipt.TargetMemoryIds.Any(target => string.Equals(NormalizeIdentity(target), NormalizeIdentity(memory.Id), StringComparison.Ordinal)))
        {
            problems.Add($"receipt {Short(receipt.ReceiptId)} does not name memory {Short(memory.Id)} among its targets");
        }

        if (memory.ConsolidatedIntoMemoryId is { } survivor
            && !string.Equals(NormalizeIdentity(receipt.SurvivorMemoryId ?? string.Empty), NormalizeIdentity(survivor), StringComparison.Ordinal))
        {
            problems.Add($"receipt {Short(receipt.ReceiptId)} names survivor {Short(receipt.SurvivorMemoryId ?? "none")}, not {Short(survivor)} the memory was folded into");
        }

        if (corpusContents.Any(content => content.Length >= 12 && receipt.RawJson.Contains(content, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"receipt {Short(receipt.ReceiptId)} is not content-free: its body carries the text of a memory");
        }

        return problems;
    }

    /// <summary>
    /// The finding for an embedding model whose own vector size is not the configured dimensions. The product's Saga writes ask the
    /// provider for its default size and refuse a vector of any other length, so a model of another native size writes no memory at all.
    /// </summary>
    public static string? EmbeddingDimensionsFinding(string model, int configured, int measured) =>
        configured == measured
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The embedding model '{model}' returns {measured}-dimension vectors but {configured} are configured, and the host refuses a vector of any other length, so it would write no memory. Pass --embedding-dimensions {measured}.");

    /// <summary>The provider requests that failed, by kind and status (or aborted or unanswered), for a failure that needs to say what the provider did.</summary>
    public static string DescribeFailedRequests(IReadOnlyList<LongRestRecordedRequest> traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);

        string[] failures =
        [
            .. traffic
                .Select(static request => (request.Kind, Outcome: request.ClientAborted
                    ? "aborted"
                    : request.StatusCode is null
                        ? "unanswered"
                        : request.StatusCode is >= 200 and < 300
                            ? null
                            : request.StatusCode.Value.ToString(CultureInfo.InvariantCulture)))
                .Where(static failure => failure.Outcome is not null)
                .GroupBy(static failure => failure)
                .OrderBy(static group => group.Key.Kind)
                .ThenBy(static group => group.Key.Outcome, StringComparer.Ordinal)
                .Select(static group => $"{group.Key.Kind} {group.Key.Outcome} x{group.Count()}"),
        ];

        return failures.Length == 0 ? "none" : string.Join("; ", failures);
    }

    /// <summary>An exception message bounded for a finding: the first line, at most 200 characters, so a response body can never be echoed whole.</summary>
    internal static string NoValues(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string firstLine = message.Split('\n')[0].TrimEnd('\r');

        return firstLine.Length <= 200 ? firstLine : firstLine[..200];
    }

    internal static string NormalizeIdentity(string identity) =>
        identity.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string Short(string identity) => identity.Length > 8 ? identity[..8] : identity;
}

/// <summary>
/// The wait for the moment the host is killed under a Long Rest request. It ends when the forwarder's hook fires, and it fails as soon as
/// the sweep reaches a terminal state without a chat request ever arriving (every decision may be reused, or the corpus empty), or when no
/// request comes within a bound, naming the operation and the traffic so far instead of waiting out the whole sweep budget.
/// </summary>
internal static class LongRestQualificationKillPoint
{
    public static async Task<LongRestRecordedRequest> AwaitAsync(
        Task<LongRestRecordedRequest> fired,
        Func<CancellationToken, Task<LongRestOperationSnapshot>> readOperation,
        Func<string> describeTraffic,
        TimeSpan pollInterval,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fired);

        ArgumentNullException.ThrowIfNull(readOperation);

        ArgumentNullException.ThrowIfNull(describeTraffic);

        Stopwatch clock = Stopwatch.StartNew();

        while (true)
        {
            Task delay = Task.Delay(pollInterval, cancellationToken);

            if (await Task.WhenAny(fired, delay) == fired)
            {
                return await fired;
            }

            cancellationToken.ThrowIfCancellationRequested();

            LongRestOperationSnapshot snapshot;

            try
            {
                snapshot = await readOperation(cancellationToken);
            }
            catch (HttpRequestException) when (fired.IsCompleted)
            {
                // The host died under the very request the hook saw: the poll that raced its death is of no interest.
                return await fired;
            }

            if (fired.IsCompleted)
            {
                return await fired;
            }

            if (snapshot.IsTerminal)
            {
                throw new InvalidOperationException(
                    $"Scenario B: the sweep reached {snapshot} before any Long Rest chat request reached the provider, so there was no request to kill the host under. "
                    + $"Traffic so far: {describeTraffic()}.");
            }

            if (clock.Elapsed >= bound)
            {
                throw new TimeoutException(
                    $"Scenario B: no Long Rest chat request reached the provider within {bound}; the operation is {snapshot}. Traffic so far: {describeTraffic()}.");
            }
        }
    }
}

/// <summary>The questions the qualification puts to the embedding model before it creates anything.</summary>
internal static class LongRestQualificationPreflight
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Asks <paramref name="model"/> for one vector the way the host's own Saga writes do, with no size requested, and returns its length.
    /// Only the loopback-only handler is used: no redirect, no ambient proxy.
    /// </summary>
    public static async Task<int> MeasureEmbeddingDimensionsAsync(Uri endpoint, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        using SocketsHttpHandler handler = LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler();

        using HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        using MemoryStream body = new();

        using (Utf8JsonWriter writer = new(body))
        {
            writer.WriteStartObject();

            writer.WriteString("model", model);

            writer.WriteString("input", "dimension probe");

            writer.WriteEndObject();
        }

        using ByteArrayContent request = new(body.ToArray());

        request.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(new Uri(endpoint, "embeddings"), request, timeout.Token);

        byte[]? responseBytes = await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(response.Content, MaximumResponseBytes, timeout.Token);

        if (!response.IsSuccessStatusCode || responseBytes is null)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Ollama /v1/embeddings returned {(int)response.StatusCode} for the embedding model '{model}'."));
        }

        using JsonDocument document = JsonDocument.Parse(responseBytes);

        return document.RootElement.GetProperty("data")[0].GetProperty("embedding").GetArrayLength();
    }
}
