using System.Text.Json;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class PromptMapping
{

    public static PromptSummaryDto ToSummaryDto(Prompt prompt) =>
        new(
            prompt.Id,
            prompt.CampaignId,
            prompt.Name,
            prompt.Version,
            prompt.Description,
            PromptRepository.DeserializeTags(prompt.Tags),
            prompt.UpdatedAt);

    public static PromptDetailDto ToDetailDto(Prompt prompt) =>
        new(
            prompt.Id,
            prompt.CampaignId,
            prompt.Name,
            prompt.Version,
            prompt.Description,
            PromptRepository.DeserializeTags(prompt.Tags),
            prompt.Template,
            DeserializeJsonDocument(prompt.ParameterSchema),
            DeserializeJsonDocument(prompt.DefaultParameters),
            prompt.Model,
            prompt.Provider,
            prompt.Temperature,
            prompt.TopP,
            prompt.MaxOutputTokens,
            prompt.CreatedAt,
            prompt.UpdatedAt);

    public static PromptExportDto ToExportDto(Prompt prompt) =>
        new(
            prompt.Name,
            prompt.Version,
            prompt.Description,
            PromptRepository.DeserializeTags(prompt.Tags),
            prompt.Template,
            DeserializeJsonDocument(prompt.ParameterSchema),
            DeserializeJsonDocument(prompt.DefaultParameters),
            prompt.Model,
            prompt.Provider,
            prompt.Temperature,
            prompt.TopP,
            prompt.MaxOutputTokens,
            prompt.CampaignId);

    public static JsonDocument? DeserializeJsonDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonDocument.Parse(json);
    }

    public static string? SerializeJsonDocument(JsonDocument? doc) =>
        doc is null ? null : doc.RootElement.GetRawText();

}

internal static class PromptImportHelper
{

    /// <remarks>
    /// Every shape check below returns a <see cref="Result{T}"/> failure rather than throwing.
    /// System.Text.Json does not enforce constructor-parameter nullability, so <c>{}</c> on
    /// <c>POST /api/prompts/import</c> binds a non-null <see cref="PromptImportRequest"/> whose
    /// <c>Payload</c> is null, and a truncated export file binds a payload with a null name,
    /// version or template. Throwing would turn one bad element of a campaign bundle into a
    /// whole-request 500 instead of the per-prompt warning the campaign-import loop expects.
    /// </remarks>
    public static async Task<Result<PromptSummaryDto>> ImportAsync(
        IPromptRepository repo,
        PromptImportRequest request,
        CancellationToken ct)
    {
        PromptExportDto? payload = request.Payload;

        if (payload is null)
        {
            return Result<PromptSummaryDto>.Failure(
                new Error(ErrorCodes.Prompt.InvalidRequest, "Import payload is required."));
        }

        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return Result<PromptSummaryDto>.Failure(
                new Error(ErrorCodes.Prompt.InvalidName, "Import payload must include a prompt name."));
        }

        if (string.IsNullOrWhiteSpace(payload.Version))
        {
            return Result<PromptSummaryDto>.Failure(
                new Error(ErrorCodes.Prompt.InvalidVersion, "Import payload must include a prompt version."));
        }

        if (payload.Template is null)
        {
            return Result<PromptSummaryDto>.Failure(
                new Error(ErrorCodes.Prompt.InvalidRequest, "Import payload must include a prompt template."));
        }

        Prompt? existing = await repo
            .GetByNameAndVersionAsync(payload.Name, payload.Version, request.CampaignId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Result<PromptSummaryDto>.Failure(
                new Error(ErrorCodes.Prompt.DuplicateVersion, "A prompt with this name and version already exists in the target scope."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Prompt prompt = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = request.CampaignId,
            Name = payload.Name.Trim(),
            Version = payload.Version.Trim(),
            Description = payload.Description,
            Tags = PromptRepository.SerializeTags(payload.Tags ?? []),
            Template = payload.Template,
            ParameterSchema = PromptMapping.SerializeJsonDocument(payload.ParameterSchema),
            DefaultParameters = PromptMapping.SerializeJsonDocument(payload.DefaultParameters),
            Model = payload.Model,
            Provider = payload.Provider,
            Temperature = payload.Temperature,
            TopP = payload.TopP,
            MaxOutputTokens = payload.MaxOutputTokens,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repo.AddAsync(prompt, ct).ConfigureAwait(false);

        return Result<PromptSummaryDto>.Success(PromptMapping.ToSummaryDto(prompt));
    }

}
