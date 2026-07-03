using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class OrchestrationSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _daemonMaxConcurrentJobs;

    [ObservableProperty] private int _daemonShutdownDrainTimeoutSeconds;

    [ObservableProperty] private int _daemonExecutionHistoryLimit;

    public ObservableCollection<UnseenServantJobViewModel> Jobs { get; } = [];

    [ObservableProperty] private bool _apprenticesEnabled;

    [ObservableProperty] private int _apprenticesMaxConcurrentApprentices;

    [ObservableProperty] private int _apprenticesStepTimeoutMinutes;

    [ObservableProperty] private int _apprenticesChronicleChannelCapacity;

    [ObservableProperty] private int _apprenticesMaxStepRetries;

    [ObservableProperty] private int _apprenticesRetryBackoffSeconds;

    [ObservableProperty] private int _apprenticesRetryBackoffMaxSeconds;

    [ObservableProperty] private bool _apprenticesEnableShiftingFate;

    [ObservableProperty] private bool _apprenticesEnableDivineIntervention;

    [ObservableProperty] private int _apprenticesMaxSimulacra;

    [ObservableProperty] private int _apprenticesMaxRunSteps;

    [ObservableProperty] private int _apprenticesMaxRunDurationMinutes;

    [ObservableProperty] private int _apprenticesMaxReweavesPerRun;

    [ObservableProperty] private int _apprenticesMaxPendingStarts;

    [ObservableProperty] private bool _conclaveEnabled;

    [ObservableProperty] private int _conclaveMaxDelegationDepth;

    [ObservableProperty] private int _conclaveMaxDescendantsPerRoot;

    private DaemonSettings _daemonSnapshot = new();

    private ApprenticeSettings _apprenticeSnapshot = new();

    private ConclaveSettings _conclaveSnapshot = new();

    public void LoadFrom(
        DaemonSettings daemon,
        ApprenticeSettings apprentices,
        ConclaveSettings conclave)
    {

        _daemonSnapshot = daemon;

        _apprenticeSnapshot = apprentices;

        _conclaveSnapshot = conclave;

        DaemonMaxConcurrentJobs = daemon.MaxConcurrentJobs;

        DaemonShutdownDrainTimeoutSeconds = daemon.ShutdownDrainTimeoutSeconds;

        DaemonExecutionHistoryLimit = daemon.ExecutionHistoryLimit;

        Jobs.Clear();

        foreach (UnseenServantJob job in daemon.Jobs)
        {

            Jobs.Add(new UnseenServantJobViewModel(job));

        }

        ApprenticesEnabled = apprentices.Enabled;

        ApprenticesMaxConcurrentApprentices = apprentices.MaxConcurrentApprentices;

        ApprenticesStepTimeoutMinutes = apprentices.StepTimeoutMinutes;

        ApprenticesChronicleChannelCapacity = apprentices.ChronicleChannelCapacity;

        ApprenticesMaxStepRetries = apprentices.MaxStepRetries;

        ApprenticesRetryBackoffSeconds = apprentices.RetryBackoffSeconds;

        ApprenticesRetryBackoffMaxSeconds = apprentices.RetryBackoffMaxSeconds;

        ApprenticesEnableShiftingFate = apprentices.EnableShiftingFate;

        ApprenticesEnableDivineIntervention = apprentices.EnableDivineIntervention;

        ApprenticesMaxSimulacra = apprentices.MaxSimulacra;

        ApprenticesMaxRunSteps = apprentices.MaxRunSteps;

        ApprenticesMaxRunDurationMinutes = apprentices.MaxRunDurationMinutes;

        ApprenticesMaxReweavesPerRun = apprentices.MaxReweavesPerRun;

        ApprenticesMaxPendingStarts = apprentices.MaxPendingStarts;

        ConclaveEnabled = conclave.Enabled;

        ConclaveMaxDelegationDepth = conclave.MaxDelegationDepth;

        ConclaveMaxDescendantsPerRoot = conclave.MaxDescendantsPerRoot;

    }

    public DaemonSettings BuildDaemon() => _daemonSnapshot with
    {

        MaxConcurrentJobs = DaemonMaxConcurrentJobs,

        ShutdownDrainTimeoutSeconds = DaemonShutdownDrainTimeoutSeconds,

        ExecutionHistoryLimit = DaemonExecutionHistoryLimit,

        Jobs = Jobs.Select(static j => j.Build()).ToList(),

    };

    public ApprenticeSettings BuildApprentices() => _apprenticeSnapshot with
    {

        Enabled = ApprenticesEnabled,

        MaxConcurrentApprentices = ApprenticesMaxConcurrentApprentices,

        StepTimeoutMinutes = ApprenticesStepTimeoutMinutes,

        ChronicleChannelCapacity = ApprenticesChronicleChannelCapacity,

        MaxStepRetries = ApprenticesMaxStepRetries,

        RetryBackoffSeconds = ApprenticesRetryBackoffSeconds,

        RetryBackoffMaxSeconds = ApprenticesRetryBackoffMaxSeconds,

        EnableShiftingFate = ApprenticesEnableShiftingFate,

        EnableDivineIntervention = ApprenticesEnableDivineIntervention,

        MaxSimulacra = ApprenticesMaxSimulacra,

        MaxRunSteps = ApprenticesMaxRunSteps,

        MaxRunDurationMinutes = ApprenticesMaxRunDurationMinutes,

        MaxReweavesPerRun = ApprenticesMaxReweavesPerRun,

        MaxPendingStarts = ApprenticesMaxPendingStarts,

    };

    public ConclaveSettings BuildConclave() => _conclaveSnapshot with
    {

        Enabled = ConclaveEnabled,

        MaxDelegationDepth = ConclaveMaxDelegationDepth,

        MaxDescendantsPerRoot = ConclaveMaxDescendantsPerRoot,

    };

    [RelayCommand]
    private void AddJob()
    {

        Jobs.Add(new UnseenServantJobViewModel(new UnseenServantJob()));

    }

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
