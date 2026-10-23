using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Builds multimodal <see cref="AIContent"/> for a successful <c>attach_session_file</c> so the
/// next inference round receives <see cref="TextContent"/> / <see cref="DataContent"/> explicitly
/// (not only as tool-result text).
/// </summary>
public static class SessionAttachmentToolInjection
{
    /// <summary>The words that tell a reader the extractor could not retain the whole document.</summary>
    internal const string TruncatedNoticeSuffix = "; truncated at the extraction limit";

    /// <summary>
    /// The untrusted-data notice for a Comprehension document, naming the source format of the record
    /// so a reader can tell what was extracted. A record outside the registry is never framed as a document.
    /// A partial extraction says so, so the text is never read as the whole document.
    /// </summary>
    internal static string ComprehensionDataNotice(SessionAttachmentRecord record, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!SessionAttachmentComprehensionPolicy.TryGetFormat(record.MimeType, out SessionAttachmentComprehensionFormat? format))
        {
            throw new InvalidOperationException("Only a registered Comprehension document can be framed as document data.");
        }

        return "UNTRUSTED DATA; " + format.FormatName + "; layout not preserved"
            + (truncated ? TruncatedNoticeSuffix : string.Empty);
    }

    internal static string FormatComprehensionData(
        SessionAttachmentRecord record,
        string label,
        string text,
        bool truncated = false) =>
        SystemPromptBuilder.FormatUntrusted(label + " | " + ComprehensionDataNotice(record, truncated), text);

    /// <summary>
    /// The extracted text bounded to the per-item byte ceiling, and whether it is partial: the extractor
    /// reported a truncation, or the ceiling cut it. Either way the frame has to say so.
    /// </summary>
    internal static string BoundExtractedText(
        SessionAttachmentExtractionResult extraction,
        long maxTextBytes,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        byte[] encoded = Encoding.UTF8.GetBytes(extraction.Text);

        truncated = extraction.WasTruncated || (maxTextBytes > 0 && encoded.Length > maxTextBytes);

        return DecodeTextWithByteBound(encoded, maxTextBytes);
    }

    /// <summary>
    /// Builds injection contents for a Bound current-session attachment. Enforces combined turn budget,
    /// inject-once, Scrying/vision for images, and size limits (images are rejected, never truncated).
    /// Budget and inject-once are consumed only after validation and materialization succeed.
    /// </summary>
    public static async Task<IReadOnlyList<AIContent>?> TryBuildContentsAsync(
        ISessionAttachmentStore store,
        Guid sessionId,
        string logicalName,
        int? version,
        ArcanumSettings settings,
        string? requestModel,
        CancellationToken cancellationToken = default) =>
        (await MaterializeAttachAsync(store, sessionId, logicalName, version, settings, requestModel, SessionAttachmentDocumentExtractors.For, cancellationToken)
            .ConfigureAwait(false)).Contents;

    /// <summary>
    /// What <c>attach_session_file</c> materialized for the next model turn, or, when it materialized
    /// nothing, the code-owned message the model is shown instead of the tool's acceptance.
    /// </summary>
    internal sealed record AttachMaterialization(IReadOnlyList<AIContent>? Contents, string? NotAttachedMessage);

    /// <summary>The words that open every attach outcome that put nothing into the next model turn.</summary>
    internal const string NotAttachedPrefix = "Not attached: ";

    /// <summary>The hint a busy-family deferral adds: nothing was read, so the same call can succeed later.</summary>
    internal const string RetryLaterHint = " Try attaching it again shortly.";

    /// <summary>
    /// <see cref="TryBuildContentsAsync"/>, also saying why nothing was materialized. The tool accepts an
    /// attachment before anything reads it, so only this decides whether its content reaches the model.
    /// A registry document is extracted by the host <paramref name="documentHostFor"/> names for its MIME
    /// type, which in production is always <see cref="SessionAttachmentDocumentExtractors.For"/>.
    /// </summary>
    internal static async Task<AttachMaterialization> MaterializeAttachAsync(
        ISessionAttachmentStore store,
        Guid sessionId,
        string logicalName,
        int? version,
        ArcanumSettings settings,
        string? requestModel,
        Func<string, SessionAttachmentDocumentExtractionHost?> documentHostFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(documentHostFor);

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return new AttachMaterialization(null, null);
        }

        SessionAttachmentRecord? record = await store
            .GetByLogicalAsync(sessionId, logicalName.Trim(), version, cancellationToken)
            .ConfigureAwait(false);

        if (record is null
            || record.State != SessionAttachmentState.Bound
            || record.SessionId != sessionId
            || string.IsNullOrWhiteSpace(record.RelativePath)
            || Path.IsPathRooted(record.RelativePath)
            || HasParentSegment(record.RelativePath))
        {
            return NotAttached(
                SystemPromptBuilder.HardenAttachmentIndexName(logicalName.Trim()),
                version,
                "The attachment is not available in this session.");
        }

        // The settings are the turn's, so the gates are judged here once, by the snapshot the rest of the
        // turn uses; the tool server judged a registry document by its format alone.
        bool isDocument = SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, settings);

        if (record.Kind is not SessionAttachmentKind.Text and not SessionAttachmentKind.Image
            && !isDocument)
        {
            return NotAttached(record, ArcanumInternalToolServer.AttachmentNotInjectableMessage);
        }

        ReadOnlyMemory<byte> bytes = isDocument
            ? ReadOnlyMemory<byte>.Empty
            : await store.ReadBytesAsync(record, cancellationToken).ConfigureAwait(false);

        List<AIContent> contents;

        if (record.Kind == SessionAttachmentKind.Image)
        {
            string? imageError = ValidateImageAttach(record, bytes.Length, settings, requestModel);

            if (imageError is not null)
            {
                return NotAttached(record, imageError);
            }

            string mime = string.IsNullOrWhiteSpace(record.MimeType) ? "image/*" : record.MimeType;

            string label = FrameLabel(record);

            contents =
            [
                new TextContent(SystemPromptBuilder.FormatUntrustedImageNotice(label))
                {
                    AdditionalProperties = ExplicitAttachmentContextProperties(),
                },
                new DataContent(bytes, mime)
                {
                    AdditionalProperties = ExplicitAttachmentContextProperties(),
                },
            ];
        }
        else if (isDocument)
        {
            long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

            SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor
                .ExtractDocumentAsync(store, record, settings, maxTextBytes, cancellationToken, documentHostFor(record.MimeType))
                .ConfigureAwait(false);

            if (extraction.Status != SessionAttachmentExtractionStatus.Extracted
                || string.IsNullOrWhiteSpace(extraction.Text))
            {
                return NotAttached(record, NotExtractedReason(extraction));
            }

            string text = BoundExtractedText(extraction, maxTextBytes, out bool truncated);

            contents =
            [
                new TextContent(FormatComprehensionData(record, FrameLabel(record), text, truncated))
                {
                    AdditionalProperties = ExplicitAttachmentContextProperties(),
                },
            ];
        }
        else
        {
            long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

            string text;

            if (bytes.Length > maxTextBytes && maxTextBytes > 0)
            {
                // Text may be truncated at a UTF-8 boundary; images must never be truncated (rejected above).
                text = DecodeTextWithByteBound(bytes, maxTextBytes);
            }
            else
            {
                text = Encoding.UTF8.GetString(bytes.Span);
            }

            string label = FrameLabel(record);

            contents =
            [
                new TextContent(SystemPromptBuilder.FormatUntrusted(label, text))
                {
                    AdditionalProperties = ExplicitAttachmentContextProperties(),
                },
            ];
        }

        if (!TryAcceptToolMaterialization(
                record,
                ContextMaterializationSourceKind.AttachSessionFile))
        {
            return NotAttached(record, "It is already in this turn's context.");
        }

        return new AttachMaterialization(contents, null);
    }

    /// <summary>
    /// The code-owned reason a document put no text into the turn: the extraction's own fixed reason, which
    /// never echoes an entry name, part name or document text, and for a busy family a hint to try again.
    /// </summary>
    private static string NotExtractedReason(SessionAttachmentExtractionResult extraction) =>
        extraction.Status switch
        {
            SessionAttachmentExtractionStatus.Deferred =>
                (extraction.FailureReason ?? SessionAttachmentDocumentExtractionHost.BusyReason) + RetryLaterHint,
            SessionAttachmentExtractionStatus.Failed => extraction.FailureReason ?? "Document extraction failed.",
            _ => extraction.FailureReason ?? "The document has no extractable text.",
        };

    private static AttachMaterialization NotAttached(SessionAttachmentRecord record, string reason) =>
        NotAttached(FrameLabel(record), record.Version, reason);

    private static AttachMaterialization NotAttached(string label, int? version, string reason) =>
        new(
            null,
            NotAttachedPrefix + "'" + label + "'"
            + (version is { } selected ? " v" + selected.ToString(CultureInfo.InvariantCulture) : string.Empty)
            + ". " + reason);

    public static async Task<IReadOnlyList<AIContent>?> TryBuildRefreshedContentsAsync(
        ISessionAttachmentStore store,
        SessionAttachmentRecord record,
        ArcanumSettings settings,
        string? requestModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(settings);

        if (record.State != SessionAttachmentState.Bound
            || record.SessionId is null
            || string.IsNullOrWhiteSpace(record.RelativePath)
            || Path.IsPathRooted(record.RelativePath)
            || HasParentSegment(record.RelativePath))
        {
            return null;
        }

        bool isDocument = SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, settings);

        if (record.Kind is not SessionAttachmentKind.Text and not SessionAttachmentKind.Image
            && !isDocument)
        {
            return null;
        }

        ReadOnlyMemory<byte> bytes = isDocument
            ? ReadOnlyMemory<byte>.Empty
            : await store.ReadBytesAsync(record, cancellationToken).ConfigureAwait(false);
        string label = RefreshedFrameLabel(record);
        List<AIContent> contents;

        if (record.Kind == SessionAttachmentKind.Image)
        {
            if (ValidateImageAttach(record, bytes.Length, settings, requestModel) is not null)
            {
                return null;
            }

            contents =
            [
                new TextContent(SystemPromptBuilder.FormatUntrustedImageNotice(label))
                {
                    AdditionalProperties = RefreshedContextProperties(),
                },
                new DataContent(bytes, record.MimeType)
                {
                    AdditionalProperties = RefreshedContextProperties(),
                },
            ];
        }
        else if (isDocument)
        {
            long maxTextBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
                ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

            SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor
                .ExtractDocumentAsync(store, record, settings, maxTextBytes, cancellationToken)
                .ConfigureAwait(false);

            if (extraction.Status != SessionAttachmentExtractionStatus.Extracted
                || string.IsNullOrWhiteSpace(extraction.Text))
            {
                return null;
            }

            string text = BoundExtractedText(extraction, maxTextBytes, out bool truncated);

            contents =
            [
                new TextContent(FormatComprehensionData(record, label, text, truncated))
                {
                    AdditionalProperties = RefreshedContextProperties(),
                },
            ];
        }
        else
        {
            string text = DecodeTextWithByteBound(
                bytes,
                ArcanumSettingClamps.MaxAttachFileSizeBytes(
                    ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes));
            contents =
            [
                new TextContent(SystemPromptBuilder.FormatUntrusted(label, text))
                {
                    AdditionalProperties = RefreshedContextProperties(),
                },
            ];
        }

        return TryAcceptToolMaterialization(
                record,
                ContextMaterializationSourceKind.RefreshSessionFile)
            ? contents
            : null;
    }

    /// <summary>
    /// True when a whole path segment is <c>..</c>. A name that merely contains or starts with two dots
    /// (<c>notes..v2.txt</c>, <c>..cache</c>) is an ordinary child and must not be refused.
    /// </summary>
    private static bool HasParentSegment(string relativePath)
    {
        foreach (string segment in relativePath.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAcceptToolMaterialization(
        SessionAttachmentRecord record,
        ContextMaterializationSourceKind sourceKind)
    {
        ContextMaterializationLedger? ledger = ContextMaterializationLedgerAmbient.Ledger;

        if (ledger is null)
        {
            return SessionAttachmentTurnBudget.TryMarkInjected(
                record.LogicalKey,
                record.Version);
        }

        ContextMaterializationEntry entry = ledger.Accept(
            new ContextMaterializationCandidate(
                record.SessionId,
                sourceKind,
                record.Id.ToString("N"),
                record.Version.ToString(CultureInfo.InvariantCulture),
                record.Version,
                ContextMaterializationRange.Whole,
                ContextMaterializationOrigin.ModelRequested,
                FrameLabel(record),
                record.ContentSha256,
                EstimatedTokens: EstimateTokens(record.ByteLength),
                MaterializedBytes: int.CreateSaturating(record.ByteLength),
                ContextMaterializationTrust.UntrustedData)
            {
                AttachmentProvenance = new AttachmentMemoryProvenance(
                    record.SessionId ?? Guid.Empty,
                    record.Id,
                    record.LogicalKey,
                    record.Version,
                    record.ContentSha256,
                    DateTimeOffset.UtcNow,
                    record.Source?.Kind.ToString() ?? AttachmentSourceKind.SnapshotOnly.ToString(),
                    AttachmentSourceAvailability.Available),
            },
            materialized: true);

        if (!entry.Accepted ||
            !SessionAttachmentTurnBudget.TryMarkInjected(record.LogicalKey, record.Version))
        {
            return false;
        }

        if (record.Source is
            {
                Kind: AttachmentSourceKind.WorkspaceFile,
                WorkspaceRelativePath: { Length: > 0 } workspaceRelativePath,
            })
        {
            ledger.RegisterExplicitWorkspaceSource(workspaceRelativePath);
        }

        return ledger.TryMarkInjected(
            entry.Identity,
            ContextMaterializationLedgerAmbient.ProviderRound);
    }

    private static int EstimateTokens(long materializedBytes) =>
        int.CreateSaturating((Math.Max(0L, materializedBytes) + 3L) / 4L);

    private static AdditionalPropertiesDictionary RefreshedContextProperties() =>
        new()
        {
            ["arcanum.context_source"] = "refreshedFile",
        };

    private static AdditionalPropertiesDictionary ExplicitAttachmentContextProperties() =>
        new()
        {
            ["arcanum.context_source"] = "explicitAttachment",
        };

    private static string FrameLabel(SessionAttachmentRecord record)
    {
        string hardened = SystemPromptBuilder.HardenAttachmentIndexName(record.OriginalFileName);

        if (hardened.Length == 0)
        {
            hardened = SystemPromptBuilder.HardenAttachmentIndexName(record.LogicalKey);
        }

        if (hardened.Length == 0)
        {
            hardened = "attachment";
        }

        return hardened;
    }

    private static string RefreshedFrameLabel(SessionAttachmentRecord record)
    {
        string fileName = FrameLabel(record);
        string logicalKey = SystemPromptBuilder.HardenAttachmentIndexName(record.LogicalKey);
        string freshness = record.Source?.LastObservedWriteTime?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            ?? "unknown";
        return $"{fileName} | logicalKey={logicalKey} | version={record.Version.ToString(CultureInfo.InvariantCulture)} | sourceFreshness={freshness}";
    }

    /// <summary>Shared image gate for user rehydrate and model inject.</summary>
    public static string? ValidateImageAttach(
        SessionAttachmentRecord record,
        long byteLength,
        ArcanumSettings settings,
        string? requestModel)
    {
        ScryingSettings scrying = settings.ResolveScrying();

        if (!scrying.Enabled)
        {
            return "Scrying is disabled; image attachments cannot be re-attached.";
        }

        if (!ProviderResolver.SupportsVision(settings, requestModel))
        {
            return $"Model '{requestModel ?? "(default)"}' does not support vision. Use a vision-capable model.";
        }

        long maxImageBytes = ArcanumSettingClamps.ScryingMaxImageBytes(scrying.MaxImageBytes);

        if (byteLength > maxImageBytes)
        {
            return $"Image attachment '{record.LogicalKey}' exceeds MaxImageBytes ({maxImageBytes}); images are rejected, never truncated.";
        }

        return null;
    }

    public static bool TryParseAttachArguments(
        IDictionary<string, object?>? arguments,
        out string logicalName,
        out int? version)
    {
        logicalName = string.Empty;

        version = null;

        if (arguments is null || arguments.Count == 0)
        {
            return false;
        }

        if (!TryGetArgumentValue(arguments, "logicalName", out object? nameRaw)
            || !TryCoerceString(nameRaw, out string? name)
            || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        logicalName = name.Trim();

        if (TryGetArgumentValue(arguments, "version", out object? versionRaw)
            && TryCoerceInt(versionRaw, out int parsedVersion))
        {
            version = parsedVersion;
        }

        return true;
    }

    private static bool TryGetArgumentValue(
        IDictionary<string, object?> arguments,
        string name,
        out object? value)
    {
        if (arguments.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;

                return true;
            }
        }

        value = null;

        return false;
    }

    private static bool TryCoerceString(object? raw, out string? value)
    {
        switch (raw)
        {
            case null:
                value = null;
                return false;

            case string s:
                value = s;
                return true;

            case JsonElement { ValueKind: JsonValueKind.String } je:
                value = je.GetString();
                return true;

            default:
                value = raw.ToString();
                return !string.IsNullOrWhiteSpace(value);
        }
    }

    private static bool TryCoerceInt(object? raw, out int value)
    {
        switch (raw)
        {
            case null:
                value = 0;
                return false;

            case int i:
                value = i;
                return true;

            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;

            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out int fromJson):
                value = fromJson;
                return true;

            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromString):
                value = fromString;
                return true;

            default:
                value = 0;
                return false;
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
}
