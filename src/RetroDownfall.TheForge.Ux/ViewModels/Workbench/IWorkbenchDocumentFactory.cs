using RetroDownfall.TheForge.Ux.Markdown;

using RetroDownfall.TheForge.Ux.Models;

using RetroDownfall.TheForge.Core.Services;

using RetroDownfall.TheForge.Ux.Services;

using RetroDownfall.TheForge.Ux.Services.Whispers;

using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Factory seam used by <c>MainViewModel</c> to create Workbench documents from navigation requests.</summary>
public interface IWorkbenchDocumentFactory
{

    ViewModelBase Create(DocumentKind kind, string id, string? workspace = null);

}

/// <summary>
/// Production Workbench document factory. Spell navigation opens the Spell editor; Session
/// navigation opens The Tome; Codex opens the CODEX.md editor; Markdown opens The Illumination
/// tab when content was registered in <see cref="IMarkdownDocumentContentStore"/>; Trial opens
/// the singleton Proving Grounds designer.
/// </summary>
public sealed class WorkbenchDocumentFactory : IWorkbenchDocumentFactory
{

    private readonly ISpellEditorDataSource _spellEditorDataSource;

    private readonly IPromptEditorDataSource _promptEditorDataSource;

    private readonly ITomeDataSource _tomeDataSource;

    private readonly ICodexDataSource _codexDataSource;

    private readonly ITrialDataSource _trialDataSource;

    private readonly ITrialSuiteStore _trialSuiteStore;

    private readonly IComparisonRunStore _comparisonRunStore;

    private readonly IComparisonWorkbenchDataSource _comparisonDataSource;

    private readonly IInferenceTraceStore _inferenceTraceStore;

    private readonly IMarkdownDocumentContentStore _markdownContentStore;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly ITextInputDialogService _textInputDialog;

    private readonly IClipboardService _clipboard;

    private readonly IWhispersService _whispers;

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    public WorkbenchDocumentFactory(
        ISpellEditorDataSource spellEditorDataSource,
        IPromptEditorDataSource promptEditorDataSource,
        ITomeDataSource tomeDataSource,
        ICodexDataSource codexDataSource,
        ITrialDataSource trialDataSource,
        ITrialSuiteStore trialSuiteStore,
        IComparisonRunStore comparisonRunStore,
        IComparisonWorkbenchDataSource comparisonDataSource,
        IInferenceTraceStore inferenceTraceStore,
        IMarkdownDocumentContentStore markdownContentStore,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IConfirmationDialogService confirmationDialog,
        IArtifactFileDialogService fileDialog,
        ITextInputDialogService textInputDialog,
        IClipboardService clipboard,
        IWhispersService whispers,
        ITheForgeLocalMutationRunner mutationRunner)
    {

        _spellEditorDataSource = spellEditorDataSource;

        _promptEditorDataSource = promptEditorDataSource;

        _tomeDataSource = tomeDataSource;

        _codexDataSource = codexDataSource;

        _trialDataSource = trialDataSource;

        _trialSuiteStore = trialSuiteStore;

        _comparisonRunStore = comparisonRunStore;

        _comparisonDataSource = comparisonDataSource;

        _inferenceTraceStore = inferenceTraceStore;

        _markdownContentStore = markdownContentStore;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _confirmationDialog = confirmationDialog;

        _fileDialog = fileDialog;

        _textInputDialog = textInputDialog;

        _clipboard = clipboard;

        _whispers = whispers;

        _mutationRunner = mutationRunner;

    }

    public ViewModelBase Create(DocumentKind kind, string id, string? workspace = null)
    {

        if (kind == DocumentKind.Spell)
        {

            SpellEditorViewModel editor = new(
                id,
                _spellEditorDataSource,
                _navigation,
                _foundryFloor,
                _confirmationDialog,
                _fileDialog,
                _textInputDialog,
                _whispers,
                _mutationRunner,
                workspace);

            _ = editor.LoadCommand.ExecuteAsync(null);

            return editor;

        }

        if (kind == DocumentKind.Prompt && Guid.TryParse(id, out Guid promptId))
        {

            ScriptoriumViewModel scriptorium = new(
                promptId,
                _promptEditorDataSource,
                _navigation,
                _foundryFloor,
                _confirmationDialog,
                _fileDialog,
                _textInputDialog,
                _whispers,
                _mutationRunner);

            _ = scriptorium.LoadCommand.ExecuteAsync(null);

            return scriptorium;

        }

        if (kind == DocumentKind.Session && Guid.TryParse(id, out Guid sessionId))
        {

            TomeViewModel tome = new(
                sessionId,
                _tomeDataSource,
                _navigation,
                _foundryFloor,
                _clipboard,
                _confirmationDialog,
                _mutationRunner);

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

            CodexViewModel codex = new(campaignId, _codexDataSource, _foundryFloor, _confirmationDialog);

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

        if (kind == DocumentKind.Trial)
        {

            ProvingGroundsViewModel provingGrounds = new(
                _trialDataSource,
                _foundryFloor,
                _whispers,
                _confirmationDialog,
                _trialSuiteStore,
                _fileDialog,
                _mutationRunner);

            _ = provingGrounds.LoadPickersCommand.ExecuteAsync(null);

            return provingGrounds;

        }

        if (kind == DocumentKind.Comparison)
        {

            ComparisonWorkbenchViewModel comparison = new(
                _comparisonDataSource,
                _comparisonRunStore,
                _foundryFloor,
                _whispers,
                _confirmationDialog,
                _fileDialog,
                _navigation,
                _mutationRunner,
                _inferenceTraceStore);

            _ = comparison.LoadHistoryCommand.ExecuteAsync(null);

            return comparison;

        }

        return new WorkbenchDocumentPlaceholderViewModel(kind, id);

    }

}
