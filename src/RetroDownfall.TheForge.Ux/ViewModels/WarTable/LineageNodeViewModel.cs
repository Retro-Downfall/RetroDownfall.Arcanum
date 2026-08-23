using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Conclave;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>One node in the Conclave lineage tree (parent chain walked client-side).</summary>
public sealed partial class LineageNodeViewModel : ObservableObject
{

    public LineageNodeViewModel(ApprenticeDetailDto detail)
    {

        Id = detail.Id;

        Name = detail.Name;

        Status = detail.Status;

    }

    public Guid Id { get; }

    public string Name { get; }

    public string Status { get; }

    public string Display => $"{Name} ({Status})";

    public ObservableCollection<LineageNodeViewModel> Children { get; } = [];

}
