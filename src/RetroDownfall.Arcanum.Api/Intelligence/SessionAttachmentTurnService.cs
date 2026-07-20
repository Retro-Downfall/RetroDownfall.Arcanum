using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Persist-before-model preparation for session attachments: validate refs, persist new
/// AttachedFiles / ScryingFoci, rehydrate requested references, and build the prompt index.
/// </summary>
public static class SessionAttachmentTurnService
{

    private static readonly SessionAttachmentTurnPreparation Empty = new(
        IndexItems: [],
        RehydratedContents: [],
        PendingTurnId: null,
        ErrorMessage: null);

    /// <summary>
    /// When <see cref="AttachmentsSettings.Enabled"/> is false, returns an empty preparation.
    /// On validation or persist failure, <see cref="SessionAttachmentTurnPreparation.ErrorMessage"/> is set
    /// so the caller can fail closed before the model call.
    /// </summary>
    public static async Task<SessionAttachmentTurnPreparation> PrepareAsync(
        PingRequest request,
        ISessionAttachmentStore store,
        ArcanumSettings settings,
        Guid? turnSessionId,
        Guid? turnEntryId,
        string? pendingTurnId,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        AttachmentsSettings attachments = settings.Attachments ?? new AttachmentsSettings();

        if (!attachments.Enabled)
        {
            return Empty;
        }

        try
        {
            string? validationError = await SessionAttachmentRequestValidator
                .ValidateAsync(request, store, attachments, cancellationToken)
                .ConfigureAwait(false);

            if (validationError is not null)
            {
                return new SessionAttachmentTurnPreparation([], [], null, validationError);
            }

            Guid? sessionId = request.SessionId ?? turnSessionId;
            string? effectivePending = sessionId is null ? pendingTurnId : null;
            Guid? entryId = turnEntryId;

            if (request.AttachedFiles is { Count: > 0 })
            {
                foreach (AttachedFileDto file in request.AttachedFiles)
                {
                    string nameHint = Path.GetFileName(file.RelativePath);

                    if (string.IsNullOrWhiteSpace(nameHint))
                    {
                        nameHint = "attachment.txt";
                    }

                    ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(file.Content ?? string.Empty);

                    await store
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

                    await store
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
                }
            }

            List<AIContent> rehydrated = [];

            if (request.AttachmentReferences is { Count: > 0 } && request.SessionId is { } refSessionId)
            {
                long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                    settings.Cli.MaxAttachFileSizeBytes);

                foreach (Guid attachmentId in request.AttachmentReferences)
                {
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

                    ReadOnlyMemory<byte> bytes = await store
                        .ReadBytesAsync(record, cancellationToken)
                        .ConfigureAwait(false);

                    if (record.Kind == SessionAttachmentKind.Image)
                    {
                        rehydrated.Add(new DataContent(bytes, record.MimeType));
                    }
                    else
                    {
                        string text = DecodeTextWithByteBound(bytes, maxTextBytes);
                        rehydrated.Add(new TextContent(text));
                    }
                }
            }

            IReadOnlyList<SessionAttachmentIndexItem> indexItems = [];

            if (sessionId is { } boundSessionId)
            {
                int maxItems = ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt(
                    attachments.MaxIndexItemsInPrompt);

                indexItems = await store
                    .BuildIndexAsync(boundSessionId, maxItems, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SessionAttachmentTurnPreparation(
                indexItems,
                rehydrated,
                effectivePending,
                ErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionAttachmentTurnPreparation(
                [],
                [],
                null,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "Session attachment persistence failed."
                    : ex.Message);
        }

    }

    private static string DecodeTextWithByteBound(ReadOnlyMemory<byte> bytes, long maxTextBytes)
    {

        ReadOnlySpan<byte> span = bytes.Span;

        if (span.Length > maxTextBytes && maxTextBytes > 0)
        {
            int limit = (int)Math.Min(maxTextBytes, int.MaxValue);
            span = TruncateUtf8ToRuneBoundary(span, limit);
        }

        return Encoding.UTF8.GetString(span);

    }

    /// <summary>Truncates UTF-8 bytes without splitting a multi-byte codepoint.</summary>
    internal static ReadOnlySpan<byte> TruncateUtf8ToRuneBoundary(ReadOnlySpan<byte> utf8, int maxBytes)
    {

        if (utf8.Length <= maxBytes || maxBytes <= 0)
        {
            return utf8.Length <= maxBytes ? utf8 : utf8[..Math.Max(0, maxBytes)];
        }

        int end = maxBytes;
        while (end > 0 && (utf8[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return utf8[..end];

    }

}

/// <summary>
/// Result of <see cref="SessionAttachmentTurnService.PrepareAsync"/>. Non-null
/// <see cref="ErrorMessage"/> means fail closed before the model call.
/// </summary>
public sealed record SessionAttachmentTurnPreparation(
    IReadOnlyList<SessionAttachmentIndexItem> IndexItems,
    IReadOnlyList<AIContent> RehydratedContents,
    string? PendingTurnId,
    string? ErrorMessage);
