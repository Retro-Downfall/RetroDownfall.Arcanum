using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Storage;

public static class SessionAttachmentContentPolicy

{

    public static long ResolveMaximumReadBytes(ArcanumSettings settings)

    {

        ArgumentNullException.ThrowIfNull(settings);

        long textMaximum = ArcanumSettingClamps.MaxAttachFileSizeBytes(

            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

        long imageMaximum = ArcanumSettingClamps.ScryingMaxImageBytes(

            settings.ResolveScrying().MaxImageBytes);

        return Math.Max(textMaximum, imageMaximum);

    }

    public static SessionAttachmentKind Classify(string mimeType)

    {

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))

        {

            return SessionAttachmentKind.Image;

        }

        return IsTextualMimeType(mimeType)

            ? SessionAttachmentKind.Text

            : SessionAttachmentKind.Binary;

    }

    public static string? Validate(

        SessionAttachmentKind kind,

        ReadOnlyMemory<byte> bytes,

        string mimeType,

        ArcanumSettings settings)

    {

        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(mimeType))

        {

            return "Attachment content type is required.";

        }

        long maximumBytes = kind == SessionAttachmentKind.Image

            ? ArcanumSettingClamps.ScryingMaxImageBytes(settings.ResolveScrying().MaxImageBytes)

            : ArcanumSettingClamps.MaxAttachFileSizeBytes(

                ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

        if (bytes.Length > maximumBytes)

        {

            return $"Attachment content exceeds the {maximumBytes}-byte limit.";

        }

        if (kind == SessionAttachmentKind.Image)

        {

            ScryingSettings scrying = settings.ResolveScrying();

            if (!scrying.Enabled)

            {

                return "Scrying is disabled, so image attachments are unavailable.";

            }

            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))

            {

                return "Attachment content is not a supported image type.";

            }

            string[] allowedImages = scrying.AllowedMimeTypes ?? [];

            return allowedImages.Length == 0

                || allowedImages.Contains(mimeType, StringComparer.OrdinalIgnoreCase)

                    ? null

                    : $"Image type '{mimeType}' is not permitted by Scrying policy.";

        }

        string[] allowedUploads = settings.ResolveFiles().AllowedMimeTypes ?? [];

        if (allowedUploads.Length > 0

            && !allowedUploads.Contains(mimeType, StringComparer.OrdinalIgnoreCase))

        {

            return $"Content type '{mimeType}' is not permitted by upload MIME policy.";

        }

        if (kind == SessionAttachmentKind.Binary)

        {

            return IsTextualMimeType(mimeType)

                ? "Attachment kind does not match its textual content type."

                : null;

        }

        if (kind != SessionAttachmentKind.Text || !IsTextualMimeType(mimeType))

        {

            return "Attachment kind does not match its content type.";

        }

        try

        {

            string decoded = new UTF8Encoding(false, true).GetString(bytes.Span);

            return decoded.Contains('\0', StringComparison.Ordinal)

                ? "Textual attachment content contains NUL bytes."

                : null;

        }
        catch (DecoderFallbackException)

        {

            return "Textual attachment content is not valid UTF-8.";

        }

    }

    private static bool IsTextualMimeType(string mimeType) =>

        mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)

        || mimeType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)

        || mimeType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)

        || mimeType is "application/json"

            or "application/xml"

            or "application/yaml"

            or "application/x-yaml"

            or "application/toml"

            or "application/x-ndjson"

            or "application/jsonl"

            or "application/javascript"

            or "application/x-javascript"

            or "application/x-sh";

}
