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

/// <summary>
/// Workbench tab identity. <see cref="IdentityWorkspace"/> is normalized via
/// <c>WorkspacePathHelper.ForIdentity</c> (trim, empty→null, trailing separators stripped).
/// API calls keep a separate trimmed workspace value and must not use this field as a rewrite of the path.
/// </summary>
internal readonly record struct DocumentKey(DocumentKind Kind, string Id, string? IdentityWorkspace = null);
