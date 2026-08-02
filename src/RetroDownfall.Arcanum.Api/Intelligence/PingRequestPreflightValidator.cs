using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class PingRequestPreflightValidator
{

    public static Result Validate(
        PingRequest request,
        ArcanumSettings settings)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(settings);

        Result attachedFiles = ValidateAttachedFiles(request);

        if (attachedFiles.IsFailure)
        {

            return attachedFiles;

        }

        Result bounds = PingRequestBoundsValidator.Validate(
            request,
            settings);

        if (bounds.IsFailure)
        {

            return bounds;

        }

        return ValidateScrying(request, settings);

    }

    private static Result ValidateAttachedFiles(PingRequest request)
    {

        List<AttachedFileDto>? files = request.AttachedFiles;

        if (files is null || files.Count == 0)
        {

            return Result.Success();

        }

        long maxBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);

        int maxFiles = ArcanumSettingClamps.MaxAttachedFilesPerRequest(
            ArcanumRuntimeDefaults.CliMaxAttachedFilesPerRequest);

        int maxPathChars = ArcanumSettingClamps.MaxAttachedFileRelativePathChars(
            ArcanumRuntimeDefaults.CliMaxAttachedFileRelativePathChars);

        if (files.Count > maxFiles)
        {

            return Failure(
                $"At most {maxFiles} attached files are allowed per request.");

        }

        long maxTotalBytes = maxBytes * maxFiles;

        long totalUtf8 = 0;

        for (int index = 0; index < files.Count; index++)
        {

            AttachedFileDto? item = files[index];

            if (item is null)
            {

                return Failure("Attached file entries cannot be null.");

            }

            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {

                return Failure(
                    "Each attached file must have a non-empty relative path.");

            }

            if (item.RelativePath.Length > maxPathChars)
            {

                return Failure("Attached file path is too long.");

            }

            string content = item.Content ?? string.Empty;

            long utf8Length = Encoding.UTF8.GetByteCount(content);

            if (utf8Length > maxBytes)
            {

                return Failure(
                    $"Attached file content exceeds the maximum size ({maxBytes} bytes UTF-8).");

            }

            totalUtf8 += utf8Length;

            if (totalUtf8 > maxTotalBytes)
            {

                return Failure(
                    "Total size of attached files exceeds the allowed limit for this request.");

            }

        }

        return Result.Success();

    }

    private static Result ValidateScrying(
        PingRequest request,
        ArcanumSettings settings)
    {

        if (!ScryingValidator.RequestContainsImages(request))
        {

            return Result.Success();

        }

        Result shape = ScryingValidator.ValidateRequestImages(
            request,
            settings.ResolveScrying());

        if (shape.IsFailure)
        {

            return shape;

        }

        if (ProviderResolver.TryResolveProviderForModel(
                settings,
                request.Model,
                out ProviderSettings? provider,
                out string resolvedModel)
            && provider is not null
            && !ProviderResolver.SupportsVision(provider, resolvedModel))
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Scrying.VisionNotSupported,
                    $"Model '{resolvedModel}' does not support vision. Use a vision-capable model."));

        }

        return Result.Success();

    }

    private static Result Failure(string message) =>
        Result.Failure(
            new Error(
                ErrorCodes.Validation.AttachedFiles,
                message));

}
