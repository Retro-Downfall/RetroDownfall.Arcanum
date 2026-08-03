using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Persist-before-model preparation for session attachments: validate refs, persist new
/// AttachedFiles / ScryingFoci, rehydrate requested references, and build the prompt index.
/// </summary>
public static class SessionAttachmentTurnService
{
    internal const string PublicPersistenceFailureMessage =
        "Session attachment persistence failed.";

    internal const string PublicPromotionFailureMessage =
        "Session attachment promotion failed.";

    private static readonly SessionAttachmentTurnPreparation Empty = new(
        IndexItems: [],
        RehydratedContents: [],
        PendingTurnId: null,
        ErrorMessage: null,
        ExplicitMaterializations: []);

    /// <summary>
    /// When <see cref="AttachmentsSettings.Enabled"/> is false, returns an empty preparation.
    /// On validation or persist failure, <see cref="SessionAttachmentTurnPreparation.ErrorMessage"/> is set
    /// so the caller can fail closed before the model call.
    /// </summary>
    public static Task<SessionAttachmentTurnPreparation> PrepareAsync(
        PingRequest request,
        ISessionAttachmentStore store,
        ArcanumSettings settings,
        Guid? turnSessionId,
        Guid? turnEntryId,
        string? pendingTurnId,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(
            request,
            store,
            settings,
            turnSessionId,
            turnEntryId,
            pendingTurnId,
            cancellationToken,
            logger: null);

    internal static async Task<SessionAttachmentTurnPreparation> PrepareAsync(
        PingRequest request,
        ISessionAttachmentStore store,
        ArcanumSettings settings,
        Guid? turnSessionId,
        Guid? turnEntryId,
        string? pendingTurnId,
        CancellationToken cancellationToken,
        ILogger? logger)
    {

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        AttachmentsSettings attachments = settings.ResolveAttachments();

        if (!attachments.Enabled)
        {
            return Empty;
        }

        Guid? sessionId = request.SessionId ?? turnSessionId;
        string operation = "validation";

        try
        {
            string? validationError = await SessionAttachmentRequestValidator
                .ValidateAsync(request, store, attachments, cancellationToken)
                .ConfigureAwait(false);

            if (validationError is not null)
            {
                return new SessionAttachmentTurnPreparation([], [], null, validationError);
            }

            string? effectivePending = sessionId is null ? pendingTurnId : null;
            Guid? entryId = turnEntryId;
            HashSet<Guid> explicitlyVisibleAttachmentIds = [];
            List<SessionAttachmentExplicitMaterialization> explicitMaterializations = [];

            if (request.AttachedFiles is { Count: > 0 })
            {
                for (int attachedFileIndex = 0; attachedFileIndex < request.AttachedFiles.Count; attachedFileIndex++)
                {
                    AttachedFileDto file = request.AttachedFiles[attachedFileIndex];

                    string nameHint = Path.GetFileName(file.RelativePath);

                    if (string.IsNullOrWhiteSpace(nameHint))
                    {
                        nameHint = "attachment.txt";
                    }

                    ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);

                    operation = "persist-text";

                    SessionAttachmentRecord persisted = await store
                        .PersistNewAsync(
                            sessionId,
                            effectivePending,
                            entryId,
                            nameHint,
                            nameHint,
                            bytes,
                            "text/plain",
                            SessionAttachmentKind.Text,
                            cancellationToken)
                        .ConfigureAwait(false);
                    explicitlyVisibleAttachmentIds.Add(persisted.Id);

                    explicitMaterializations.Add(
                        new SessionAttachmentExplicitMaterialization(
                            persisted,
                            ContextMaterializationSourceKind.CurrentTurnAttachment,
                            [],
                            attachedFileIndex));
                }
            }

            if (request.ScryingFoci is { Count: > 0 })
            {
                for (int i = 0; i < request.ScryingFoci.Count; i++)
                {
                    ScryingFocusDto focus = request.ScryingFoci[i];
                    string nameHint = $"image-{i}.png";
                    byte[] decoded;

                    try
                    {
                        decoded = Convert.FromBase64String(focus.Data);
                    }
                    catch (FormatException)
                    {
                        return new SessionAttachmentTurnPreparation(
                            [],
                            [],
                            effectivePending,
                            "Scrying focus data is not valid base64.");
                    }

                    string mime = string.IsNullOrWhiteSpace(focus.MimeType) ? "image/png" : focus.MimeType;

                    operation = "persist-image";

                    SessionAttachmentRecord persisted = await store
                        .PersistNewAsync(
                            sessionId,
                            effectivePending,
                            entryId,
                            nameHint,
                            nameHint,
                            decoded,
                            mime,
                            SessionAttachmentKind.Image,
                            cancellationToken)
                        .ConfigureAwait(false);
                    explicitlyVisibleAttachmentIds.Add(persisted.Id);

                    explicitMaterializations.Add(
                        new SessionAttachmentExplicitMaterialization(
                            persisted,
                            ContextMaterializationSourceKind.CurrentTurnAttachment,
                            [],
                            ScryingFocusIndex: i));
                }
            }

            List<AIContent> rehydrated = [];

            if (request.AttachmentReferences is { Count: > 0 } && request.SessionId is { } refSessionId)
            {
                foreach (Guid attachmentId in request.AttachmentReferences)
                {
                    operation = "load-reference";

                    SessionAttachmentRecord? record = await store
                        .GetByIdAsync(attachmentId, cancellationToken)
                        .ConfigureAwait(false);

                    if (record is null)
                    {
                        return new SessionAttachmentTurnPreparation(
                            [],
                            [],
                            effectivePending,
                            $"Attachment '{attachmentId}' was not found.");
                    }

                    if (record.State != SessionAttachmentState.Bound
                        || record.SessionId != refSessionId)
                    {
                        return new SessionAttachmentTurnPreparation(
                            [],
                            [],
                            effectivePending,
                            $"Attachment '{attachmentId}' is not a bound attachment for this session.");
                    }

                    if (record.Kind is not SessionAttachmentKind.Text

                        and not SessionAttachmentKind.Image)

                    {

                        return new SessionAttachmentTurnPreparation(

                            [],

                            [],

                            effectivePending,

                            $"Attachment '{attachmentId}' cannot be injected into model context.");

                    }

                    explicitlyVisibleAttachmentIds.Add(record.Id);

                    explicitMaterializations.Add(
                        new SessionAttachmentExplicitMaterialization(
                            record,
                            ContextMaterializationSourceKind.ExplicitAttachmentReference,
                            [],
                            RequiresMaterialization: true));
                }
            }

            IReadOnlyList<SessionAttachmentIndexItem> indexItems = [];

            IReadOnlySet<Guid>? visibleAttachmentIds = null;

            if (sessionId is { } boundSessionId)
            {
                operation = "build-index";

                int maxItems = ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(
                    attachments.MaxIndexItemsInPrompt);

                indexItems = await store
                    .BuildIndexAsync(boundSessionId, maxItems, cancellationToken)
                    .ConfigureAwait(false);

                List<string> indexedLogicalKeys =
                    new(indexItems.Count);
                HashSet<string> seenLogicalKeys =
                    new(StringComparer.Ordinal);

                foreach (SessionAttachmentIndexItem item in indexItems)
                {

                    if (seenLogicalKeys.Add(item.LogicalKey))
                    {

                        indexedLogicalKeys.Add(item.LogicalKey);
                    }
                }

                IReadOnlyList<SessionAttachmentRecord> indexedLatest =
                    await store.ListLatestBoundByLogicalKeysAsync(
                            boundSessionId,
                            indexedLogicalKeys,
                            cancellationToken)
                        .ConfigureAwait(false);
                explicitlyVisibleAttachmentIds.UnionWith(
                    indexedLatest.Select(static row => row.Id));
                visibleAttachmentIds = explicitlyVisibleAttachmentIds;
            }

            return new SessionAttachmentTurnPreparation(
                indexItems,
                rehydrated,
                effectivePending,
                ErrorMessage: null,
                visibleAttachmentIds,
                explicitMaterializations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Session attachment preparation failed during {Operation}; exception type {ExceptionType}, session {SessionId}, entry {EntryId}.",
                operation,
                ex.GetType().FullName,
                sessionId,
                turnEntryId);

            return new SessionAttachmentTurnPreparation(
                [],
                [],
                null,
                PublicPersistenceFailureMessage);
        }

    }

