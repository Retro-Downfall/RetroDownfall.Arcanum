using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

/// <summary>
/// The Foundry Floor collects cast/trial/tool output and Arcanum log lines. Phase 6 adds a minimal
/// append seam so The Tome can surface stream errors here; richer log routing arrives later.
/// </summary>
public sealed partial class FoundryFloorViewModel : ViewModelBase
{

    [ObservableProperty]
    private string _latestLine = string.Empty;

    public FoundryFloorViewModel()
    {

        Title = "The Foundry Floor";

    }

    public ObservableCollection<string> Lines { get; } = [];

    public bool HasNoLines => Lines.Count == 0;

    public string OutputEmptyState => HasNoLines
        ? "Output from casts, trials, and tools will collect here."
        : string.Empty;

    public string LogsEmptyState => "Arcanum logs will stream here.";

    public void AppendLine(string line)
    {

        Lines.Add(line);

        LatestLine = line;

        OnPropertyChanged(nameof(HasNoLines));

        OnPropertyChanged(nameof(OutputEmptyState));

    }

}
