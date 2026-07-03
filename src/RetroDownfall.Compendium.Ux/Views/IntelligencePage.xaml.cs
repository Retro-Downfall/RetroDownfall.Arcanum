using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class IntelligencePage : ContentPage
{

    public IntelligencePage(ConfigurationViewModel viewModel)
    {

        InitializeComponent();

        BindingContext = viewModel;

    }

}
