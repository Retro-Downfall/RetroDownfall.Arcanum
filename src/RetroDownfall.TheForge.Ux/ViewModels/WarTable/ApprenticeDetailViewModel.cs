using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>
/// Detail pane for one apprentice: plan steps, lifecycle actions, Conclave lineage walk, and Chronicle.
/// </summary>
public sealed partial class ApprenticeDetailViewModel : ViewModelBase, IDisposable
{

    private readonly IWarTableDataSource _dataSource;

    private bool _disposed;

    [ObservableProperty]
    private ApprenticeDetailDto? _detail;

    [ObservableProperty]
    private string? _interveneGuidance;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private LineageNodeViewModel? _lineageRoot;

    public ApprenticeDetailViewModel(Guid apprenticeId, IWarTableDataSource dataSource)
    {

        ApprenticeId = apprenticeId;

        _dataSource = dataSource;

        Title = $"Apprentice {apprenticeId:D}";

        Chronicle = new ChronicleViewModel(apprenticeId, dataSource);

    }

    public Guid ApprenticeId { get; }

    public Guid Id => ApprenticeId;

    public ChronicleViewModel Chronicle { get; }

    public ObservableCollection<PlanStepViewModel> PlanSteps { get; } = [];

    public ObservableCollection<LineageNodeViewModel> LineageRoots { get; } = [];

    public string Name => Detail?.Name ?? Title;

    public string Status => Detail?.Status ?? "Unknown";

    public string Goal => Detail?.Goal ?? string.Empty;

    public string WorkspacePath => Detail?.WorkspacePath ?? string.Empty;

    public void Activate() => Chronicle.Start();

    public void Deactivate() => Chronicle.Stop();

    [RelayCommand]
    public Task LoadAsync(CancellationToken cancellationToken) =>
        LoadCoreAsync(initialDetail: null, cancellationToken);

    internal Task LoadKnownDetailAsync(
        ApprenticeDetailDto detail,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(detail);

        return LoadCoreAsync(detail, cancellationToken);

    }

    private async Task LoadCoreAsync(
        ApprenticeDetailDto? initialDetail,
        CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            Detail = initialDetail ?? await _dataSource
                .GetApprenticeAsync(ApprenticeId, cancellationToken)
                .ConfigureAwait(true);

            if (Detail is not null)
            {

                Title = string.IsNullOrWhiteSpace(Detail.Name) ? $"Apprentice {ApprenticeId:D}" : Detail.Name;

            }

            SyncPlan();

            OnPropertyChanged(nameof(Name));

            OnPropertyChanged(nameof(Status));

            OnPropertyChanged(nameof(Goal));

            OnPropertyChanged(nameof(WorkspacePath));

            await LoadLineageAsync(cancellationToken).ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await RunActionAsync(() => _dataSource.StartAsync(ApprenticeId, cancellationToken), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    public async Task PauseAsync(CancellationToken cancellationToken) =>
        await RunActionAsync(() => _dataSource.PauseAsync(ApprenticeId, cancellationToken), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    public async Task ResumeAsync(CancellationToken cancellationToken) =>
        await RunActionAsync(() => _dataSource.ResumeAsync(ApprenticeId, cancellationToken), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    public async Task CancelAsync(CancellationToken cancellationToken) =>
        await RunActionAsync(() => _dataSource.CancelAsync(ApprenticeId, cancellationToken), cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    public async Task ReweaveAsync(CancellationToken cancellationToken)
    {

        if (Detail is null || Detail.Plan.Count == 0)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            ApprenticeDetailDto? updated = await _dataSource
                .ReweaveAsync(ApprenticeId, new ReweaveApprenticeRequest(Detail.Plan), cancellationToken)
                .ConfigureAwait(true);

            if (updated is not null)
            {

                Detail = updated;

                SyncPlan();

                OnPropertyChanged(nameof(Status));

            }
            else
            {

                LastError = "Reweave failed.";

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task InterveneAsync(CancellationToken cancellationToken)
    {

        string guidance = InterveneGuidance?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(guidance))
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            bool ok = await _dataSource
                .InterveneAsync(ApprenticeId, new InterveneApprenticeRequest(guidance), cancellationToken)
                .ConfigureAwait(true);

            if (ok)
            {

                InterveneGuidance = string.Empty;

                await LoadAsync(cancellationToken).ConfigureAwait(true);

            }
            else
            {

                LastError = "Intervene failed.";

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        Chronicle.Dispose();

        GC.SuppressFinalize(this);

    }

    private async Task RunActionAsync(Func<Task<bool>> action, CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            bool ok = await action().ConfigureAwait(true);

            if (ok)
            {

                await LoadAsync(cancellationToken).ConfigureAwait(true);

            }
            else
            {

                LastError = "Action failed.";

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    private void SyncPlan()
    {

        PlanSteps.Clear();

        if (Detail is null)
        {

            return;

        }

        foreach (PlanStep step in Detail.Plan)
        {

            PlanSteps.Add(new PlanStepViewModel(step));

        }

    }

    private async Task LoadLineageAsync(CancellationToken cancellationToken)
    {

        LineageRoots.Clear();

        LineageRoot = null;

        IReadOnlyList<ApprenticeDetailDto> chain = await _dataSource
            .GetLineageAsync(ApprenticeId, cancellationToken)
            .ConfigureAwait(true);

        if (chain.Count == 0)
        {

            return;

        }

        LineageNodeViewModel? parent = null;

        foreach (ApprenticeDetailDto node in chain.Reverse())
        {

            LineageNodeViewModel vm = new(node);

            if (parent is null)
            {

                LineageRoots.Add(vm);

                LineageRoot = vm;

            }
            else
            {

                parent.Children.Add(vm);

            }

            parent = vm;

        }

    }

}
