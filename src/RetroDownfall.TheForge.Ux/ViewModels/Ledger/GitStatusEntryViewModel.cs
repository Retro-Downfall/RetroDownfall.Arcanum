using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.TheForge.Ux.Services.Git;

namespace RetroDownfall.TheForge.Ux.ViewModels.Ledger;

/// <summary>Selectable status row for The Ledger staged / unstaged lists.</summary>
public sealed partial class GitStatusEntryViewModel : ObservableObject
{

    public GitStatusEntryViewModel(GitPorcelainEntry entry, bool isStagedList)
    {

        Entry = entry;

        IsStagedList = isStagedList;

        Path = entry.Path;

        DisplayPath = entry.DisplayPath;

        StatusCode = entry.StatusCode;

        OriginalPath = entry.OriginalPath;

    }

    public GitPorcelainEntry Entry { get; }

    public bool IsStagedList { get; }

    public string Path { get; }

    public string DisplayPath { get; }

    public string StatusCode { get; }

    public string? OriginalPath { get; }

    public string Label => $"{StatusCode} {DisplayPath}";

    [ObservableProperty]
    private bool _isSelected;

}
