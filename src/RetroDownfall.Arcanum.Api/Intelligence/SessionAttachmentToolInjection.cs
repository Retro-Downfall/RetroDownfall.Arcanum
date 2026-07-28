using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Builds multimodal <see cref="AIContent"/> for a successful <c>attach_session_file</c> so the
/// next inference round receives <see cref="TextContent"/> / <see cref="DataContent"/> explicitly
/// (not only as tool-result text).
/// </summary>
public static class SessionAttachmentToolInjection
{

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
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return null;
        }

        SessionAttachmentRecord? record = await store
            .GetByLogicalAsync(sessionId, logicalName.Trim(), version, cancellationToken)
            .ConfigureAwait(false);

        if (record is null
            || record.State != SessionAttachmentState.Bound
            || record.SessionId != sessionId)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(record.RelativePath)
            || Path.IsPathRooted(record.RelativePath)
            || record.RelativePath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlyMemory<byte> bytes = await store
            .ReadBytesAsync(record, cancellationToken)
            .ConfigureAwait(false);

        List<AIContent> contents;

        if (record.Kind == SessionAttachmentKind.Image)
        {
            string? imageError = ValidateImageAttach(record, bytes.Length, settings, requestModel);

            if (imageError is not null)
            {
                return null;
            }

            string mime = string.IsNullOrWhiteSpace(record.MimeType) ? "image/*" : record.MimeType;

            string label = FrameLabel(record);

            contents =
            [
                new TextContent(SystemPromptBuilder.FormatUntrustedImageNotice(label)),
                new DataContent(bytes, mime),
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

            contents = [new TextContent(SystemPromptBuilder.FormatUntrusted(label, text))];
        }

        if (!SessionAttachmentTurnBudget.TryConsumeAndMarkInjected(record.LogicalKey, record.Version))
        {
            return null;
        }

        return contents;

    }

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

        long maxImageBytes = ArcanumSettingClamps.ScryingMaxImageBytes(
            ArcanumRuntimeDefaults.Scrying.MaxImageBytes);

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
