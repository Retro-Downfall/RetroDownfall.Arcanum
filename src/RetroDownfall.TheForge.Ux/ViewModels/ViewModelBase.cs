using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Base type for every Forge ViewModel. <see cref="Kind"/> identifies a document-hosting ViewModel's
/// <see cref="DocumentKind"/> for Workbench tab tracking (<c>null</c> for non-document ViewModels,
/// e.g. panel roots like <c>AtelierViewModel</c>); <see cref="Title"/> is the Workbench tab label.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The document kind for tab identification, or <see langword="null"/> for non-document ViewModels.</summary>
    public virtual DocumentKind? Kind => null;

}
