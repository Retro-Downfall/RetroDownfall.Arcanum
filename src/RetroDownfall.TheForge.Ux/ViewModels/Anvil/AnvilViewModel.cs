using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Anvil;

/// <summary>
/// Phase 3 status-bar ViewModel for The Anvil. It mirrors the live connection state now; Phase 9
/// expands this into budget, ward, apprentice, MCP, campaign, model, and mana aggregates.
/// </summary>
public sealed partial class AnvilViewModel : ViewModelBase
{

    [ObservableProperty]
    private ConnectionState _connectionState;

    private readonly IArcanumConnection _connection;

    public AnvilViewModel(IArcanumConnection connection)
    {

        _connection = connection;

        Title = "The Anvil";

        _connectionState = connection.State;

        _connection.PropertyChanged += OnConnectionPropertyChanged;

    }

    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Connected => "Arcanum connected",
        ConnectionState.Connecting => "Seeking Arcanum...",
        ConnectionState.Error => "Arcanum unreachable",
        _ => "Arcanum disconnected",
    };

    public string ActiveCampaignName => "No campaign";

    public string ActiveModelName => "No model";

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(IArcanumConnection.State))
        {

            ConnectionState = _connection.State;

            OnPropertyChanged(nameof(ConnectionStatusText));

        }

    }

    partial void OnConnectionStateChanged(ConnectionState value)
    {

        OnPropertyChanged(nameof(ConnectionStatusText));

    }

}
