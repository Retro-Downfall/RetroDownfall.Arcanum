using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<SessionAttachmentKind>))]
public enum SessionAttachmentKind
{

    Text,

    Image,

}

public enum SessionAttachmentState
{

    Pending,

    Bound,

}

public sealed record SessionAttachmentRecord(
    Guid Id,
    Guid? SessionId,
    Guid? EntryId,
    string? PendingTurnId,
    SessionAttachmentState State,
    string LogicalKey,
    string OriginalFileName,
    int Version,
    string RelativePath,
    string ContentSha256,
    string MimeType,
    long ByteLength,
    SessionAttachmentKind Kind,
    DateTimeOffset CreatedAt);

public sealed record SessionAttachmentIndexItem(
    string LogicalKey,
    string OriginalFileName,
    IReadOnlyList<int> Versions,
    SessionAttachmentKind Kind,
    long LatestByteLength);

public interface ISessionAttachmentStore
{

    Task<SessionAttachmentRecord> PersistNewAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        CancellationToken cancellationToken = default);

    Task PromotePendingAsync(string pendingTurnId, Guid sessionId, Guid? entryId, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByLogicalAsync(Guid sessionId, string logicalKey, int? version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(Guid sessionId, int maxItems, CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> ReadBytesAsync(SessionAttachmentRecord record, CancellationToken cancellationToken = default);

    Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    Task ValidateReferencesAsync(Guid sessionId, IReadOnlyList<Guid> attachmentIds, int maxReferences, CancellationToken cancellationToken = default);

}
