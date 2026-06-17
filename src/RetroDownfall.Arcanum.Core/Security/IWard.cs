using System.Text.Json;

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

    /// <summary>Snapshot of all active wards.</summary>
    IReadOnlyList<ActiveWard> GetActiveWards();

}

public enum ResolveStatus
{

    Success,

    NotFound,

    AlreadyResolved,

}

public sealed record WardResolution(bool Allowed, string? Reason, DateTimeOffset ResolvedAt);

public sealed record ActiveWard(
    string WardId,
    string ToolName,
    JsonDocument? Arguments,
    string? SessionId,
    DateTimeOffset PlacedAt,
    DateTimeOffset ExpiresAt);
