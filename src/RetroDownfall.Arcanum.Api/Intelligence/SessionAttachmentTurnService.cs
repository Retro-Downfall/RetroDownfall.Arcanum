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

                    explicitlyVisibleAttachmentIds.Add(record.Id);

                    List<AIContent> referenceContents = [];

                    operation = "read-reference";

                    ReadOnlyMemory<byte> bytes = await store
                        .ReadBytesAsync(record, cancellationToken)
                        .ConfigureAwait(false);

                    if (record.Kind == SessionAttachmentKind.Image)
                    {
                        string? imageError = SessionAttachmentToolInjection.ValidateImageAttach(
                            record,
                            bytes.Length,
                            settings,
                            request.Model);

                        if (imageError is not null)
                        {
                            return new SessionAttachmentTurnPreparation(
                                [],
                                [],
                                effectivePending,
                                imageError);
                        }

                        string imageLabel = SystemPromptBuilder.HardenAttachmentIndexName(record.OriginalFileName);

                        if (imageLabel.Length == 0)
                        {
                            imageLabel = SystemPromptBuilder.HardenAttachmentIndexName(record.LogicalKey);
                        }

                        if (imageLabel.Length == 0)
                        {
                            imageLabel = "image";
                        }

                        referenceContents.Add(
                            new TextContent(SystemPromptBuilder.FormatUntrustedImageNotice(imageLabel))
                            {

                                AdditionalProperties = ExplicitAttachmentContextProperties(),

                            });

                        referenceContents.Add(
                            new DataContent(bytes, record.MimeType)
                            {

                                AdditionalProperties = ExplicitAttachmentContextProperties(),

                            });
                    }
                    else
                    {
                        long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

                        string text = DecodeTextWithByteBound(bytes, maxTextBytes);

                        string label = SystemPromptBuilder.HardenAttachmentIndexName(record.OriginalFileName);

                        if (label.Length == 0)
                        {
                            label = SystemPromptBuilder.HardenAttachmentIndexName(record.LogicalKey);
                        }

                        if (label.Length == 0)
                        {
                            label = "attachment";
                        }

                        referenceContents.Add(
                            new TextContent(SystemPromptBuilder.FormatUntrusted(label, text))
                            {

                                AdditionalProperties = ExplicitAttachmentContextProperties(),

                            });
                    }

                    rehydrated.AddRange(referenceContents);

                    explicitMaterializations.Add(
                        new SessionAttachmentExplicitMaterialization(
                            record,
                            ContextMaterializationSourceKind.ExplicitAttachmentReference,
                            referenceContents));
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

                HashSet<string> indexedLogicalKeys = indexItems
                    .Select(static item => item.LogicalKey)
                    .ToHashSet(StringComparer.Ordinal);
                explicitlyVisibleAttachmentIds.UnionWith((await store
                        .ListBoundAsync(boundSessionId, cancellationToken)
                        .ConfigureAwait(false))
                    .Where(row => indexedLogicalKeys.Contains(row.LogicalKey))
                    .Select(static row => row.Id));
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
    int? ScryingFocusIndex = null);
