using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class SecurityPage : ContentPage
{

    public SecurityPage(ConfigurationViewModel viewModel)
    {

        InitializeComponent();

        BindingContext = viewModel;

    }

}
