using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class DaemonSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _maxConcurrentJobs;

    public ObservableCollection<UnseenServantJobViewModel> Jobs { get; } = [];

    private DaemonSettings _snapshot = new();

    private readonly IDialogService _dialogService;

    public DaemonSectionViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public void LoadFrom(DaemonSettings daemon)
    {

        _snapshot = daemon;

        MaxConcurrentJobs = daemon.MaxConcurrentJobs;

        Jobs.Clear();

        foreach (UnseenServantJob job in daemon.Jobs)
        {

            Jobs.Add(new UnseenServantJobViewModel(job, _dialogService));

        }

    }

    public DaemonSettings Build() => _snapshot with
    {
        MaxConcurrentJobs = MaxConcurrentJobs,
        Jobs = Jobs.Select(static job => job.Build()).ToList(),
    };

    [RelayCommand]
    private void AddJob() =>
        Jobs.Add(new UnseenServantJobViewModel(new UnseenServantJob(), _dialogService));

    [RelayCommand]
    private async Task RemoveJobAsync(UnseenServantJobViewModel? job)
    {

        if (job is null)
        {

            return;

        }

        bool confirmed = await _dialogService.ShowConfirmAsync(
            "Remove Job",
            $"Are you sure you want to remove the job '{job.Name}'? This action cannot be undone.",
            "Remove",
            "Cancel");

        if (!confirmed)
        {

            return;

        }

        Jobs.Remove(job);

    }

    public sealed partial class UnseenServantJobViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private int _intervalMinutes;

        [ObservableProperty] private string _targetSpell = string.Empty;

        [ObservableProperty] private bool _enabled;

        private UnseenServantJob _snapshot;

        private readonly IDialogService _dialogService;

        public UnseenServantJobViewModel(UnseenServantJob snapshot, IDialogService dialogService)
        {

            _snapshot = snapshot;

            _dialogService = dialogService;

            LoadFrom(snapshot);

        }

        public void LoadFrom(UnseenServantJob snapshot)
        {

            _snapshot = snapshot;

            Name = snapshot.Name;

            IntervalMinutes = snapshot.IntervalMinutes;

            TargetSpell = snapshot.TargetSpell;

            Enabled = snapshot.Enabled;

        }

        public UnseenServantJob Build() => _snapshot with
        {
            Name = Name,
            IntervalMinutes = IntervalMinutes,
            TargetSpell = TargetSpell,
            Enabled = Enabled,
        };

    }

}
