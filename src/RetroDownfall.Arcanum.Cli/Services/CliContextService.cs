using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Cli.Services;

public enum CliContextScope
{

    All,

    Campaign,

    Workspace,

    Model,

    Session,

}

public sealed record CliContextMutationResult(
    bool IsSuccess,
    string Message,
    CliExitCode ExitCode)
{

    public static CliContextMutationResult Success(string message) =>
        new(true, message, CliExitCode.Success);

    public static CliContextMutationResult Failure(string message) =>
        new(false, message, CliExitCode.ConfigurationError);

}

public sealed record CliContextStatusValue(
    string Value,
    string Source);

public sealed record CliContextStatusPayload(
    CliContextStatusValue Campaign,
    CliContextStatusValue Workspace,
    CliContextStatusValue Model,
    CliContextStatusValue Session,
    string[] Warnings,
    string StateFile);

public interface ICliContextService
{

    Task<CliContextMutationResult> SelectAsync(
        CliContextScope scope,
        string identifier,
        CancellationToken cancellationToken);

    Task<CliContextMutationResult> ClearAsync(
        CliContextScope scope,
        CancellationToken cancellationToken);

    Task<CliContextStatusPayload> GetCurrentAsync(
        bool noContext,
        CancellationToken cancellationToken);

    Task<CliContextValidation> ValidateAsync(
        bool noContext,
        CancellationToken cancellationToken);

}

