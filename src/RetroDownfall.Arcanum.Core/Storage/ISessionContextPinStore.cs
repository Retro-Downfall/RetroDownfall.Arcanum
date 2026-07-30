using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<SessionContextPinKind>))]
public enum SessionContextPinKind
{
    File,
    DirectorySnapshot,
    SymbolRange,
    SessionEntry,
    Attachment,
    Url,
    Diagnostic,
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionContextPinStatus>))]
public enum SessionContextPinStatus
{
    Current,
    Modified,
    Missing,
    Unsafe,
    Truncated,
    Unsupported,
    Error,
}

public sealed record SessionContextPinRecord(
    Guid Id,
    Guid SessionId,
    SessionContextPinKind Kind,
    string TargetIdentifier,
    string DisplayLabel,
    string? ContentVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface ISessionContextPinStore
{
    Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionContextPinRecord> UpsertAsync(
        Guid sessionId,
        SessionContextPinKind kind,
        string targetIdentifier,
        string displayLabel,
        string? contentVersion,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid sessionId,
        Guid pinId,
        CancellationToken cancellationToken = default);
}
