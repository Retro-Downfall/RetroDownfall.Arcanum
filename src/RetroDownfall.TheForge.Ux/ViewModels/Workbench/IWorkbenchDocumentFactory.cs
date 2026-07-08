using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Factory seam used by <c>MainViewModel</c> to create Workbench documents from navigation requests.</summary>
public interface IWorkbenchDocumentFactory
{

    ViewModelBase Create(DocumentKind kind, string id);

}

/// <summary>
/// Production Workbench document factory. Spell navigation opens the Spell editor; Session
/// navigation opens The Tome. Other kinds remain placeholders until later phases.
/// </summary>
public sealed class WorkbenchDocumentFactory : IWorkbenchDocumentFactory
{

    private readonly ISpellEditorDataSource _spellEditorDataSource;

    private readonly ITomeDataSource _tomeDataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    public WorkbenchDocumentFactory(
        ISpellEditorDataSource spellEditorDataSource,
        ITomeDataSource tomeDataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor)
    {

        _spellEditorDataSource = spellEditorDataSource;

        _tomeDataSource = tomeDataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

    }

    public ViewModelBase Create(DocumentKind kind, string id)
    {

        if (kind == DocumentKind.Spell)
        {

            SpellEditorViewModel editor = new(id, _spellEditorDataSource, _navigation);

            _ = editor.LoadCommand.ExecuteAsync(null);

            return editor;

        }

        if (kind == DocumentKind.Session && Guid.TryParse(id, out Guid sessionId))
        {

            TomeViewModel tome = new(sessionId, _tomeDataSource, _navigation, _foundryFloor);

            _ = tome.LoadCommand.ExecuteAsync(null);

            return tome;

        }

        return new WorkbenchDocumentPlaceholderViewModel(kind, id);

    }

}
