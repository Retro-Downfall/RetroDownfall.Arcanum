using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for the Spell editor, implemented by the API-backed adapter in production and fakes in tests.</summary>
public interface ISpellEditorDataSource
{

    Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<SpellVersionDetailDto?> GetVersionDetailAsync(string name, string version, string? workspace, CancellationToken cancellationToken);

    Task<SpellVersionDto?> CreateVersionAsync(string name, CreateSpellVersionRequest request, CancellationToken cancellationToken);

    Task<SpellVersionDto?> UpdateVersionAsync(string name, string version, UpdateSpellVersionRequest request, CancellationToken cancellationToken);

    Task<bool> SaveAsync(string name, UpdateSpellRequest request, string? workspace, CancellationToken cancellationToken);

    Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken);

    Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, CancellationToken cancellationToken);

    Task<SpellVersionDto?> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken);

    Task<SpellValidationResultDto?> ValidateAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<SpellExportDto?> ExportAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<SpellSummary?> CloneAsync(string name, CloneSpellRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string name, string workspace, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListSpellNamesAsync(string? workspace, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListAvailableToolNamesAsync(string? workspace, CancellationToken cancellationToken);

}
