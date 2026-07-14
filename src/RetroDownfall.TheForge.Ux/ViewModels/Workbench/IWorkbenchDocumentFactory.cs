using RetroDownfall.TheForge.Ux.Markdown;
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
/// navigation opens The Tome; Codex opens the CODEX.md editor; Markdown opens The Illumination
/// tab when content was registered in <see cref="IMarkdownDocumentContentStore"/>.
/// </summary>
public sealed class WorkbenchDocumentFactory : IWorkbenchDocumentFactory
{

    private readonly ISpellEditorDataSource _spellEditorDataSource;

    private readonly IPromptEditorDataSource _promptEditorDataSource;

    private readonly ITomeDataSource _tomeDataSource;

    private readonly ICodexDataSource _codexDataSource;

    private readonly IMarkdownDocumentContentStore _markdownContentStore;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    public WorkbenchDocumentFactory(
        ISpellEditorDataSource spellEditorDataSource,
        IPromptEditorDataSource promptEditorDataSource,
        ITomeDataSource tomeDataSource,
        ICodexDataSource codexDataSource,
        IMarkdownDocumentContentStore markdownContentStore,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor)
    {

        _spellEditorDataSource = spellEditorDataSource;

        _promptEditorDataSource = promptEditorDataSource;

        _tomeDataSource = tomeDataSource;

        _codexDataSource = codexDataSource;

        _markdownContentStore = markdownContentStore;

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

        if (kind == DocumentKind.Prompt && Guid.TryParse(id, out Guid promptId))
        {

            ScriptoriumViewModel scriptorium = new(promptId, _promptEditorDataSource, _navigation, _foundryFloor);

            _ = scriptorium.LoadCommand.ExecuteAsync(null);

            return scriptorium;

        }

        if (kind == DocumentKind.Session && Guid.TryParse(id, out Guid sessionId))
        {

            TomeViewModel tome = new(sessionId, _tomeDataSource, _navigation, _foundryFloor);

            _ = tome.LoadCommand.ExecuteAsync(null);

            return tome;

        }

        if (kind == DocumentKind.Codex)
        {

            Guid? campaignId = string.Equals(id, "global", StringComparison.OrdinalIgnoreCase)
                ? null
                : Guid.TryParse(id, out Guid parsed)
                    ? parsed
                    : null;

            if (!string.Equals(id, "global", StringComparison.OrdinalIgnoreCase) && campaignId is null)
            {

                return new WorkbenchDocumentPlaceholderViewModel(kind, id);

            }

            CodexViewModel codex = new(campaignId, _codexDataSource, _foundryFloor);

            _ = codex.LoadCommand.ExecuteAsync(null);

            return codex;

        }

        if (kind == DocumentKind.Markdown)
        {

            if (_markdownContentStore.TryGet(id, out MarkdownDocumentPayload payload))
            {

                return new MarkdownDocumentViewModel(
                    payload.Id,
                    payload.Title,
                    payload.Content,
                    _markdownContentStore,
                    payload.WorkspaceId,
                    payload.RelativePath,
                    payload.BaseRelativeDirectory);

            }

            return new WorkbenchDocumentPlaceholderViewModel(
                kind,
                id,
                "Markdown preview content is no longer available. Reopen the file from Workspace Explorer.");

        }

        return new WorkbenchDocumentPlaceholderViewModel(kind, id);

    }

}
