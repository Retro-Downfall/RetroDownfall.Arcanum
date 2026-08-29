using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Security;

public interface IWard
{

    /// <summary>Place a ward and await resolution or timeout.</summary>
    Task<WardResolution> WardAsync(
        string wardId,
        string toolName,
        JsonDocument? arguments,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Resolve a ward. Returns status indicating success, not-found, or already-resolved.</summary>
    ResolveStatus Resolve(string wardId, bool allow, string? reason);

    /// <summary>
    /// Record a decision the host made on its own — no operator is waiting, so no live ward is ever
    /// inserted. The implementation performs an atomic create → resolved transition, so
    /// <see cref="GetActiveWards"/> never lists the ward and a competing <see cref="Resolve"/> loses
    /// with <see cref="ResolveStatus.AlreadyResolved"/> instead of replacing the automatic decision.
    /// A ward id that is already live or already resolved is left untouched and the existing (or a
    /// fail-closed denial) resolution is returned.
    /// </summary>
    WardResolution RecordAutomaticResolution(
        string wardId,
        bool allowed,
        string? reason,
        WardResolutionOrigin origin);

    /// <summary>Snapshot of all active wards.</summary>
    IReadOnlyList<ActiveWard> GetActiveWards();

}

public enum ResolveStatus
{

    Success,

    NotFound,

    AlreadyResolved,

}

/// <summary>
/// Who or what supplied a ward's outcome. Additive observability only — it never widens or narrows
/// what a resolution permits. Serialized as camelCase strings on native NDJSON frames and API
/// payloads via the AOT-safe <see cref="JsonStringEnumConverter{TEnum}"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WardResolutionOrigin>))]
public enum WardResolutionOrigin
{

    /// <summary>An operator resolved the ward through <c>POST /api/wards/{id}</c>.</summary>
    [JsonStringEnumMemberName("human")]
    Human,

    /// <summary>The configured <c>Arcanum:Security:Ward:AutoApprove</c> allowlist supplied consent.</summary>
    [JsonStringEnumMemberName("autoApproved")]
    AutoApproved,

    /// <summary>
    /// The host denied without asking — unattended auto-deny policy, or the active-ward capacity
    /// cap rejecting admission.
    /// </summary>
    [JsonStringEnumMemberName("autoDenied")]
    AutoDenied,

    /// <summary>The ward held until its timeout elapsed.</summary>
    [JsonStringEnumMemberName("timedOut")]
    TimedOut,

    /// <summary>
    /// Contract value for a ward abandoned because its inference turn was cancelled. The waiter
    /// observes <see cref="OperationCanceledException"/> rather than a resolution, so no code path
    /// produces this today; it exists so clients can name the outcome.
    /// </summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>
    /// Contract value paired with <c>"Host restarted — ward timed out"</c>. Wards are in-memory and
    /// a restarted host has none to deny, so no code path produces this today (DESIGN §11.14).
    /// </summary>
    [JsonStringEnumMemberName("hostRestarted")]
    HostRestarted,

    /// <summary>
    /// The tool was not a Ward candidate, so the host recorded the invocation without asking or
    /// blocking it.
    /// </summary>
    [JsonStringEnumMemberName("ungated")]
    Ungated,

}

public sealed record WardResolution(
    bool Allowed,
    string? Reason,
    DateTimeOffset ResolvedAt,
    WardResolutionOrigin Origin = WardResolutionOrigin.Human);

public sealed record ActiveWard(
    string WardId,
    string ToolName,
    JsonDocument? Arguments,
    string? SessionId,
    DateTimeOffset PlacedAt,
    DateTimeOffset ExpiresAt);
