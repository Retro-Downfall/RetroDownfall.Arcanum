using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>No-op <see cref="ISessionAttachmentStore"/> for WizardIntelligenceProvider test factories.</summary>
internal sealed class NoOpSessionAttachmentStore : ISessionAttachmentStore
{

    public Task<SessionAttachmentRecord> PersistNewAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionAttachmentRecord(
            Guid.NewGuid(),
            sessionId,
            entryId,
            pendingTurnId,
            sessionId is null ? SessionAttachmentState.Pending : SessionAttachmentState.Bound,
            logicalNameHint,
            originalFileName,
            1,
            "noop",
            "noop",
            mimeType,
            bytes.Length,
            kind,
            DateTimeOffset.UtcNow));

    public Task PromotePendingAsync(
        string pendingTurnId,
        Guid sessionId,
        Guid? entryId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<SessionAttachmentRecord?>(null);

    public Task<SessionAttachmentRecord?> GetByLogicalAsync(
        Guid sessionId,
        string logicalKey,
        int? version,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<SessionAttachmentRecord?>(null);

    public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>([]);

    public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
        Guid sessionId,
        int maxItems,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>([]);

    public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
        SessionAttachmentRecord record,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ReadOnlyMemory<byte>.Empty);

    public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ValidateReferencesAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        int maxReferences,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

}