    internal static async Task<SessionAttachmentReferenceMaterialization> MaterializeReferenceAsync(
        SessionAttachmentRecord record,
        ISessionAttachmentStore store,
        ArcanumSettings settings,
        string resolvedModel,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(record);

        ArgumentNullException.ThrowIfNull(store);

        ArgumentNullException.ThrowIfNull(settings);

        try
        {

            List<AIContent> contents = [];

            if (record.Kind == SessionAttachmentKind.Image)
            {

                int declaredLength = int.CreateSaturating(record.ByteLength);

                string? imageError = SessionAttachmentToolInjection.ValidateImageAttach(
                    record,
                    declaredLength,
                    settings,
                    resolvedModel);

                if (imageError is not null)
                {

                    return new SessionAttachmentReferenceMaterialization([], imageError);

                }

                await using Stream stream = await store
                    .OpenReadAsync(record, cancellationToken)
                    .ConfigureAwait(false);

                ReadOnlyMemory<byte> bytes = await ReadDeclaredBytesAsync(
                    stream,
                    record.ByteLength,
                    cancellationToken).ConfigureAwait(false);

                string imageLabel = ResolveReferenceLabel(record, "image");

                contents.Add(
                    new TextContent(SystemPromptBuilder.FormatUntrustedImageNotice(imageLabel))
                    {

                        AdditionalProperties = ExplicitAttachmentContextProperties(),

                    });

                contents.Add(
                    new DataContent(bytes, record.MimeType)
                    {

                        AdditionalProperties = ExplicitAttachmentContextProperties(),

                    });

                return new SessionAttachmentReferenceMaterialization(contents, ErrorMessage: null);

            }

            if (record.Kind != SessionAttachmentKind.Text)
            {

                return new SessionAttachmentReferenceMaterialization(
                    [],
                    $"Attachment '{record.Id}' cannot be injected into model context.");

            }

            long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

            long readLength = Math.Min(
                record.ByteLength,
                checked(maxTextBytes + 1));

            await using (Stream stream = await store
                .OpenReadAsync(record, cancellationToken)
                .ConfigureAwait(false))
            {

                ReadOnlyMemory<byte> bytes = await ReadDeclaredBytesAsync(
                    stream,
                    readLength,
                    cancellationToken).ConfigureAwait(false);

                string text = DecodeTextWithByteBound(bytes, maxTextBytes);

                string label = ResolveReferenceLabel(record, "attachment");

                contents.Add(
                    new TextContent(SystemPromptBuilder.FormatUntrusted(label, text))
                    {

                        AdditionalProperties = ExplicitAttachmentContextProperties(),

                    });

            }

            return new SessionAttachmentReferenceMaterialization(contents, ErrorMessage: null);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception)
        {

            string label = ResolveReferenceLabel(record, record.Id.ToString());

            return new SessionAttachmentReferenceMaterialization(
                [],
                $"Attachment '{label}' could not be read for model context. Retry the request or refresh the attachment.");

        }

    }

