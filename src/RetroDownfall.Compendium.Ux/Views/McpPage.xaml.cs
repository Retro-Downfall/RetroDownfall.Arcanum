using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class McpPage : ContentPage
{

    public McpPage(ConfigurationViewModel viewModel)
    {

        InitializeComponent();

        BindingContext = viewModel;

    }

}
