using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>What a fault in a Long Rest sweep asks for next. Unknown faults always need an operator.</summary>
public enum LongRestFaultClass : byte
{
    /// <summary>Transient: the same sweep may be readmitted by its own authority class after the cooldown.</summary>
    ResumeLater = 1,

    /// <summary>
    /// The captured generation can never complete, so a fresh request starts a new one. A clean sweep is never parked with one of these
    /// codes: the coordinator settles it <c>Abandoned</c> and the next request retires it. A protected sweep keeps its park, because retiring
    /// it takes the live authority of a Session turn: a request that carries that authority retires it (Fresh-Authority Retire), and a
    /// request that does not is refused as <c>LongRest.SweepParked</c>. Request admission reads this class, and lists no code of its own.
    /// </summary>
    RestartGeneration = 2,

    /// <summary>Nothing automatic may continue this sweep; an operator retries or cancels it.</summary>
    NeedsOperator = 3,
}

/// <summary>The durable terminal codes the Long Rest park-and-readmit mechanism writes or reads.</summary>
public static class LongRestParkCodes
{
    /// <summary>A Running or Waiting sweep whose lease lapsed: its owner is gone and the checkpoint is intact.</summary>
    public const string Interrupted = "long_rest.interrupted";

    /// <summary>Maintenance closed admission while the sweep held clean work; it resumes after reopen.</summary>
    public const string MaintenanceDeferred = "long_rest.maintenance_deferred";

    /// <summary>The worker lost its lease or was cancelled without the host stopping.</summary>
    public const string WorkerUnavailable = "long_rest.worker_unavailable";

    /// <summary>An operator retry returned the sweep to the worker; this readmission resets the failure budget.</summary>
    public const string ReadmitRequested = "long_rest.readmit_requested";

    /// <summary>A protected sweep: only a fresh eligible Session request may revalidate and readmit it.</summary>
    public const string AuthorizedRequestRequired = "long_rest.authorized_request_required";

    /// <summary>The failure budget is spent: the park became a needs-operator park.</summary>
    public const string ReadmitExhausted = "long_rest.readmit_exhausted";

    /// <summary>An operator cancelled the sweep.</summary>
    public const string Cancelled = "long_rest.cancelled";

    /// <summary>The sweep never began, so recovery abandons it without effects.</summary>
    public const string NotStarted = "long_rest.not_started";

    /// <summary>A never-started request was retired because its configuration changed.</summary>
    public const string StaleConfiguration = "long_rest.stale_configuration";

    /// <summary>The checkpoint cannot be decoded.</summary>
    public const string CheckpointInvalid = "long_rest.checkpoint_invalid";
}

/// <summary>
/// The pure classifier that maps every Long Rest fault code to what may happen next, and the Sweep Failure Budget that
/// bounds clean readmission. The coordinator's outcome mapping, worker discovery, Atomic Readmission and the request-time
/// parked-sweep rule all read this one table (the classes, the free-park set and the budget arithmetic), so no code is
/// listed twice.
/// </summary>
public static class LongRestFaultPolicy
{
    /// <summary>Clean readmissions allowed before the park becomes a needs-operator park.</summary>
    public const int MaxReadmissions = 4;

    /// <summary>The cooldown after a park before the first readmission; it doubles per readmission.</summary>
    public static readonly TimeSpan ReadmitBaseCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The Unknown-Usage Cap: a work unit may end this many physical attempts with unknown billing before it stops sending and
    /// parks as needs-operator. It bounds repeated unknown spend until a debt gate exists; an estimate is never written as usage.
    /// </summary>
    public const int UnknownUsageCap = 2;

