using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>API-backed data source for the Spell editor.</summary>
public sealed class SpellEditorDataSource : ISpellEditorDataSource
{

    private readonly SpellService _spellService;

    public SpellEditorDataSource(SpellService spellService)
    {

        _spellService = spellService;

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

    public async Task<bool> SaveAsync(string name, UpdateSpellRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _spellService.UpdateAsync(name, request, cancellationToken).ConfigureAwait(false);

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

    public async Task<bool> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
    {

        ApiResponse<SpellVersionDto>? response = await _spellService
            .ActivateVersionAsync(name, version, new ActivateSpellVersionRequest(workspace), cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

}
