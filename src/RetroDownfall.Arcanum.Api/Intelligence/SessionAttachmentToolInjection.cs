using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Builds multimodal <see cref="AIContent"/> for a successful <c>attach_session_file</c> so the
/// next inference round receives <see cref="TextContent"/> / <see cref="DataContent"/> explicitly
/// (not only as tool-result text).
/// </summary>
public static class SessionAttachmentToolInjection
{

    public static async Task<IReadOnlyList<AIContent>?> TryBuildContentsAsync(
        ISessionAttachmentStore store,
        Guid sessionId,
        string logicalName,
        int? version,
        long maxTextBytes,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return null;
        }

        SessionAttachmentRecord? record = await store
            .GetByLogicalAsync(sessionId, logicalName.Trim(), version, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        ReadOnlyMemory<byte> bytes = await store
            .ReadBytesAsync(record, cancellationToken)
            .ConfigureAwait(false);

        if (record.Kind == SessionAttachmentKind.Image)
        {
            string mime = string.IsNullOrWhiteSpace(record.MimeType) ? "image/*" : record.MimeType;

            return [new DataContent(bytes, mime)];
        }

        string text = DecodeTextWithByteBound(bytes, maxTextBytes);

        return [new TextContent(text)];

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
            span = SessionAttachmentTurnService.TruncateUtf8ToRuneBoundary(span, limit);
        }

        return Encoding.UTF8.GetString(span);

    }

}
