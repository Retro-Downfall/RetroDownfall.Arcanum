using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Anvil;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.Docking;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;
using RetroDownfall.TheForge.Ux.ViewModels.Hearth;
using RetroDownfall.TheForge.Ux.ViewModels.Treasury;
using RetroDownfall.TheForge.Ux.ViewModels.WarTable;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Root ViewModel for the shell: dock layout, Workbench tab collection, connection
/// commands, and event-based routing via <see cref="INavigationService"/>.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{

    private readonly IArcanumConnection _connection;

    private readonly INavigationService _navigation;

    private readonly Dictionary<DocumentKey, ViewModelBase> _documentsByKey = [];

    private readonly IWorkbenchDocumentFactory _documentFactory;

    private readonly ILogger<MainViewModel>? _logger;

    private bool _atelierLoaded;

    private bool _disposed;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private ViewModelBase? _activeDocument;

    public ObservableCollection<ViewModelBase> OpenDocuments { get; } = [];

    /// <summary>True when no Workbench documents are open; drives the shell's empty-state overlay.</summary>
    public bool HasNoOpenDocuments => OpenDocuments.Count == 0;

    public DockLayoutViewModel DockLayout { get; }

    public AtelierViewModel Atelier { get; }

    public WarTableViewModel WarTable { get; }

    public GatehouseViewModel Gatehouse { get; }

    public TreasuryViewModel Treasury { get; }

    public ArsenalViewModel Arsenal { get; }

    public FoundryFloorViewModel FoundryFloor { get; }

    public HearthViewModel Hearth { get; }

    public AnvilViewModel Anvil { get; }

    public MainViewModel(
        IArcanumConnection connection,
        INavigationService navigation,
        AtelierViewModel atelier,
        WarTableViewModel warTable,
        GatehouseViewModel gatehouse,
        TreasuryViewModel treasury,
        ArsenalViewModel arsenal,
        FoundryFloorViewModel foundryFloor,
        HearthViewModel hearth,
        AnvilViewModel anvil,
        IWorkbenchDocumentFactory documentFactory,
        IForgeSettingsStore settingsStore,
        IOptionsMonitor<ForgeSettings> settings,
        ILogger<MainViewModel>? logger = null)
    {

        _connection = connection;

        _navigation = navigation;

        _logger = logger;

        Title = "The Forge — Inference IDE";

        Atelier = atelier;

        WarTable = warTable;

        Gatehouse = gatehouse;

        Treasury = treasury;

        Arsenal = arsenal;

        FoundryFloor = foundryFloor;

        Hearth = hearth;

        Anvil = anvil;

        _documentFactory = documentFactory;

        _connectionState = connection.State;

        DockLayout = new DockLayoutViewModel(
            settingsStore,
            settings.CurrentValue.LayoutState,
            logger: null);

        WireDockContent();

        ApplyToolVisibility();

        DockLayout.Left.PropertyChanged += OnDockGroupPropertyChanged;

        DockLayout.Right.PropertyChanged += OnDockGroupPropertyChanged;

        DockLayout.Bottom.PropertyChanged += OnDockGroupPropertyChanged;

        DockLayout.Left.Tools.CollectionChanged += OnDockToolsChanged;

        DockLayout.Right.Tools.CollectionChanged += OnDockToolsChanged;

        DockLayout.Bottom.Tools.CollectionChanged += OnDockToolsChanged;

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        OpenDocuments.CollectionChanged += OnOpenDocumentsCollectionChanged;

        _navigation.DocumentOpenRequested += OnDocumentOpenRequested;

        _navigation.DocumentCloseRequested += OnDocumentCloseRequested;

        _navigation.PanelFocusRequested += OnPanelFocusRequested;

    }

    [RelayCommand]
    private void ResetWindowLayout() => DockLayout.ResetLayout();

    [RelayCommand]
    private void ShowAtelier() => DockLayout.ShowTool(DockToolId.Atelier);

    [RelayCommand]
    private void ShowGatehouse() => DockLayout.ShowTool(DockToolId.Gatehouse);

    [RelayCommand]
    private void ShowTreasury() => DockLayout.ShowTool(DockToolId.Treasury);

    [RelayCommand]
    private void ShowArsenal() => DockLayout.ShowTool(DockToolId.Arsenal);

    [RelayCommand]
    private void ShowWarTable() => DockLayout.ShowTool(DockToolId.WarTable);

    [RelayCommand]
    private void ShowOutput() => DockLayout.ShowTool(DockToolId.Output);

    [RelayCommand]
    private void ShowLogs() => DockLayout.ShowTool(DockToolId.Logs);

    [RelayCommand]
    private void ShowHearth() => DockLayout.ShowTool(DockToolId.Hearth);

    [RelayCommand]
    private void Connect()
    {

        _connection.Connect();

    }

    [RelayCommand]
    private void Disconnect()
    {

        _connection.Disconnect();

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        DockLayout.Left.PropertyChanged -= OnDockGroupPropertyChanged;

        DockLayout.Right.PropertyChanged -= OnDockGroupPropertyChanged;

        DockLayout.Bottom.PropertyChanged -= OnDockGroupPropertyChanged;

        DockLayout.Left.Tools.CollectionChanged -= OnDockToolsChanged;

        DockLayout.Right.Tools.CollectionChanged -= OnDockToolsChanged;

        DockLayout.Bottom.Tools.CollectionChanged -= OnDockToolsChanged;

        _connection.PropertyChanged -= OnConnectionPropertyChanged;

        OpenDocuments.CollectionChanged -= OnOpenDocumentsCollectionChanged;

        _navigation.DocumentOpenRequested -= OnDocumentOpenRequested;

        _navigation.DocumentCloseRequested -= OnDocumentCloseRequested;

        _navigation.PanelFocusRequested -= OnPanelFocusRequested;

        foreach (ViewModelBase document in OpenDocuments.ToArray())
        {

            if (document is IDisposable disposable)
            {

                disposable.Dispose();

            }

        }

        OpenDocuments.Clear();

        _documentsByKey.Clear();

        DockLayout.Dispose();

        WarTable.Dispose();

        Gatehouse.Dispose();

        Hearth.Dispose();

        Anvil.Dispose();

        // FoundryFloorViewModel is a DI singleton — leave disposal to ServiceProvider.

        GC.SuppressFinalize(this);

    }

    private void WireDockContent()
    {

        DockLayout.SetContent(DockToolId.Atelier, Atelier);

        DockLayout.SetContent(DockToolId.Gatehouse, Gatehouse);

        DockLayout.SetContent(DockToolId.Treasury, Treasury);

        DockLayout.SetContent(DockToolId.Arsenal, Arsenal);

        DockLayout.SetContent(DockToolId.WarTable, WarTable);

        DockLayout.SetContent(DockToolId.Output, new OutputToolContent(FoundryFloor));

        DockLayout.SetContent(DockToolId.Logs, new LogsToolContent(FoundryFloor));

        DockLayout.SetContent(DockToolId.Hearth, Hearth);

    }

    private void ApplyToolVisibility()
    {

        string? rightSelected = DockLayout.Right.SelectedTool?.ToolId;

        string? bottomSelected = DockLayout.Bottom.SelectedTool?.ToolId;

        string? leftSelected = DockLayout.Left.SelectedTool?.ToolId;

        Gatehouse.IsVisible =
            rightSelected == DockToolId.Gatehouse
            || bottomSelected == DockToolId.Gatehouse
            || leftSelected == DockToolId.Gatehouse;

        WarTable.IsVisible =
            rightSelected == DockToolId.WarTable
            || bottomSelected == DockToolId.WarTable
            || leftSelected == DockToolId.WarTable;

        bool foundryVisible = !DockLayout.Bottom.IsCollapsed
            && DockLayout.Bottom.Tools.Any(t =>
                t.ToolId is DockToolId.Output or DockToolId.Logs);

        // Also stream logs if Output/Logs were moved to another region and selected.
        if (!foundryVisible)
        {

            foundryVisible =
                leftSelected is DockToolId.Output or DockToolId.Logs
                || rightSelected is DockToolId.Output or DockToolId.Logs;

        }

        FoundryFloor.IsVisible = foundryVisible;

    }

    private void OnDockToolsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyToolVisibility();

    private void OnDockGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(DockGroupViewModel.SelectedTool)
            or nameof(DockGroupViewModel.IsCollapsed)
            or nameof(DockGroupViewModel.Tools))
        {

            ApplyToolVisibility();

        }

    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(IArcanumConnection.State))
        {

            ConnectionState = _connection.State;

            if (_connection.State == ConnectionState.Connected && !_atelierLoaded)
            {

                _atelierLoaded = true;

                TaskUtilities.FireAndForget(Atelier.RefreshCommand.ExecuteAsync(null), _logger);

            }

        }

    }

    private void OnOpenDocumentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        OnPropertyChanged(nameof(HasNoOpenDocuments));

    }

    private void OnDocumentOpenRequested(DocumentKind kind, string id)
    {

        DocumentKey key = new(kind, id);

        if (!_documentsByKey.TryGetValue(key, out ViewModelBase? document))
        {

            document = _documentFactory.Create(kind, id);

            _documentsByKey.Add(key, document);

            OpenDocuments.Add(document);

        }

        ActiveDocument = document;

    }

    private void OnDocumentCloseRequested(DocumentKind kind, string id)
    {

        DocumentKey key = new(kind, id);

        if (!_documentsByKey.Remove(key, out ViewModelBase? document))
        {

            return;

        }

        int index = OpenDocuments.IndexOf(document);

        OpenDocuments.Remove(document);

        if (document is IDisposable disposable)
        {

            disposable.Dispose();

        }

        if (ReferenceEquals(ActiveDocument, document))
        {

            ActiveDocument = OpenDocuments.Count == 0
                ? null
                : OpenDocuments[Math.Clamp(index, 0, OpenDocuments.Count - 1)];

        }

    }

    private void OnPanelFocusRequested(PanelKind panel)
    {

        string? toolId = panel switch
        {
            PanelKind.Atelier => DockToolId.Atelier,
            PanelKind.Gatehouse => DockToolId.Gatehouse,
            PanelKind.Treasury => DockToolId.Treasury,
            PanelKind.Arsenal => DockToolId.Arsenal,
            PanelKind.WarTable => DockToolId.WarTable,
            PanelKind.FoundryFloor => DockToolId.Output,
            PanelKind.Hearth => DockToolId.Hearth,
            _ => null,
        };

        if (toolId is not null)
        {

            DockLayout.FocusTool(toolId);

            ApplyToolVisibility();

        }

    }

}
