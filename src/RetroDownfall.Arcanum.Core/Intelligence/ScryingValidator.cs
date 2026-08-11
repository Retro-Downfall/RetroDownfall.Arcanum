using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Scrying — vision/multimodality request-shape validation, shared by the native
/// <c>/api/intelligence/ping(-stream)</c> path (<c>WizardIntelligenceProvider</c>) and the
/// OpenAI-compatible <c>/v1/chat/completions</c> path (validated against the mapped
/// <see cref="PingRequest"/> so both surfaces share one code path). Model capability
/// (<see cref="Configuration.ModelEntry.SupportsVision"/>) is checked separately by the caller via
/// <see cref="ProviderResolver.SupportsVision(ArcanumSettings, string?)"/> once a model is resolved
/// — this class only validates the image payloads themselves (count/MIME/size) and the master
/// <see cref="ScryingSettings.Enabled"/> kill-switch.
/// </summary>
public static class ScryingValidator
{

    private const string DataUriPrefix = "data:";

    /// <summary>
    /// True when the request carries any image content — native <see cref="PingRequest.ScryingFoci"/>
    /// or an <c>image_url</c> <see cref="CoreContentPart"/> on any <see cref="PingRequest.StatelessMessages"/>.
    /// </summary>
    public static bool RequestContainsImages(PingRequest request)
    {

        if (request.ScryingFoci is { Count: > 0 })
        {
            return true;
        }

        if (request.StatelessMessages is not { Count: > 0 } messages)
        {
            return false;
        }

        foreach (CoreChatMessage message in messages)
        {
            if (message.ContentParts is not { Count: > 0 } parts)
            {
                continue;
            }

            foreach (CoreContentPart part in parts)
            {
                if (string.Equals(part.Kind, "image_url", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;

    }

    /// <summary>
    /// Validates every image on the request against <paramref name="scrying"/>: master kill-switch,
    /// per-request image count, and — for <c>data:</c> URI images only (native
    /// <see cref="PingRequest.ScryingFoci"/> and any <c>data:</c>-URI <c>image_url</c> content part) —
    /// MIME allow-list and decoded byte size. A <c>data:</c>-URI image with a missing or blank MIME
    /// type is rejected (<see cref="ErrorCodes.Scrying.UnsupportedMimeType"/>) rather than skipped —
    /// it can satisfy no allow-list entry, and the downstream <c>DataContent</c> mapping requires a
    /// media type. <c>http(s)</c> URL images are counted toward the per-request cap but are not
    /// size/MIME-checked here; the downstream provider fetches and rejects them, avoiding a
    /// HEAD-request side-channel and added latency on this boundary.
    /// Returns success immediately when the request carries no images.
    /// </summary>
    public static Result ValidateRequestImages(PingRequest request, ScryingSettings scrying)
    {

        if (!RequestContainsImages(request))
        {
            return Result.Success();
        }

        if (!scrying.Enabled)
        {
            return Result.Failure(new Error(
                ErrorCodes.Scrying.FeatureDisabled,
                "Scrying is disabled. Enable Arcanum:Features:Scrying to send images."));
        }

        int maxImages = ArcanumSettingClamps.ScryingMaxImagesPerRequest(scrying.MaxImagesPerRequest);

        long maxBytes = ArcanumSettingClamps.ScryingMaxImageBytes(scrying.MaxImageBytes);

        string[] allowedMimeTypes = scrying.AllowedMimeTypes ?? [];

        int imageCount = 0;

        if (request.ScryingFoci is { Count: > 0 } foci)
        {
            foreach (ScryingFocusDto focus in foci)
            {

                imageCount++;

                if (imageCount > maxImages)
                {
                    return TooManyImagesError(maxImages);
                }

                Result mimeResult = ValidateMimeType(focus.MimeType, allowedMimeTypes);

                if (mimeResult.IsFailure)
                {
                    return mimeResult;
                }

                Result sizeResult = ValidateBase64Size(focus.Data, maxBytes);

                if (sizeResult.IsFailure)
                {
                    return sizeResult;
                }

            }
        }

        if (request.StatelessMessages is { Count: > 0 } messages)
        {
            foreach (CoreChatMessage message in messages)
            {
                if (message.ContentParts is not { Count: > 0 } parts)
                {
                    continue;
                }

                foreach (CoreContentPart part in parts)
                {
                    if (!string.Equals(part.Kind, "image_url", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    imageCount++;

                    if (imageCount > maxImages)
                    {
                        return TooManyImagesError(maxImages);
                    }

                    string? url = part.ImageUrl;

                    if (string.IsNullOrWhiteSpace(url)
                        || !url.StartsWith(DataUriPrefix, StringComparison.OrdinalIgnoreCase)
                        || !TryParseDataUri(url, out string mime, out string base64Payload))
                    {
                        // http(s) URL (or a malformed data: URI, left to the provider/mapper to
                        // reject) — not size/MIME-checked here; see ValidateRequestImages summary.
                        continue;
                    }

                    Result mimeResult = ValidateMimeType(mime, allowedMimeTypes);

                    if (mimeResult.IsFailure)
                    {
                        return mimeResult;
                    }

                    Result sizeResult = ValidateBase64Size(base64Payload, maxBytes);

                    if (sizeResult.IsFailure)
                    {
                        return sizeResult;
                    }

                }
            }
        }

        return Result.Success();

    }

    private static Result TooManyImagesError(int maxImages) =>
        Result.Failure(new Error(
            ErrorCodes.Scrying.TooManyImages,
            $"Request exceeds the maximum of {maxImages} images per request."));

    private static Result ValidateMimeType(string? mimeType, string[] allowedMimeTypes)
    {

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return Result.Failure(new Error(
                ErrorCodes.Scrying.UnsupportedMimeType,
                $"Image content must declare a MIME type. Allowed types: {string.Join(", ", allowedMimeTypes)}."));
        }

        foreach (string allowed in allowedMimeTypes)
        {
            if (string.Equals(allowed, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success();
            }
        }

        return Result.Failure(new Error(
            ErrorCodes.Scrying.UnsupportedMimeType,
            $"Image type '{mimeType}' is not supported. Allowed types: {string.Join(", ", allowedMimeTypes)}."));

    }

    private static Result ValidateBase64Size(string? base64Data, long maxBytes)
    {

        long decodedBytes = EstimateDecodedBase64Bytes(base64Data);

        if (decodedBytes > maxBytes)
        {
            return Result.Failure(new Error(
                ErrorCodes.Scrying.ImageTooLarge,
                $"Image exceeds the maximum size of {maxBytes} bytes."));
        }

        // Well-formedness is checked here, after the size cap has bounded the buffer: a decode is
        // the very next thing that happens to this payload, and a caller mistake deserves a Scrying
        // validation error rather than a FormatException escaping the turn as a provider failure.
        if (!string.IsNullOrEmpty(base64Data)
            && !Convert.TryFromBase64String(
                base64Data,
                new byte[Math.Max(decodedBytes, 0L) + 3L],
                out _))
        {
            return Result.Failure(new Error(
                ErrorCodes.Scrying.InvalidImageData,
                "Scrying focus data is not valid base64."));
        }

        return Result.Success();

    }

    /// <summary>
    /// Estimates decoded byte length from a base64 payload's character length (accounting for
    /// <c>=</c> padding) without allocating a decode buffer — sufficient for a size-cap check.
    /// </summary>
    private static long EstimateDecodedBase64Bytes(string? base64)
    {

        if (string.IsNullOrEmpty(base64))
        {
            return 0L;
        }

        int padding = base64.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : base64.EndsWith('=')
                ? 1
                : 0;

        return ((long)base64.Length * 3 / 4) - padding;

    }

    /// <summary>
    /// Parses a <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c> URI. Returns <c>false</c> for any
    /// other shape (missing <c>base64,</c> marker or encoding token) — callers skip size/MIME
    /// validation for those rather than fail the request, leaving them to the provider/mapper. An
    /// empty mime still parses, so <c>data:;base64,…</c> reaches the allow-list and is rejected
    /// there rather than slipping past validation.
    /// </summary>
    private static bool TryParseDataUri(string dataUri, out string mimeType, out string base64Payload)
    {

        mimeType = string.Empty;

        base64Payload = string.Empty;

        ReadOnlySpan<char> body = dataUri.AsSpan(DataUriPrefix.Length);

        int comma = body.IndexOf(',');

        if (comma < 0)
        {
            return false;
        }

        ReadOnlySpan<char> descriptor = body[..comma];

        base64Payload = body[(comma + 1)..].ToString();

        int semicolon = descriptor.IndexOf(';');

        ReadOnlySpan<char> mimeSpan = semicolon >= 0 ? descriptor[..semicolon] : descriptor;

        ReadOnlySpan<char> encoding = semicolon >= 0 ? descriptor[(semicolon + 1)..] : ReadOnlySpan<char>.Empty;

        if (!encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mimeType = mimeSpan.ToString();

        return true;

    }

}
