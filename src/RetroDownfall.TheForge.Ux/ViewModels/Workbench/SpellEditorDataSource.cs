using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>API-backed data source for the Spell editor.</summary>
public sealed class SpellEditorDataSource : ISpellEditorDataSource
{

    private readonly SpellService _spellService;

    private readonly McpService _mcpService;

    public SpellEditorDataSource(SpellService spellService, McpService mcpService)
    {

        _spellService = spellService;

        _mcpService = mcpService;

    }

    public async Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellDetail>? response = await _spellService.GetAsync(name, workspace, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDto[]>? response = await _spellService.ListVersionsAsync(name, workspace, cancellationToken).ConfigureAwait(false);

        return response?.Data ?? [];

    }

    public async Task<SpellVersionDetailDto?> GetVersionDetailAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDetailDto>? response = await _spellService
            .GetVersionDetailAsync(name, version, workspace, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SpellVersionDto?> CreateVersionAsync(string name, CreateSpellVersionRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDto>? response = await _spellService
            .CreateVersionAsync(name, request, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SpellVersionDto?> UpdateVersionAsync(string name, string version, UpdateSpellVersionRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDto>? response = await _spellService
            .UpdateVersionAsync(name, version, request, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<bool> SaveAsync(string name, UpdateSpellRequest request, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _spellService.UpdateAsync(name, request, workspace, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: true };

    }

    public async Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SpellCastResult>? response = await _spellService.CastAsync(name, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<ManaCountResult>? response = await _spellService.EstimateManaAsync(request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, CancellationToken cancellationToken) =>
        _spellService.ExecuteStreamAsync(name, request, cancellationToken);

    public async Task<SpellVersionDto?> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDto>? response = await _spellService
            .ActivateVersionAsync(name, version, new ActivateSpellVersionRequest(workspace), cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SpellValidationResultDto?> ValidateAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellValidationResultDto>? response = await _spellService
            .ValidateAsync(name, workspace, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SpellExportDto?> ExportAsync(string name, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellExportDto>? response = await _spellService.ExportAsync(name, workspace, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<SpellSummary?> CloneAsync(string name, CloneSpellRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary>? response = await _spellService.CloneAsync(name, request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true } ? response.Data : null;

    }

    public async Task<DataSourceResult<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary>? response = await _spellService.ImportAsync(request, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<SpellSummary>.FromResponse(response);

    }

    public Task<DeleteOutcome> DeleteAsync(string name, string workspace, CancellationToken cancellationToken) =>
        _spellService.DeleteAsync(name, workspace, cancellationToken);

    public async Task<IReadOnlyList<string>> ListSpellNamesAsync(string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary[]>? response = await _spellService.ListAsync(workspace, cancellationToken).ConfigureAwait(false);

        if (response?.Data is null)
        {

            return [];

        }

        return response.Data
            .Select(static spell => spell.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    }

    public async Task<IReadOnlyList<string>> ListAvailableToolNamesAsync(string? workspace, CancellationToken cancellationToken)
    {

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        ApiResponse<McpServerInfo[]>? mcpResponse = await _mcpService.ListAsync(cancellationToken).ConfigureAwait(false);

        if (mcpResponse?.Data is not null)
        {

            foreach (McpServerInfo server in mcpResponse.Data)
            {

                foreach (string tool in server.Tools)
                {

                    if (!string.IsNullOrWhiteSpace(tool))
                    {

                        names.Add(tool);

                    }

                }

            }

        }

        ApiResponse<WorkspaceArsenalDto>? arsenalResponse = await _mcpService
            .GetArsenalAsync(workspace, cancellationToken)
            .ConfigureAwait(false);

        if (arsenalResponse?.Data is not null)
        {

            foreach (string tool in arsenalResponse.Data.NativeTools)
            {

                if (!string.IsNullOrWhiteSpace(tool))
                {

                    names.Add(tool);

                }

            }

            foreach (McpServerStatusDto server in arsenalResponse.Data.McpServers)
            {

                foreach (string tool in server.ProvidedTools)
                {

                    if (!string.IsNullOrWhiteSpace(tool))
                    {

                        names.Add(tool);

                    }

                }

            }

        }

        return names
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    }

}
