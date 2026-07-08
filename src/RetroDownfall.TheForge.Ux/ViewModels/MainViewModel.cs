using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Anvil;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;
using RetroDownfall.TheForge.Ux.ViewModels.Hearth;
using RetroDownfall.TheForge.Ux.ViewModels.Treasury;
using RetroDownfall.TheForge.Ux.ViewModels.WarTable;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Root ViewModel for the Phase 3 shell: panel visibility, Workbench tab collection, connection
/// commands, and event-based routing via <see cref="INavigationService"/>. Feature-rich panel
/// content lands in later milestones, but the shell layout and navigation seams are in place now.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{

    private readonly IArcanumConnection _connection;

    private readonly INavigationService _navigation;

    private readonly Dictionary<DocumentKey, ViewModelBase> _documentsByKey = [];

    private readonly IWorkbenchDocumentFactory _documentFactory;

    private bool _atelierLoaded;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private bool _isAtelierVisible = true;

    [ObservableProperty]
    private bool _isRightPanelVisible = true;

    [ObservableProperty]
    private bool _isFoundryFloorVisible = true;

    [ObservableProperty]
    private ViewModelBase? _activeDocument;

    public ObservableCollection<ViewModelBase> OpenDocuments { get; } = [];

    /// <summary>True when no Workbench documents are open; drives the shell's empty-state overlay.</summary>
    public bool HasNoOpenDocuments => OpenDocuments.Count == 0;

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
        IWorkbenchDocumentFactory documentFactory)
    {

        _connection = connection;

        _navigation = navigation;

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

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        OpenDocuments.CollectionChanged += OnOpenDocumentsCollectionChanged;

        _navigation.DocumentOpenRequested += OnDocumentOpenRequested;

        _navigation.DocumentCloseRequested += OnDocumentCloseRequested;

        _navigation.PanelFocusRequested += OnPanelFocusRequested;

    }

    [RelayCommand]
    private void ToggleAtelier()
    {

        IsAtelierVisible = !IsAtelierVisible;

    }

    [RelayCommand]
    private void ToggleRightPanel()
    {

        IsRightPanelVisible = !IsRightPanelVisible;

    }

    [RelayCommand]
    private void ToggleFoundryFloor()
    {

        IsFoundryFloorVisible = !IsFoundryFloorVisible;

    }

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

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(IArcanumConnection.State))
        {

            ConnectionState = _connection.State;

            if (_connection.State == ConnectionState.Connected && !_atelierLoaded)
            {

                _atelierLoaded = true;

                _ = Atelier.RefreshCommand.ExecuteAsync(null);

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

        switch (panel)
        {
            case PanelKind.Atelier:
                IsAtelierVisible = true;
                break;

            case PanelKind.Gatehouse:
            case PanelKind.Treasury:
            case PanelKind.Arsenal:
                IsRightPanelVisible = true;
                break;

            case PanelKind.FoundryFloor:
            case PanelKind.Hearth:
                IsFoundryFloorVisible = true;
                break;

            case PanelKind.Workbench:
            case PanelKind.WarTable:
            case PanelKind.Anvil:
            default:
                break;
        }

    }

}
