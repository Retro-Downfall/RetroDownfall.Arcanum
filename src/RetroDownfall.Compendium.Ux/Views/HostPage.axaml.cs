using Avalonia.Controls;

using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class HostPage : UserControl
{

    public HostPage()
    {

        InitializeComponent();

        // The generic editor wires this for every chips control it builds; a polished page has to do
        // it for the ones it declares. Text typed into the entry but not yet committed is still an
        // operator edit, and without this Save stays disabled underneath it and closing the window
        // discards it with no confirmation.
        CorsAllowedOriginsEditor.PendingInputChanged += OnCorsAllowedOriginsPendingInputChanged;

    }

    private void OnCorsAllowedOriginsPendingInputChanged(object? sender, EventArgs args) =>
        (DataContext as ConfigurationViewModel)?.MarkDirty();

}