    /// <summary>
    /// Binds pending attachments to a newly created session. Cancellation propagates; unexpected
    /// failures return the stable public promotion message and log only safe metadata.
    /// </summary>
    internal static async Task<string?> PromotePendingAsync(
        ISessionAttachmentStore store,
        string pendingTurnId,
        Guid sessionId,
        Guid? entryId,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            await store
                .PromotePendingAsync(
                    pendingTurnId,
                    sessionId,
                    entryId,
                    cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Failed to promote pending session attachments for session {SessionId}; exception type {ExceptionType}.",
                sessionId,
                ex.GetType().FullName);

            return PublicPromotionFailureMessage;
        }
    }

    private static string DecodeTextWithByteBound(ReadOnlyMemory<byte> bytes, long maxTextBytes)
    {

        ReadOnlySpan<byte> span = bytes.Span;

        if (span.Length > maxTextBytes && maxTextBytes > 0)
        {
            int limit = (int)Math.Min(maxTextBytes, int.MaxValue);
            span = Utf8Truncation.TruncateUtf8BytesToCodepointBoundary(span, limit);
        }

        return Encoding.UTF8.GetString(span);

    }

    private static async Task<ReadOnlyMemory<byte>> ReadDeclaredBytesAsync(
        Stream stream,
        long declaredLength,
        CancellationToken cancellationToken)
    {

        if (declaredLength < 0 || declaredLength > int.MaxValue)
        {

            throw new InvalidDataException(
                "Attachment exceeds the per-item in-memory provider boundary.");

        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)declaredLength);

        int offset = 0;

        while (offset < bytes.Length)
        {

            int read = await stream
                .ReadAsync(bytes.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {

                throw new InvalidDataException(
                    "Attachment ended before its declared byte length.");

            }

            offset += read;

        }

        return bytes;

    }

    private static string ResolveReferenceLabel(
        SessionAttachmentRecord record,
        string fallback)
    {

        string label = SystemPromptBuilder.HardenAttachmentIndexName(record.OriginalFileName);

        if (label.Length == 0)
        {

            label = SystemPromptBuilder.HardenAttachmentIndexName(record.LogicalKey);

        }

        return label.Length == 0 ? fallback : label;

    }

    private static AdditionalPropertiesDictionary ExplicitAttachmentContextProperties() =>
        new()
        {

            ["arcanum.context_source"] = "explicitAttachment",

        };

    /// <summary>
    /// Obsolete wrapper — prefer <see cref="Utf8Truncation.TruncateUtf8BytesToCodepointBoundary"/>.
    /// </summary>
    [Obsolete("Use Utf8Truncation.TruncateUtf8BytesToCodepointBoundary.")]
    internal static ReadOnlySpan<byte> TruncateUtf8ToRuneBoundary(ReadOnlySpan<byte> utf8, int maxBytes) =>
        Utf8Truncation.TruncateUtf8BytesToCodepointBoundary(utf8, maxBytes);

}

/// <summary>
/// Result of <see cref="SessionAttachmentTurnService.PrepareAsync"/>. Non-null
/// <see cref="ErrorMessage"/> means fail closed before the model call.
/// </summary>
public sealed record SessionAttachmentTurnPreparation(
    IReadOnlyList<SessionAttachmentIndexItem> IndexItems,
    IReadOnlyList<AIContent> RehydratedContents,
    string? PendingTurnId,
    string? ErrorMessage,
    IReadOnlySet<Guid>? VisibleAttachmentIds = null,
    IReadOnlyList<SessionAttachmentExplicitMaterialization>? ExplicitMaterializations = null);

public sealed record SessionAttachmentExplicitMaterialization(
    SessionAttachmentRecord Record,
    ContextMaterializationSourceKind SourceKind,
    IReadOnlyList<AIContent> Contents,
    int? AttachedFileIndex = null,
    int? ScryingFocusIndex = null,
    bool RequiresMaterialization = false);

internal sealed record SessionAttachmentReferenceMaterialization(
    IReadOnlyList<AIContent> Contents,
    string? ErrorMessage);
