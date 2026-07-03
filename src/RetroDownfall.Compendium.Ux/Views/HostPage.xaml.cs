using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class HostPage : ContentPage
{

    public HostPage(ConfigurationViewModel viewModel)
    {

        InitializeComponent();

        BindingContext = viewModel;

    }

}
