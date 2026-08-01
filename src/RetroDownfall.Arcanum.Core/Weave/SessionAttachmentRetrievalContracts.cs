using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Core.Weave;

[JsonConverter(typeof(JsonStringEnumConverter<SessionAttachmentIndexStatus>))]
public enum SessionAttachmentIndexStatus
{

    NotEligible,

    Pending,

    Indexed,

    Failed,

    Stale,

}

public sealed record SessionAttachmentIndexRequest(
    Guid AttachmentId,
    Guid SessionId,
    int Attempt = 0);

public sealed record SessionAttachmentRetrievedChunk(
    string ChunkId,
    Guid SessionId,
    Guid AttachmentId,
    string LogicalKey,
    int Version,
    string OriginalFileName,
    string MimeType,
    string ContentSha256,
    int ChunkIndex,
    int CharacterStart,
    int CharacterEnd,
    int StartLine,
    int EndLine,
    string Content,
    float Similarity);

public interface ISessionAttachmentIndexQueue
{

    bool TryEnqueue(SessionAttachmentIndexRequest request);

}

public interface ISessionAttachmentRetrievalService
{

    Task<SessionAttachmentRetrievedChunk[]> SearchAsync(
        Guid sessionId,
        Embedding<float> queryEmbedding,
        bool includeHistorical,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken);

}

public interface ISessionAttachmentIndexMaintenance
{

    Task DeleteForSessionInAmbientTransactionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

}
