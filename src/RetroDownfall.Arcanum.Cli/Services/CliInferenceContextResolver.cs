using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed record CliInferenceContextRequest(
    string? Campaign,
    string? Workspace,
    string? Model,
    string? Session,
    string CurrentDirectory,
    bool NoContext,
    bool NewSession,
    bool SessionPicker = false);

public sealed record CliInferenceContextResult(
    bool IsSuccess,
    bool IsCancelled,
    CliEffectiveContext? Context,
    string[] Warnings,
    string? Error)
{

    public static CliInferenceContextResult Success(
        CliEffectiveContext context,
        IEnumerable<string> warnings) =>
        new(true, false, context, [.. warnings], null);

    public static CliInferenceContextResult Failure(string error) =>
        new(false, false, null, [], error);

    public static CliInferenceContextResult Cancelled() =>
        new(false, true, null, [], null);

}

public interface ICliInferenceContextResolver
{

    Task<CliInferenceContextResult> ResolveAsync(
        CliInferenceContextRequest request,
        CancellationToken cancellationToken);

}

public sealed class CliInferenceContextResolver(
    ICliContextService contextService,
    ICliResourceCatalog resources,
    IOptions<ArcanumSettings> settings) : ICliInferenceContextResolver
{

    public async Task<CliInferenceContextResult> ResolveAsync(
        CliInferenceContextRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        CliContextValidation validation = await contextService
            .ValidateAsync(request.NoContext, cancellationToken)
            .ConfigureAwait(false);

        Guid? explicitCampaignId = null;

        string? explicitWorkspacePath = null;

        string? explicitModel = null;

        Guid? explicitSessionId = null;

        SessionSummaryDto? explicitSession = null;

        if (!string.IsNullOrWhiteSpace(request.Campaign))
        {

            ResourceSelectionResult<CampaignDto> campaign = await resources
                .SelectCampaignAsync(request.Campaign, cancellationToken)
                .ConfigureAwait(false);

            if (!TrySelected(campaign, out CampaignDto? selected, out string? error))
            {

                return SelectionFailure(campaign.Status, error);

            }

            explicitCampaignId = selected!.Id;

        }

        if (!string.IsNullOrWhiteSpace(request.Workspace))
        {

            ResourceSelectionResult<WorkspaceInfo> workspace = await resources
                .SelectWorkspaceAsync(request.Workspace, cancellationToken)
                .ConfigureAwait(false);

            if (!TrySelected(workspace, out WorkspaceInfo? selected, out string? error))
            {

                return SelectionFailure(workspace.Status, error);

            }

            explicitWorkspacePath = selected!.Path.Trim();

        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {

            ResourceSelectionResult<ModelInfoDto> model = await resources
                .SelectModelAsync(request.Model, cancellationToken)
                .ConfigureAwait(false);

            if (TrySelected(model, out ModelInfoDto? selected, out string? error))
            {

                explicitModel = selected!.Model;

            }
            else if (model.Status == ResourceSelectionStatus.Cancelled)
            {

                return SelectionFailure(model.Status, error);

            }
            else
            {

                // A name the listing does not contain is not necessarily wrong. The listing omits
                // models the operator hid, and a Familiar's catalogue belongs to the vendor rather
                // than to arcanum.json — so gating here would refuse exactly the two cases the
                // hide-list and pass-through rules exist to allow. The host is the authority: it
                // resolves the name, or answers Hub.Model with the reason it could not.
                explicitModel = request.Model.Trim();

            }

        }

        // A bare --resume asks for the picker explicitly, which is not the same as an absent
        // selector: absent means "fall through to saved context", picker means "let me choose now".
        if (request.SessionPicker
            || !string.IsNullOrWhiteSpace(request.Session))
        {

            ResourceSelectionResult<SessionSummaryDto> session = await resources
                .SelectSessionAsync(
                    request.SessionPicker ? null : request.Session,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!TrySelected(session, out explicitSession, out string? error))
            {

                return SelectionFailure(session.Status, error);

            }

            explicitSessionId = explicitSession!.Id;

        }

        CliContextDocument active = request.NewSession
            ? validation.Active with { SessionId = null }
            : validation.Active;

        CliEffectiveContext effective = CliContextPrecedence.Resolve(
            new CliContextResolutionRequest(
                explicitCampaignId,
                explicitWorkspacePath,
                explicitModel,
                request.NewSession ? null : explicitSessionId,
                active,
                validation.DetectedCampaign?.Id,
                validation.DetectedCampaign?.Path,
                validation.DetectedWorkspace?.Path,
                settings.Value.DefaultModel,
                request.NoContext));

        List<string> warnings = [.. validation.Warnings];

        if (effective.Workspace is
            {
                Source: CliContextSource.ActiveContext,
                Value: { } workspacePath,
            }
            && !CliContextService.IsWithin(
                request.CurrentDirectory,
                workspacePath))
        {

            warnings.Add(
                $"Current directory is outside the selected workspace {workspacePath}.");

        }

        Guid? sessionCampaignId = explicitSession?.CampaignId
            ?? validation.Session?.CampaignId;

        if (effective.Session.Value is not null
            && effective.Campaign.Value is { } campaignId
            && sessionCampaignId != campaignId)
        {

            warnings.Add(
                "The selected session belongs to another campaign; start a new session or align the campaign before operating.");

            return CliInferenceContextResult.Failure(
                string.Join(' ', warnings));

        }

        return CliInferenceContextResult.Success(effective, warnings);

    }

    private static bool TrySelected<T>(
        ResourceSelectionResult<T> result,
        out T? value,
        out string? error)
        where T : class
    {

        value = result.Value;

        error = result.Status switch
        {
            ResourceSelectionStatus.Cancelled => "Selection cancelled.",
            ResourceSelectionStatus.Error => result.Error,
            _ => null,
        };

        return result.Status == ResourceSelectionStatus.Selected
            && value is not null;

    }

    private static CliInferenceContextResult SelectionFailure(
        ResourceSelectionStatus status,
        string? error) =>
        status == ResourceSelectionStatus.Cancelled
            ? CliInferenceContextResult.Cancelled()
            : CliInferenceContextResult.Failure(
                error ?? "The resource could not be selected.");

}
