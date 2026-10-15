using System.Diagnostics;
using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>Which faults an operator retry may answer while a scenario waits for a sweep to finish.</summary>
internal enum LongRestRetryScope
{
    /// <summary>Any park that only an operator clears: the sweep is run to its end whatever the real model did.</summary>
    AnyFault = 0,

    /// <summary>
    /// Only the model's own invalid output. A crash must be recovered by the worker alone, so any other fault that needs an operator is
    /// a finding even though the run answers it to carry on.
    /// </summary>
    ModelFaultsOnly = 1,
}

/// <summary>
/// The judgements the Long Rest qualification makes about what it observed, as pure functions of that evidence so that each one is
/// pinned by a deterministic test without a host, a model or a clock.
/// </summary>
internal static class LongRestQualificationChecks
{
    /// <summary>
    /// The finding for answering a needs-operator park with an operator retry, or <see langword="null"/> when the scope permits the answer.
    /// The run still retries (it must go on to report the rest), but a recovery that needed one has not been shown to need none.
    /// </summary>
    public static string? OperatorRetryFinding(LongRestRetryScope scope, string what, string? code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        if (scope == LongRestRetryScope.AnyFault || string.Equals(code, ErrorCodes.LongRest.InvalidOutput, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{what}: the sweep was parked as '{code ?? "(no code)"}', a fault only an operator clears, in a run where the worker must recover the crash without an operator. "
            + $"The run answered it with an operator retry to go on, which proves nothing about automatic recovery. Only the model's own invalid output ({ErrorCodes.LongRest.InvalidOutput}) may be answered that way here.";
    }

    /// <summary>
    /// The finding for the crashed sweep's first park when its code is not one the worker resumes by itself, or <see langword="null"/> when
    /// there is no park in the observations (a stale lease is parked and readmitted in one pass, which a poll can miss) or the first one is resumable.
    /// </summary>
    public static string? CrashParkFinding(IReadOnlyList<LongRestOperationSnapshot> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        LongRestOperationSnapshot? first = observed.FirstOrDefault(static snapshot => snapshot.State == LongRestOperationState.ReconciliationRequired);

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
