using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

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

    CliContextMutationResult Clear(CliContextScope scope);

    Task<CliContextStatusPayload> GetCurrentAsync(
        bool noContext,
        CancellationToken cancellationToken);

}

public sealed class CliContextService(
    ICliContextStore store,
    ICliResourceCatalog resources,
    ArcanumApiClient apiClient,
    IOptions<ArcanumSettings> settings,
    CliSessionManager sessionManager) : ICliContextService
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

        CliContextDocument document = store.Load();

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

                document = document with
                {
                    CampaignId = campaignValue!.Id,
                    CampaignName = campaignValue.Name,
                };

                store.Save(document);

                return CliContextMutationResult.Success(
                    $"Using campaign {campaignValue.Name} ({campaignValue.Id:D}).");

            case CliContextScope.Workspace:

                ResourceSelectionResult<WorkspaceInfo> workspace = await resources
                    .SelectWorkspaceAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(workspace, out WorkspaceInfo? workspaceValue, out string? workspaceError))
                {

                    return SelectionFailure(workspace.Status, workspaceError);

                }

                WorkspaceInfo selectedWorkspace = workspaceValue!;

                document = document with
                {
                    WorkspaceId = selectedWorkspace.Id,
                    WorkspacePath = selectedWorkspace.Path.Trim(),
                };

                store.Save(document);

                return CliContextMutationResult.Success(
                    $"Using workspace {selectedWorkspace.Name} ({selectedWorkspace.Path}).");

            case CliContextScope.Model:

                ResourceSelectionResult<ModelInfoDto> model = await resources
                    .SelectModelAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(model, out ModelInfoDto? modelValue, out string? modelError))
                {

                    return SelectionFailure(model.Status, modelError);

                }

                document = document with { Model = modelValue!.Model };

                store.Save(document);

                return CliContextMutationResult.Success(
                    $"Using model {modelValue.Model}.");

            case CliContextScope.Session:

                ResourceSelectionResult<SessionSummaryDto> session = await resources
                    .SelectSessionAsync(identifier, cancellationToken)
                    .ConfigureAwait(false);

                if (!TrySelected(session, out SessionSummaryDto? sessionValue, out string? sessionError))
                {

                    return SelectionFailure(session.Status, sessionError);

                }

                document = document with { SessionId = sessionValue!.Id };

                store.Save(document);

                sessionManager.SaveSessionId(sessionValue.Id, quiet: true);

                string mismatch =
                    document.CampaignId is { } campaignId
                    && sessionValue.CampaignId != campaignId
                        ? " Warning: the selected session belongs to another campaign."
                        : string.Empty;

                return CliContextMutationResult.Success(
                    $"Using session {sessionValue.Id:D}.{mismatch}");

            default:

                return CliContextMutationResult.Failure(
                    "Select campaign, workspace, model, or session.");

        }

    }

    public CliContextMutationResult Clear(CliContextScope scope)
    {

        CliContextDocument document = store.Load();

        document = scope switch
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
        };

        store.Save(document);

        if (scope is CliContextScope.All or CliContextScope.Session)
        {

            sessionManager.ClearSession(quiet: true);

        }

        string label = scope == CliContextScope.All
            ? "all saved context"
            : $"saved {scope.ToString().ToLowerInvariant()} context";

        return CliContextMutationResult.Success($"Cleared {label}.");

    }

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

    internal async Task<CliContextValidation> ValidateAsync(
        bool noContext,
        CancellationToken cancellationToken)
    {

        CliContextDocument active = noContext
            ? CliContextDocument.Empty
            : store.Load();

        List<string> warnings = [];

        (bool campaignsLoaded, CampaignDto[] campaigns) = await GetCampaignsAsync(cancellationToken)
            .ConfigureAwait(false);

        CampaignDto? activeCampaign = null;

        if (active.CampaignId is { } activeCampaignId)
        {

            activeCampaign = campaigns.FirstOrDefault(
                item => item.Id == activeCampaignId);

            if (campaignsLoaded && activeCampaign is null)
            {

                warnings.Add(
                    $"Saved campaign {activeCampaignId:D} is stale and was cleared.");

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

                warnings.Add(
                    $"Saved workspace {active.WorkspaceId} is stale and was cleared.");

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

            warnings.Add(
                $"Saved model {active.Model} is no longer configured and was cleared.");

            active = active with { Model = null };

        }

        SessionDetailDto? session = null;

        bool staleSessionCleared = false;

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

                warnings.Add(
                    $"Saved session {sessionId:D} is stale and was cleared.");

                active = active with { SessionId = null };

                staleSessionCleared = true;

            }

        }

        if (!noContext && active != store.Load())
        {

            store.Save(active);

        }

        if (staleSessionCleared)
        {

            sessionManager.ClearSession(quiet: true);

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
