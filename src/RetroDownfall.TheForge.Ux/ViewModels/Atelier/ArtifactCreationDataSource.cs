using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>API-backed creation data source for Atelier New* commands. Maps ApiResponse failures to error strings (no throwing).</summary>
public sealed class ArtifactCreationDataSource : IArtifactCreationDataSource
{

    private readonly SpellService _spellService;

    private readonly PromptService _promptService;

    private readonly SessionService _sessionService;

    private readonly CampaignService _campaignService;

    private readonly WorkspaceService _workspaceService;

    public ArtifactCreationDataSource(
        SpellService spellService,
        PromptService promptService,
        SessionService sessionService,
        CampaignService campaignService,
        WorkspaceService workspaceService)
    {

        _spellService = spellService;

        _promptService = promptService;

        _sessionService = sessionService;

        _campaignService = campaignService;

        _workspaceService = workspaceService;

    }

    public async Task<(bool Success, string? Error)> CreateSpellAsync(
        string workspacePath,
        CreateSpellRequest request,
        CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _spellService
            .CreateAsync(workspacePath, request, cancellationToken)
            .ConfigureAwait(false);

        if (response is { IsSuccess: true, Data: true })
        {

            return (true, null);

        }

        return (false, response?.Error?.Message ?? "Failed to create spell.");

    }

    public async Task<(PromptDetailDto? Prompt, string? Error)> CreatePromptAsync(
        CreatePromptRequest request,
        CancellationToken cancellationToken)
    {

        ApiResponse<PromptDetailDto>? response = await _promptService
            .CreateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response is { IsSuccess: true })
        {

            return (response.Data, null);

        }

        return (null, response?.Error?.Message ?? "Failed to create prompt.");

    }

    public async Task<(SessionDetailDto? Session, string? Error)> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken cancellationToken)
    {

        ApiResponse<SessionDetailDto>? response = await _sessionService
            .CreateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response is { IsSuccess: true })
        {

            return (response.Data, null);

        }

        return (null, response?.Error?.Message ?? "Failed to create session.");

    }

    public async Task<IReadOnlyList<WorkspaceOption>> ListWorkspaceOptionsAsync(CancellationToken cancellationToken)
    {

        List<WorkspaceOption> options = new();

        ApiResponse<ListPageResult<CampaignDto>>? campaignsResponse = await _campaignService
            .ListAsync(type: null, limit: 10_000, offset: 0, cancellationToken)
            .ConfigureAwait(false);

        foreach (CampaignDto campaign in campaignsResponse?.Data?.Items ?? [])
        {

            options.Add(new WorkspaceOption(campaign.Path, $"Campaign: {campaign.Name}"));

        }

        ApiResponse<WorkspaceInfo[]>? workspacesResponse = await _workspaceService
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (WorkspaceInfo workspace in workspacesResponse?.Data ?? [])
        {

            options.Add(new WorkspaceOption(workspace.Path, workspace.Name));

        }

        return options;

    }

}
