using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Phase 3 placeholder Workbench document. Later phases replace this with real SpellEditor,
/// PromptEditor, Tome, ApprenticeDetail, TrialDesigner, and Config ViewModels while keeping the
/// same <see cref="DocumentKey"/> routing shape.
/// </summary>
public sealed class WorkbenchDocumentPlaceholderViewModel : ViewModelBase
{

    public WorkbenchDocumentPlaceholderViewModel(DocumentKind kind, string id, string? emptyState = null)
    {

        Kind = kind;

        DocumentId = id;

        Title = $"{kind}: {id}";

        EmptyState = emptyState
            ?? "This Workbench document is reserved for a later phase of The Forge.";

    }

    public override DocumentKind? Kind { get; }

    public string DocumentId { get; }

    public string EmptyState { get; }

}

internal readonly record struct DocumentKey(DocumentKind Kind, string Id);
