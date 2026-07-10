using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Base type for every node in The Atelier tree. Nodes lazily load their children the first time they
/// are expanded (<see cref="ExpandAsync"/>) so the tree never fetches an entire campaign's contents
/// up front. Leaf nodes report <see cref="HasChildren"/> = <see langword="false"/> and never load.
/// </summary>
public abstract partial class AtelierNodeViewModel : ObservableObject
{

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _icon = "IconSpell";

    private bool _childrenLoaded;

    private bool _expandLoadQueued;

    public ObservableCollection<AtelierNodeViewModel> Children { get; } = [];

    /// <summary>Whether this node can hold children (branch nodes); leaves override to <see langword="false"/>.</summary>
    public virtual bool HasChildren => true;

    /// <summary>The node's primary action (usually Open) for double-click and context menus, if any.</summary>
    public virtual ICommand? PrimaryCommand => null;

    /// <summary>
    /// Expands the node, loading its children on first expansion. Safe to call repeatedly — children
    /// load at most once unless <see cref="ReloadAsync"/> is used.
    /// </summary>
    [RelayCommand]
    public async Task ExpandAsync(CancellationToken cancellationToken)
    {

        IsExpanded = true;

        if (_childrenLoaded || !HasChildren)
        {

            return;

        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

    }

    /// <summary>Reloads this node's children from its data source, replacing any existing children.</summary>
    [RelayCommand]
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {

        if (!HasChildren)
        {

            return;

        }

        IsLoading = true;

        try
        {

            Children.Clear();

            foreach (AtelierNodeViewModel child in await LoadChildrenAsync(cancellationToken).ConfigureAwait(true))
            {

                Children.Add(child);

            }

            _childrenLoaded = true;

        }
        finally
        {

            IsLoading = false;

        }

    }

    /// <summary>
    /// Marks children as already loaded (for category nodes that were pre-populated in the constructor).
    /// </summary>
    protected void MarkChildrenLoaded() => _childrenLoaded = true;

    partial void OnIsExpandedChanged(bool value)
    {

        if (!value || _childrenLoaded || !HasChildren || _expandLoadQueued)
        {

            return;

        }

        _expandLoadQueued = true;

        _ = ExpandFromIsExpandedAsync();

    }

    private async Task ExpandFromIsExpandedAsync()
    {

        try
        {

            await ExpandAsync(CancellationToken.None).ConfigureAwait(true);

        }
        finally
        {

            _expandLoadQueued = false;

        }

    }

    /// <summary>Loads the child nodes for this branch. Branch nodes override; the default is empty.</summary>
    protected virtual Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AtelierNodeViewModel>>([]);

}
