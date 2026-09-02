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

        Result bounds = PingRequestBoundsValidator.Validate(request);

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

        int maxPathChars = ArcanumSettingClamps.MaxAttachedFileRelativePathChars(
            ArcanumRuntimeDefaults.CliMaxAttachedFileRelativePathChars);

        long maxTotalBytes = ArcanumRuntimeDefaults.CliMaxAttachedTotalBytes;

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

                return Failure(
                    $"Protocol path boundary reached for attached file {index + 1}: measured "
                    + $"{item.RelativePath.Length} characters; limit {maxPathChars}. No attachment work was saved. "
                    + "Shorten the workspace-relative path and retry.");

            }

            string content = item.Content ?? string.Empty;

            long utf8Length = Encoding.UTF8.GetByteCount(content);

            if (utf8Length > maxBytes)
            {

                return Failure(
                    $"Physical single-request allocation boundary reached for attached file '{item.RelativePath}': "
                    + $"measured {utf8Length} UTF-8 bytes; limit {maxBytes}. No attachment work was saved. "
                    + "Split the content into smaller attached-file chunks and retry.");

            }

            totalUtf8 += utf8Length;

            if (totalUtf8 > maxTotalBytes)
            {

                return Failure(
                    "Physical single-request allocation boundary reached for attached files: "
                    + $"measured {totalUtf8} UTF-8 bytes; limit {maxTotalBytes}. "
                    + "No attachment work was saved. Send the remaining files in a later turn "
                    + "or persist and reference session attachments.");

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
