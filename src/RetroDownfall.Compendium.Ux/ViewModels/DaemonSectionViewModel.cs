using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class DaemonSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _maxConcurrentJobs;

    public ObservableCollection<UnseenServantJobViewModel> Jobs { get; } = [];

    private DaemonSettings _snapshot = new();

    public void LoadFrom(DaemonSettings daemon)
    {

        _snapshot = daemon;

        MaxConcurrentJobs = daemon.MaxConcurrentJobs;

        Jobs.Clear();

        foreach (UnseenServantJob job in daemon.Jobs)
        {

            Jobs.Add(new UnseenServantJobViewModel(job));

        }

    }

    public DaemonSettings Build() => _snapshot with
    {
        MaxConcurrentJobs = MaxConcurrentJobs,
        Jobs = Jobs.Select(static job => job.Build()).ToList(),
    };

    [RelayCommand]
    private void AddJob() =>
        Jobs.Add(new UnseenServantJobViewModel(new UnseenServantJob()));

    [RelayCommand]
    private void RemoveJob(UnseenServantJobViewModel? job)
    {

        if (job is not null)
        {

            Jobs.Remove(job);

        }

    }

    public sealed partial class UnseenServantJobViewModel : ObservableObject
    {

        [ObservableProperty] private string _name = string.Empty;

        [ObservableProperty] private int _intervalMinutes;

        [ObservableProperty] private string _targetSpell = string.Empty;

        [ObservableProperty] private bool _enabled;

        private UnseenServantJob _snapshot;

        public UnseenServantJobViewModel(UnseenServantJob snapshot)
        {

            _snapshot = snapshot;

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