internal sealed class CliContextService(
    ICliContextStore store,
    ICliContextExclusiveWriter contextWriter,
    ICliResourceCatalog resources,
    ArcanumApiClient apiClient,
    IOptions<ArcanumSettings> settings,
    IArcanumClientMutationBoundary mutationBoundary) : ICliContextService
{

    public async Task<CliContextMutationResult> SelectAsync(
        CliContextScope scope,
        string identifier,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(identifier))
        {

            return CliContextMutationResult.Failure(
                "A context identifier is required.");

        }

        switch (scope)
        {

            case CliContextScope.Campaign:

                ResourceSelectionResult<CampaignDto> campaign = await resources
                    .SelectCampaignAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(campaign, out CampaignDto? campaignValue, out string? campaignError))
                {

                    return SelectionFailure(campaign.Status, campaignError);

                }

                CampaignDto selectedCampaign = campaignValue!;

                return await MutateAsync(
                        (current, token) => RevalidateCampaignSelectionAsync(
                            current,
                            selectedCampaign,
                            token),
                        saved =>
                            $"Using campaign {saved.CampaignName} ({selectedCampaign.Id:D}).",
                        cancellationToken)
                    .ConfigureAwait(false);

            case CliContextScope.Workspace:

                ResourceSelectionResult<WorkspaceInfo> workspace = await resources
                    .SelectWorkspaceAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(workspace, out WorkspaceInfo? workspaceValue, out string? workspaceError))
                {

                    return SelectionFailure(workspace.Status, workspaceError);

                }

                WorkspaceInfo selectedWorkspace = workspaceValue!;

                return await MutateAsync(
                        (current, token) => RevalidateWorkspaceSelectionAsync(
                            current,
                            selectedWorkspace,
                            token),
                        saved =>
                            $"Using workspace {selectedWorkspace.Name} ({saved.WorkspacePath}).",
                        cancellationToken)
                    .ConfigureAwait(false);

            case CliContextScope.Model:

                ResourceSelectionResult<ModelInfoDto> model = await resources
                    .SelectModelAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(model, out ModelInfoDto? modelValue, out string? modelError))
                {

                    return SelectionFailure(model.Status, modelError);

                }

                ModelInfoDto selectedModel = modelValue!;

                return await MutateAsync(
                        (current, token) => RevalidateModelSelectionAsync(
                            current,
                            selectedModel,
                            token),
                        _ => $"Using model {selectedModel.Model}.",
                        cancellationToken)
                    .ConfigureAwait(false);

            case CliContextScope.Session:

                ResourceSelectionResult<SessionSummaryDto> session = await resources
                    .SelectSessionAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(session, out SessionSummaryDto? sessionValue, out string? sessionError))
                {

                    return SelectionFailure(session.Status, sessionError);

                }

                SessionSummaryDto selectedSession = sessionValue!;

                return await MutateAsync(
                        (current, token) => RevalidateSessionSelectionAsync(
                            current,
                            selectedSession,
                            token),
                        saved =>
                        {

                            string mismatch =
                                saved.CampaignId is { } campaignId
                                && selectedSession.CampaignId != campaignId
                                    ? " Warning: the selected session belongs to another campaign."
                                    : string.Empty;

                            return $"Using session {selectedSession.Id:D}.{mismatch}";

                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            default:

                return CliContextMutationResult.Failure(
                    "Select campaign, workspace, model, or session.");

        }

    }

    public Task<CliContextMutationResult> ClearAsync(
        CliContextScope scope,
        CancellationToken cancellationToken)
    {

        string label = scope == CliContextScope.All
            ? "all saved context"
            : $"saved {scope.ToString().ToLowerInvariant()} context";

        return MutateAsync(
            document => scope switch
            {
                CliContextScope.All => CliContextDocument.Empty,
                CliContextScope.Campaign => document with
                {
                    CampaignId = null,
                    CampaignName = null,
                },
                CliContextScope.Workspace => document with
                {
                    WorkspaceId = null,
                    WorkspacePath = null,
                },
                CliContextScope.Model => document with { Model = null },
                CliContextScope.Session => document with { SessionId = null },
                _ => document,
            },
            _ => $"Cleared {label}.",
            cancellationToken);

    }

    private async Task<CliContextMutationResult> MutateAsync(
        Func<CliContextDocument, CliContextDocument> mutation,
        Func<CliContextDocument, string> successMessage,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        return await MutateAsync(
                (current, _) => Task.FromResult(
                    Result<CliContextDocument>.Success(mutation(current))),
                successMessage,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<CliContextMutationResult> MutateAsync(
        Func<
            CliContextDocument,
            CancellationToken,
            Task<Result<CliContextDocument>>> prepareAsync,
        Func<CliContextDocument, string> successMessage,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(prepareAsync);

        ArgumentNullException.ThrowIfNull(successMessage);

        try
        {

            ArcanumClientMutationResult<Result<CliContextDocument>> result =
                await mutationBoundary
                    .RunAsync(
                        async token =>
                        {

                            Result<CliContextDocument> prepared =
                                await prepareAsync(store.Load(), token)
                                    .ConfigureAwait(false);

                            if (prepared.IsFailure)
                            {

                                return prepared;

                            }

                            contextWriter.SaveUnderExclusive(prepared.Value);

                            return prepared;

                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!result.IsCompleted)
            {

                return CliContextMutationResult.Failure(result.Error.Message);

            }

            return result.Value.IsSuccess
                ? CliContextMutationResult.Success(
                    successMessage(result.Value.Value))
                : CliContextMutationResult.Failure(
                    result.Value.Error.Message);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return CliContextMutationResult.Failure(
                "The saved CLI context could not be changed safely.");

        }

    }

    private async Task<Result<CliContextDocument>> RevalidateCampaignSelectionAsync(
        CliContextDocument current,
        CampaignDto selected,
        CancellationToken cancellationToken)
    {

        (bool loaded, CampaignDto[] campaigns) = await GetCampaignsAsync(
                cancellationToken)
            .ConfigureAwait(false);

        if (!loaded)
        {

            return RevalidationUnavailable("campaign");

        }

        CampaignDto? refreshed = campaigns.FirstOrDefault(
            candidate => candidate.Id == selected.Id
                && candidate.CreatedAt == selected.CreatedAt);

        return refreshed is null
            ? RevalidationMissing("campaign", selected.Id.ToString("D"))
            : Result<CliContextDocument>.Success(
                current with
                {
                    CampaignId = refreshed.Id,
                    CampaignName = refreshed.Name,
                });

    }

    private async Task<Result<CliContextDocument>> RevalidateWorkspaceSelectionAsync(
        CliContextDocument current,
        WorkspaceInfo selected,
        CancellationToken cancellationToken)
    {

        Result<WorkspaceInfo[]> workspaces = await apiClient
            .GetWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (workspaces.IsFailure)
        {

            return Result<CliContextDocument>.Failure(workspaces.Error);

        }

        WorkspaceInfo? refreshed = workspaces.Value.FirstOrDefault(
            candidate => string.Equals(
                    candidate.Id,
                    selected.Id,
                    StringComparison.Ordinal)
                && candidate.RegisteredAt == selected.RegisteredAt);

        return refreshed is null
            ? RevalidationMissing("workspace", selected.Id)
            : Result<CliContextDocument>.Success(
                current with
                {
                    WorkspaceId = refreshed.Id,
                    WorkspacePath = refreshed.Path.Trim(),
                });

    }

    private async Task<Result<CliContextDocument>> RevalidateModelSelectionAsync(
        CliContextDocument current,
        ModelInfoDto selected,
        CancellationToken cancellationToken)
    {

        Result<ModelInfoDto[]> models = await apiClient
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (models.IsFailure)
        {

            return Result<CliContextDocument>.Failure(models.Error);

        }

        ModelInfoDto? refreshed = models.Value.FirstOrDefault(
            candidate => string.Equals(
                    candidate.Model,
                    selected.Model,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidate.ProviderName,
                    selected.ProviderName,
                    StringComparison.OrdinalIgnoreCase));

        return refreshed is null
            ? RevalidationMissing("model", selected.Model)
            : Result<CliContextDocument>.Success(
                current with { Model = refreshed.Model });

    }

    private async Task<Result<CliContextDocument>> RevalidateSessionSelectionAsync(
        CliContextDocument current,
        SessionSummaryDto selected,
        CancellationToken cancellationToken)
    {

        Result<SessionDetailDto> session = await apiClient
            .GetSessionAsync(selected.Id, cancellationToken)
            .ConfigureAwait(false);

        if (session.IsFailure)
        {

            return Result<CliContextDocument>.Failure(session.Error);

        }

        SessionDetailDto refreshed = session.Value;

        if (refreshed.Id != selected.Id
            || refreshed.CampaignId != selected.CampaignId
            || refreshed.CreatedAt != selected.CreatedAt)
        {

            return RevalidationMissing(
                "session",
                selected.Id.ToString("D"));

        }

        return Result<CliContextDocument>.Success(
            current with { SessionId = refreshed.Id });

    }

    private static Result<CliContextDocument> RevalidationUnavailable(
        string resourceKind) =>
        Result<CliContextDocument>.Failure(
            new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                $"The selected {resourceKind} could not be revalidated on the current host. Retry the selection."));

    private static Result<CliContextDocument> RevalidationMissing(
        string resourceKind,
        string identifier) =>
        Result<CliContextDocument>.Failure(
            new Error(
                ErrorCodes.Data.NotFound,
                $"The selected {resourceKind} {identifier} is no longer available on the current host. Retry the selection."));

    public async Task<CliContextStatusPayload> GetCurrentAsync(
        bool noContext,
        CancellationToken cancellationToken)
    {

        CliContextValidation validation = await ValidateAsync(
            noContext,
            cancellationToken).ConfigureAwait(false);

        CliEffectiveContext effective = CliContextPrecedence.Resolve(
            new CliContextResolutionRequest(
                null,
                null,
                null,
                null,
                validation.Active,
                validation.DetectedCampaign?.Id,
                validation.DetectedCampaign?.Path,
                validation.DetectedWorkspace?.Path,
                settings.Value.DefaultModel,
                noContext));

        List<string> warnings = [.. validation.Warnings];

        AddRelationshipWarnings(effective, validation.Session, warnings);

        return new CliContextStatusPayload(
            StatusValue(
                effective.Campaign.Value is { } campaignId
                    ? CampaignLabel(campaignId, validation)
                    : "-",
                effective.Campaign.Source),
            StatusValue(effective.Workspace.Value ?? "-", effective.Workspace.Source),
            StatusValue(effective.Model.Value ?? "-", effective.Model.Source),
            StatusValue(
                effective.Session.Value?.ToString("D") ?? "-",
                effective.Session.Source),
            [.. warnings],
            store.FilePath);

    }

    public async Task<CliContextValidation> ValidateAsync(
        bool noContext,
        CancellationToken cancellationToken)
    {

        CliContextDocument persisted = noContext
            ? CliContextDocument.Empty
            : store.Load();

        CliContextDocument active = persisted;

        List<string> warnings = [];

        bool staleCampaign = false;

        bool staleWorkspace = false;

        bool staleModel = false;

        bool staleSession = false;

        (bool campaignsLoaded, CampaignDto[] campaigns) = await GetCampaignsAsync(cancellationToken)
            .ConfigureAwait(false);

        CampaignDto? activeCampaign = null;

        if (active.CampaignId is { } activeCampaignId)
        {

            activeCampaign = campaigns.FirstOrDefault(
                item => item.Id == activeCampaignId);

            if (campaignsLoaded && activeCampaign is null)
            {

                staleCampaign = true;

                active = active with
                {
                    CampaignId = null,
                    CampaignName = null,
                };

            }

        }

        WorkspaceInfo? activeWorkspace = null;

        Result<WorkspaceInfo[]> workspaces = await apiClient
            .GetWorkspacesAsync(cancellationToken)
            .ConfigureAwait(false);

        if (active.WorkspaceId is not null && workspaces.IsSuccess)
        {

            activeWorkspace = workspaces.Value.FirstOrDefault(
                item => string.Equals(
                    item.Id,
                    active.WorkspaceId,
                    StringComparison.Ordinal));

            if (activeWorkspace is null)
            {

                staleWorkspace = true;

                active = active with
                {
                    WorkspaceId = null,
                    WorkspacePath = null,
                };

            }

        }

        Result<ModelInfoDto[]> models = await apiClient
            .GetModelsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (active.Model is not null
            && models.IsSuccess
            && !models.Value.Any(
                item => string.Equals(
                    item.Model,
                    active.Model,
                    StringComparison.OrdinalIgnoreCase)))
        {

            staleModel = true;

            active = active with { Model = null };

        }

        SessionDetailDto? session = null;

        if (active.SessionId is { } sessionId)
        {

            Result<SessionDetailDto> sessionResult = await apiClient
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (sessionResult.IsSuccess)
            {

                session = sessionResult.Value;

            }
            else if (IsNotFound(sessionResult.Error))
            {

                staleSession = true;

                active = active with { SessionId = null };

            }

        }
        if (!noContext
            && (staleCampaign || staleWorkspace || staleModel || staleSession))
        {

            try
            {

                ArcanumClientMutationResult<Result<StaleCleanupOutcome>> cleanup =
                    await mutationBoundary
                        .RunAsync(
                            token => RevalidateStaleCleanupAsync(
                                persisted,
                                staleCampaign,
                                staleWorkspace,
                                staleModel,
                                staleSession,
                                token),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (cleanup.IsCompleted && cleanup.Value.IsSuccess)
                {

                    StaleCleanupOutcome outcome = cleanup.Value.Value;

                    active = outcome.Document;

                    if (outcome.Campaigns is not null)
                    {

                        campaigns = outcome.Campaigns;

                        activeCampaign = active.CampaignId is { } campaignId
                            ? campaigns.FirstOrDefault(
                                candidate => candidate.Id == campaignId)
                            : null;

                    }

                    if (outcome.Workspaces is not null)
                    {

                        workspaces = Result<WorkspaceInfo[]>.Success(
                            outcome.Workspaces);

                        activeWorkspace = active.WorkspaceId is { } workspaceId
                            ? outcome.Workspaces.FirstOrDefault(
                                candidate => string.Equals(
                                    candidate.Id,
                                    workspaceId,
                                    StringComparison.Ordinal))
                            : null;

                    }

                    if (outcome.SessionWasRevalidated)
                    {

                        session = outcome.Session;

                    }

                    AddStaleCleanupWarnings(
                        warnings,
                        persisted,
                        outcome.CampaignCleared,
                        outcome.WorkspaceCleared,
                        outcome.ModelCleared,
                        outcome.SessionCleared,
                        error: null);

                }
                else
                {

                    AddStaleCleanupWarnings(
                        warnings,
                        persisted,
                        staleCampaign,
                        staleWorkspace,
                        staleModel,
                        staleSession,
                        cleanup.IsCompleted
                            ? cleanup.Value.Error.Message
                            : cleanup.Error.Message);

                }

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                AddStaleCleanupWarnings(
                    warnings,
                    persisted,
                    staleCampaign,
                    staleWorkspace,
                    staleModel,
                    staleSession,
                    "the saved CLI context could not be changed safely");

            }

        }

        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);

        CampaignDto? detectedCampaign = campaigns
            .Where(item => IsWithin(currentDirectory, item.Path))
            .OrderByDescending(item => FullPathLength(item.Path))
            .FirstOrDefault();

        WorkspaceInfo? detectedWorkspace = workspaces.IsSuccess
            ? workspaces.Value
                .Where(item => IsWithin(currentDirectory, item.Path))
                .OrderByDescending(item => FullPathLength(item.Path))
                .FirstOrDefault()
            : null;

        return new CliContextValidation(
            active,
            activeCampaign,
            activeWorkspace,
            session,
            detectedCampaign,
            detectedWorkspace,
            [.. campaigns],
            [.. warnings]);

    }

    private async Task<Result<StaleCleanupOutcome>> RevalidateStaleCleanupAsync(
        CliContextDocument persisted,
        bool staleCampaign,
        bool staleWorkspace,
        bool staleModel,
        bool staleSession,
        CancellationToken cancellationToken)
    {

        CliContextDocument current = store.Load();

        CliContextDocument updated = current;

        CampaignDto[]? refreshedCampaigns = null;

        WorkspaceInfo[]? refreshedWorkspaces = null;

        SessionDetailDto? refreshedSession = null;

        bool sessionWasRevalidated = false;

        bool campaignCleared = false;

        bool workspaceCleared = false;

        bool modelCleared = false;

        bool sessionCleared = false;

        if (staleCampaign
            && current.CampaignId == persisted.CampaignId)
        {

            (bool loaded, CampaignDto[] campaigns) = await GetCampaignsAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            if (!loaded)
            {

                return StaleCleanupUnavailable("campaign");

            }

            refreshedCampaigns = campaigns;

            CampaignDto? refreshed = campaigns.FirstOrDefault(
                candidate => candidate.Id == current.CampaignId);

            if (refreshed is null)
            {

                updated = updated with
                {
                    CampaignId = null,
                    CampaignName = null,
                };

                campaignCleared = true;

            }
            else
            {

                updated = updated with { CampaignName = refreshed.Name };

            }

        }

        if (staleWorkspace
            && string.Equals(
                current.WorkspaceId,
                persisted.WorkspaceId,
                StringComparison.Ordinal))
        {

            Result<WorkspaceInfo[]> workspaces = await apiClient
                .GetWorkspacesAsync(cancellationToken)
                .ConfigureAwait(false);

            if (workspaces.IsFailure)
            {

                return Result<StaleCleanupOutcome>.Failure(workspaces.Error);

            }

            refreshedWorkspaces = workspaces.Value;

            WorkspaceInfo? refreshed = workspaces.Value.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Id,
                    current.WorkspaceId,
                    StringComparison.Ordinal));

            if (refreshed is null)
            {

                updated = updated with
                {
                    WorkspaceId = null,
                    WorkspacePath = null,
                };

                workspaceCleared = true;

            }
            else
            {

                updated = updated with
                {
                    WorkspacePath = refreshed.Path.Trim(),
                };

            }

        }

        if (staleModel
            && string.Equals(
                current.Model,
                persisted.Model,
                StringComparison.Ordinal))
        {

            Result<ModelInfoDto[]> models = await apiClient
                .GetModelsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (models.IsFailure)
            {

                return Result<StaleCleanupOutcome>.Failure(models.Error);

            }

            ModelInfoDto? refreshed = models.Value.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Model,
                    current.Model,
                    StringComparison.OrdinalIgnoreCase));

            if (refreshed is null)
            {

                updated = updated with { Model = null };

                modelCleared = true;

            }
            else
            {

                updated = updated with { Model = refreshed.Model };

            }

        }

        if (staleSession
            && current.SessionId == persisted.SessionId
            && current.SessionId is { } sessionId)
        {

            Result<SessionDetailDto> sessionResult = await apiClient
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            sessionWasRevalidated = true;

            if (sessionResult.IsSuccess)
            {

                refreshedSession = sessionResult.Value;

            }
            else if (IsNotFound(sessionResult.Error))
            {

                updated = updated with { SessionId = null };

                sessionCleared = true;

            }
            else
            {

                return Result<StaleCleanupOutcome>.Failure(
                    sessionResult.Error);

            }

        }

        if (updated != current)
        {

            contextWriter.SaveUnderExclusive(updated);

        }

        return Result<StaleCleanupOutcome>.Success(
            new StaleCleanupOutcome(
                updated,
                refreshedCampaigns,
                refreshedWorkspaces,
                refreshedSession,
                sessionWasRevalidated,
                campaignCleared,
                workspaceCleared,
                modelCleared,
                sessionCleared));

    }

    private static Result<StaleCleanupOutcome> StaleCleanupUnavailable(
        string resourceKind) =>
        Result<StaleCleanupOutcome>.Failure(
            new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                $"The saved {resourceKind} could not be revalidated on the current host."));

    private sealed record StaleCleanupOutcome(
        CliContextDocument Document,
        CampaignDto[]? Campaigns,
        WorkspaceInfo[]? Workspaces,
        SessionDetailDto? Session,
        bool SessionWasRevalidated,
        bool CampaignCleared,
        bool WorkspaceCleared,
        bool ModelCleared,
        bool SessionCleared);

    private static void AddStaleCleanupWarnings(
        List<string> warnings,
        CliContextDocument persisted,
        bool staleCampaign,
        bool staleWorkspace,
        bool staleModel,
        bool staleSession,
        string? error)
    {

        string suffix = error is null
            ? " and was cleared."
            : $", but could not be cleared from saved context: {error}";

        if (staleCampaign && persisted.CampaignId is { } campaignId)
        {

            warnings.Add($"Saved campaign {campaignId:D} is stale{suffix}");

        }

        if (staleWorkspace && persisted.WorkspaceId is { } workspaceId)
        {

            warnings.Add($"Saved workspace {workspaceId} is stale{suffix}");

        }

        if (staleModel && persisted.Model is { } model)
        {

            warnings.Add(
                error is null
                    ? $"Saved model {model} is no longer configured and was cleared."
                    : $"Saved model {model} is no longer configured{suffix}");

        }

        if (staleSession && persisted.SessionId is { } sessionId)
        {

            warnings.Add($"Saved session {sessionId:D} is stale{suffix}");

        }

    }

    private async Task<(bool IsSuccess, CampaignDto[] Items)> GetCampaignsAsync(
        CancellationToken cancellationToken)
    {

        List<CampaignDto> campaigns = [];

        int offset = 0;

        while (true)
        {

            Result<ListPageResult<CampaignDto>> result = await apiClient
                .GetCampaignsPageAsync(
                    null,
                    100,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {

                return (false, []);

            }

            campaigns.AddRange(result.Value.Items);

            if (!result.Value.HasMore
                || result.Value.NextOffset is not { } nextOffset)
            {

                return (true, [.. campaigns]);

            }

            if (nextOffset <= offset)
            {

                // A cursor that does not advance would loop forever while the accumulator grows without
                // bound. ArcanumApiClient.ListLoreAsync refuses the same shape; here the caller already
                // degrades gracefully when campaigns cannot be listed.
                return (false, []);

            }

            offset = nextOffset;

        }

    }

    private void AddRelationshipWarnings(
        CliEffectiveContext effective,
        SessionDetailDto? session,
        List<string> warnings)
    {

        if (effective.Workspace is
            {
                Source: CliContextSource.ActiveContext,
                Value: { } workspace,
            }
            && !IsWithin(Environment.CurrentDirectory, workspace))
        {

            warnings.Add(
                $"Current directory is outside the selected workspace {workspace}.");

        }

        if (session is not null
            && effective.Campaign.Value is { } campaignId
            && session.CampaignId != campaignId)
        {

            warnings.Add(
                $"Selected session {session.Id:D} belongs to another campaign.");

        }

    }

    private static string CampaignLabel(
        Guid campaignId,
        CliContextValidation validation)
    {

        CampaignDto? campaign = validation.Campaigns.FirstOrDefault(
            item => item.Id == campaignId);

        return campaign is null
            ? campaignId.ToString("D")
            : $"{campaign.Name} ({campaign.Id:D})";

    }

    private static CliContextStatusValue StatusValue(
        string value,
        CliContextSource source) =>
        new(value, SourceLabel(source));

    internal static string SourceLabel(CliContextSource source) =>
        source switch
        {
            CliContextSource.ExplicitOption => "explicit option",
            CliContextSource.ActiveContext => "active context",
            CliContextSource.CurrentDirectory => "current directory",
            _ => "server default",
        };

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

    private static CliContextMutationResult SelectionFailure(
        ResourceSelectionStatus status,
        string? error) =>
        status == ResourceSelectionStatus.Cancelled
            ? CliContextMutationResult.Success("Selection cancelled.")
            : CliContextMutationResult.Failure(
                error ?? "The resource could not be selected.");

    private static bool IsNotFound(Error? error) =>
        error is not null
        && error.Value.Code.EndsWith(
            ".NotFound",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsWithin(string candidatePath, string rootPath)
    {

        try
        {

            string candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidatePath));

            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(rootPath));

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(candidate, root, comparison)
                || candidate.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    comparison);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

    }

    private static int FullPathLength(string path) =>
        TryNormalizePath(path, out string? normalized)
            ? normalized!.Length
            : -1;

    internal static bool TryNormalizePath(
        string path,
        out string? normalized)
    {

        try
        {

            normalized = Path.GetFullPath(path);

            return true;

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            normalized = null;

            return false;

        }

    }

}

public sealed record CliContextValidation(
    CliContextDocument Active,
    CampaignDto? ActiveCampaign,
    WorkspaceInfo? ActiveWorkspace,
    SessionDetailDto? Session,
    CampaignDto? DetectedCampaign,
    WorkspaceInfo? DetectedWorkspace,
    CampaignDto[] Campaigns,
    string[] Warnings);
