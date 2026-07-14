using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/spells</c> route group. Campaign-scoped spells use
/// <c>GET /api/campaigns/{id}/spells</c> (NOT <c>GET /api/spells?campaignId=</c> — campaign spells
/// shadow built-ins of the same name), and execution streams via
/// <c>POST /api/spells/{name}/execute-stream</c> (NDJSON <see cref="IntelligenceEvent"/>), distinct
/// from the standalone-chat <c>ping-stream</c> endpoint on <see cref="SessionService"/>.
/// </summary>
public sealed class SpellService
{

    private readonly ArcanumApiClient _apiClient;

    public SpellService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<SpellSummary[]>?> ListAsync(string? workspace, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build("/api/spells", ("workspace", workspace));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSpellSummaryArray, cancellationToken);

    }

    /// <summary><c>GET /api/campaigns/{id}/spells</c> — campaign-scoped spells shadow built-ins of the same name.</summary>
    public Task<ApiResponse<SpellSummary[]>?> GetCampaignSpellsAsync(
        Guid campaignId,
        string? query,
        string? tag,
        string? tool,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/campaigns/{campaignId}/spells",
            ("q", query),
            ("tag", tag),
            ("tool", tool));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSpellSummaryArray, cancellationToken);

    }

    public Task<ApiResponse<SpellDetail>?> GetAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build($"/api/spells/{Uri.EscapeDataString(name)}", ("workspace", workspace));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSpellDetail, cancellationToken);

    }

    /// <summary><c>POST /api/spells?workspace={path}</c> — writes a new workspace spell (built-in spells are read-only).</summary>
    public Task<ApiResponse<bool>?> CreateAsync(string workspace, CreateSpellRequest request, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build("/api/spells", ("workspace", workspace));

        return _apiClient.PostAsync(
            path,
            request,
            TheForgeJsonContext.Default.CreateSpellRequest,
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    }

    public Task<ApiResponse<bool>?> UpdateAsync(string name, UpdateSpellRequest request, CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            $"/api/spells/{Uri.EscapeDataString(name)}",
            request,
            TheForgeJsonContext.Default.UpdateSpellRequest,
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary>Dry-run preview — consumes no tokens.</summary>
    public Task<ApiResponse<SpellCastResult>?> CastAsync(string name, SpellCastRequest? request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/spells/{Uri.EscapeDataString(name)}/cast",
            request ?? new SpellCastRequest(),
            TheForgeJsonContext.Default.SpellCastRequest,
            TheForgeJsonContext.Default.ApiResponseSpellCastResult,
            cancellationToken);

    /// <summary>Live execution — NDJSON <see cref="IntelligenceEvent"/> stream, opens a Tome tab in the Workbench.</summary>
    public IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(
        string name,
        SpellExecuteRequest request,
        CancellationToken cancellationToken) =>
        _apiClient.PostNdjsonStreamAsync(
            $"/api/spells/{Uri.EscapeDataString(name)}/execute-stream",
            request,
            TheForgeJsonContext.Default.SpellExecuteRequest,
            TheForgeJsonContext.Default.IntelligenceEvent,
            cancellationToken);

    public Task<ApiResponse<SpellVersionDto[]>?> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build($"/api/spells/{Uri.EscapeDataString(name)}/versions", ("workspace", workspace));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseSpellVersionDtoArray, cancellationToken);

    }

    public Task<ApiResponse<SpellVersionDto>?> ActivateVersionAsync(
        string name,
        string version,
        ActivateSpellVersionRequest request,
        CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/spells/{Uri.EscapeDataString(name)}/versions/{Uri.EscapeDataString(version)}/activate",
            request,
            TheForgeJsonContext.Default.ActivateSpellVersionRequest,
            TheForgeJsonContext.Default.ApiResponseSpellVersionDto,
            cancellationToken);

    /// <summary><c>POST /api/intelligence/mana</c> — read-only token estimate; call before Execute.</summary>
    public Task<ApiResponse<ManaCountResult>?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/intelligence/mana",
            request,
            TheForgeJsonContext.Default.ManaCountRequest,
            TheForgeJsonContext.Default.ApiResponseManaCountResult,
            cancellationToken);

}
