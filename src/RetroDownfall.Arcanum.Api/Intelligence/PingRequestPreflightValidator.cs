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
        ArcanumSettings settings) => Validate(request, settings, out _);

    internal static Result Validate(PingRequest request, ArcanumSettings settings, out PingRequestFailureDetails? diagnostic)
    {
        diagnostic = null;

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(settings);

        Result attachedFiles = ValidateAttachedFiles(request, out diagnostic);

        if (attachedFiles.IsFailure)
        {
            return attachedFiles;
        }

        Result bounds = PingRequestBoundsValidator.Validate(request);

        if (bounds.IsFailure)
        {
            diagnostic = PingRequestFailureDetails.FromKnownCode(bounds.Error.Code);

            return bounds;
        }

        Result clientToolChoice = ClientToolCapabilityValidator.ValidateRequest(request, settings);

        if (clientToolChoice.IsFailure)
        {
            diagnostic = PingRequestFailureDetails.FromKnownCode(clientToolChoice.Error.Code);

            return clientToolChoice;
        }

        Result scrying = ValidateScrying(request, settings);

        if (scrying.IsFailure) diagnostic = PingRequestFailureDetails.FromKnownCode(scrying.Error.Code);

        return scrying;
    }

    private static Result ValidateAttachedFiles(PingRequest request, out PingRequestFailureDetails? diagnostic)
    {
        diagnostic = null;

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
                diagnostic = new(PingRequestFailureReason.AttachmentMissing, index);

                return Failure("Attached file entries cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(item.RelativePath))
            {
                diagnostic = new(PingRequestFailureReason.AttachmentPathMissing, index);

                return Failure(
                    "Each attached file must have a non-empty relative path.");
            }

            if (item.RelativePath.Length > maxPathChars)
            {
                diagnostic = new(PingRequestFailureReason.AttachmentPathTooLong, index, item.RelativePath.Length, maxPathChars);

                return Failure(
                    $"Protocol path boundary reached for attached file {index + 1}: measured "
                    + $"{item.RelativePath.Length} characters; limit {maxPathChars}. No attachment work was saved. "
                    + "Shorten the workspace-relative path and retry.");
            }

            string content = item.Content ?? string.Empty;

            long utf8Length = Encoding.UTF8.GetByteCount(content);

            if (utf8Length > maxBytes)
            {
                diagnostic = new(PingRequestFailureReason.AttachmentTooLarge, index, utf8Length, maxBytes);

                return Failure(
                    $"Physical single-request allocation boundary reached for attached file '{item.RelativePath}': "
                    + $"measured {utf8Length} UTF-8 bytes; limit {maxBytes}. No attachment work was saved. "
                    + "Split the content into smaller attached-file chunks and retry.");
            }

            totalUtf8 += utf8Length;

            if (totalUtf8 > maxTotalBytes)
            {
                diagnostic = new(PingRequestFailureReason.AttachmentsTotalTooLarge, index, totalUtf8, maxTotalBytes);

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

/// <summary>Only local preflight classifications and safe numerical facts; no caller strings.</summary>
internal enum PingRequestFailureReason
{
    AttachmentMissing = 1,
    AttachmentPathMissing,
    AttachmentPathTooLong,
    AttachmentTooLarge,
    AttachmentsTotalTooLarge,
    PromptTooLong,
    AdditionalPromptTooLong,
    TooManyMessages,
    TooManyParts,
    MessageTooLong,
    ReasoningEffort,
    ReasoningOutput,
    ReasoningExclusive,
    ReasoningBudget,
    ClientToolsSchema,
    ClientToolsDisabled,
    ClientToolsCount,
    ClientToolsChoice,
    ImagesDisabled,
    ImageData,
    ImageCount,
    ImageMime,
    ImageSize,
    VisionUnsupported,
}

internal sealed record PingRequestFailureDetails(PingRequestFailureReason Reason, int? Index = null, long? Measured = null, long? Limit = null)
{
    internal string? ExpectedCode => Reason switch
    {
        PingRequestFailureReason.AttachmentMissing or PingRequestFailureReason.AttachmentPathMissing
            or PingRequestFailureReason.AttachmentPathTooLong or PingRequestFailureReason.AttachmentTooLarge
            or PingRequestFailureReason.AttachmentsTotalTooLarge => ErrorCodes.Validation.AttachedFiles,
        PingRequestFailureReason.PromptTooLong => "Validation.PromptTooLong",
        PingRequestFailureReason.AdditionalPromptTooLong => "Validation.AdditionalSystemPromptTooLong",
        PingRequestFailureReason.TooManyMessages => "Validation.TooManyStatelessMessages",
        PingRequestFailureReason.TooManyParts => "Validation.TooManyContentParts",
        PingRequestFailureReason.MessageTooLong => "Validation.StatelessMessageTooLong",
        PingRequestFailureReason.ReasoningEffort => ErrorCodes.Validation.InvalidReasoningEffort,
        PingRequestFailureReason.ReasoningOutput => ErrorCodes.Validation.InvalidReasoningOutput,
        PingRequestFailureReason.ReasoningExclusive => ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive,
        PingRequestFailureReason.ReasoningBudget => ErrorCodes.Validation.InvalidReasoningBudget,
        PingRequestFailureReason.ClientToolsSchema => ErrorCodes.ClientTools.InvalidSchema,
        PingRequestFailureReason.ClientToolsDisabled => ErrorCodes.ClientTools.Disabled,
        PingRequestFailureReason.ClientToolsCount => ErrorCodes.ClientTools.TooMany,
        PingRequestFailureReason.ClientToolsChoice => ErrorCodes.ClientTools.ToolChoiceUnavailable,
        PingRequestFailureReason.ImagesDisabled => ErrorCodes.Scrying.FeatureDisabled,
        PingRequestFailureReason.ImageData => ErrorCodes.Scrying.InvalidImageData,
        PingRequestFailureReason.ImageCount => ErrorCodes.Scrying.TooManyImages,
        PingRequestFailureReason.ImageMime => ErrorCodes.Scrying.UnsupportedMimeType,
        PingRequestFailureReason.ImageSize => ErrorCodes.Scrying.ImageTooLarge,
        PingRequestFailureReason.VisionUnsupported => ErrorCodes.Scrying.VisionNotSupported,
        _ => null,
    };

    internal static PingRequestFailureDetails? FromKnownCode(string code) => code switch
    {
        "Validation.PromptTooLong" => new(PingRequestFailureReason.PromptTooLong, Limit: ArcanumSettingClamps.MaxPingPromptChars(ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars)),
        "Validation.AdditionalSystemPromptTooLong" => new(PingRequestFailureReason.AdditionalPromptTooLong, Limit: ArcanumSettingClamps.MaxPingPromptChars(ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars)),
        "Validation.TooManyStatelessMessages" => new(PingRequestFailureReason.TooManyMessages, Limit: ArcanumSettingClamps.MaxStatelessMessages(ArcanumRuntimeDefaults.Intelligence.MaxStatelessMessages)),
        "Validation.TooManyContentParts" => new(PingRequestFailureReason.TooManyParts, Limit: ArcanumSettingClamps.MaxContentPartsPerMessage(ArcanumRuntimeDefaults.Intelligence.MaxContentPartsPerMessage)),
        "Validation.StatelessMessageTooLong" => new(PingRequestFailureReason.MessageTooLong, Limit: ArcanumSettingClamps.MaxEntryContentBytes(ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes)),
        ErrorCodes.Validation.InvalidReasoningEffort => new(PingRequestFailureReason.ReasoningEffort),
        ErrorCodes.Validation.InvalidReasoningOutput => new(PingRequestFailureReason.ReasoningOutput),
        ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive => new(PingRequestFailureReason.ReasoningExclusive),
        ErrorCodes.Validation.InvalidReasoningBudget => new(PingRequestFailureReason.ReasoningBudget),
        ErrorCodes.ClientTools.InvalidSchema => new(PingRequestFailureReason.ClientToolsSchema),
        ErrorCodes.ClientTools.Disabled => new(PingRequestFailureReason.ClientToolsDisabled),
        ErrorCodes.ClientTools.TooMany => new(PingRequestFailureReason.ClientToolsCount),
        ErrorCodes.ClientTools.ToolChoiceUnavailable => new(PingRequestFailureReason.ClientToolsChoice),
        ErrorCodes.Scrying.FeatureDisabled => new(PingRequestFailureReason.ImagesDisabled),
        ErrorCodes.Scrying.InvalidImageData => new(PingRequestFailureReason.ImageData),
        ErrorCodes.Scrying.TooManyImages => new(PingRequestFailureReason.ImageCount),
        ErrorCodes.Scrying.UnsupportedMimeType => new(PingRequestFailureReason.ImageMime),
        ErrorCodes.Scrying.ImageTooLarge => new(PingRequestFailureReason.ImageSize),
        ErrorCodes.Scrying.VisionNotSupported => new(PingRequestFailureReason.VisionUnsupported),
        _ => null,
    };
}
