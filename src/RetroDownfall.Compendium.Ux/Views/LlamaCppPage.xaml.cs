using RetroDownfall.Compendium.Ux.ViewModels;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class LlamaCppPage : ContentPage
{

    public LlamaCppPage(ConfigurationViewModel viewModel)
    {

        InitializeComponent();

        BindingContext = viewModel;

    }

}