    /// <summary>Every code the Long Rest surface can produce, and its class. Anything absent needs an operator.</summary>
    public static IReadOnlyDictionary<string, LongRestFaultClass> KnownCodes { get; } = new Dictionary<string, LongRestFaultClass>(StringComparer.Ordinal)
    {
        [LongRestParkCodes.Interrupted] = LongRestFaultClass.ResumeLater,

        [LongRestParkCodes.MaintenanceDeferred] = LongRestFaultClass.ResumeLater,

        [LongRestParkCodes.WorkerUnavailable] = LongRestFaultClass.ResumeLater,

        [LongRestParkCodes.ReadmitRequested] = LongRestFaultClass.ResumeLater,

        [ErrorCodes.LongRest.Unavailable] = LongRestFaultClass.ResumeLater,

        [ErrorCodes.LongRest.OperationBusy] = LongRestFaultClass.ResumeLater,

        [ErrorCodes.LongRest.ProviderUnavailable] = LongRestFaultClass.ResumeLater,

        [ErrorCodes.LongRest.ProviderUnreachable] = LongRestFaultClass.ResumeLater,

        [ErrorCodes.LongRest.StaleInput] = LongRestFaultClass.RestartGeneration,

        [ErrorCodes.LongRest.StaleFence] = LongRestFaultClass.RestartGeneration,

        [ErrorCodes.LongRest.CurrentnessLost] = LongRestFaultClass.RestartGeneration,

        [ErrorCodes.LongRest.ConfigurationChanged] = LongRestFaultClass.RestartGeneration,

        [LongRestParkCodes.StaleConfiguration] = LongRestFaultClass.RestartGeneration,

        [LongRestParkCodes.NotStarted] = LongRestFaultClass.RestartGeneration,

        [ErrorCodes.LongRest.InvalidRequest] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.NotFound] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.MissingClaim] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.IntegrityFailure] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.InvalidAdmission] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.AccountingCompletionRefused] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.AccountingRefused] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.InvalidOutput] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.CancelledAfterPayment] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.SweepParked] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.ProviderRejected] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.TransportUnknown] = LongRestFaultClass.NeedsOperator,

        // An attempt's failure reason, never a park code: it is in the table so that every code the surface writes has a class.
        [ErrorCodes.LongRest.InterruptedTransport] = LongRestFaultClass.NeedsOperator,

        [ErrorCodes.LongRest.ModelContextTooSmall] = LongRestFaultClass.NeedsOperator,

        [LongRestParkCodes.AuthorizedRequestRequired] = LongRestFaultClass.NeedsOperator,

        [LongRestParkCodes.CheckpointInvalid] = LongRestFaultClass.NeedsOperator,

        [LongRestParkCodes.ReadmitExhausted] = LongRestFaultClass.NeedsOperator,

        [LongRestParkCodes.Cancelled] = LongRestFaultClass.NeedsOperator,
    };

    /// <summary>The codes a clean parked sweep may be readmitted from, as a stable array for SQL parameters.</summary>
    public static IReadOnlyList<string> ResumeLaterCodes { get; } =
    [
        .. KnownCodes.Where(static pair => pair.Value == LongRestFaultClass.ResumeLater).Select(static pair => pair.Key).Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The parks that are not failures: a yield to maintenance and an operator retry. Neither waits out a cooldown and
    /// neither spends Sweep Failure Budget, so they stay readmittable even when the budget is spent. Every one is
    /// <see cref="LongRestFaultClass.ResumeLater"/>.
    /// </summary>
    public static IReadOnlyList<string> FreeParkCodes { get; } = [LongRestParkCodes.MaintenanceDeferred, LongRestParkCodes.ReadmitRequested];

    /// <summary>True when <paramref name="code"/> is a free park: it neither waits for a cooldown nor spends budget.</summary>
    public static bool IsFreePark(string? code) => code is not null && FreeParkCodes.Contains(code, StringComparer.Ordinal);

    /// <summary>Classifies a fault code. A missing, malformed or unrecognized code always needs an operator.</summary>
    public static LongRestFaultClass Classify(string? code) =>
        code is not null && KnownCodes.TryGetValue(code, out LongRestFaultClass fault) ? fault : LongRestFaultClass.NeedsOperator;

    /// <summary>True when a work unit that has ended <paramref name="unknownUsageAttempts"/> attempts with unknown billing may send again.</summary>
    public static bool MaySendAfterUnknownUsage(long unknownUsageAttempts) => unknownUsageAttempts is >= 0 and < UnknownUsageCap;

    /// <summary>The closed metric label for a fault class.</summary>
    public static string ClassTag(LongRestFaultClass fault) => fault switch
    {
        LongRestFaultClass.ResumeLater => "resume_later",
        LongRestFaultClass.RestartGeneration => "restart_generation",
        _ => "needs_operator",
    };

    /// <summary>True when <paramref name="code"/> belongs to the Long Rest error family (<c>LongRest.*</c>).</summary>
    public static bool IsLongRestError(string? code) => code is not null && code.StartsWith("LongRest.", StringComparison.Ordinal);

    /// <summary>True when a clean sweep that has already been readmitted <paramref name="readmitCount"/> times may be readmitted again.</summary>
    public static bool CanReadmit(int readmitCount) => readmitCount is >= 0 and < MaxReadmissions;

    /// <summary>How long after its park a sweep that has been readmitted <paramref name="readmitCount"/> times must wait.</summary>
    public static TimeSpan Cooldown(int readmitCount) => CanReadmit(readmitCount)
        ? TimeSpan.FromTicks(ReadmitBaseCooldown.Ticks << readmitCount)
        : throw new ArgumentOutOfRangeException(nameof(readmitCount));
}
