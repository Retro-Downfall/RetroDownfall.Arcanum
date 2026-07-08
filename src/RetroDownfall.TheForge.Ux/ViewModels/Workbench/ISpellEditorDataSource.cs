using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for the Spell editor, implemented by the API-backed adapter in production and fakes in tests.</summary>
public interface ISpellEditorDataSource
{

    Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken);

    Task<bool> SaveAsync(string name, UpdateSpellRequest request, CancellationToken cancellationToken);

    Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken);

    Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, CancellationToken cancellationToken);

    Task<bool> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken);

}
