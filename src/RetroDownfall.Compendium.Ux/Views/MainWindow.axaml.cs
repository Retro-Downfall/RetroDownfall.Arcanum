using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views.Controls;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class MainWindow : Window
{

    private readonly Dictionary<ConfigSection, TabItem> _openTabs = new();

    public MainWindow()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {

        if (DataContext is ConfigurationViewModel vm)
        {

            vm.PropertyChanged += (_, args) =>
            {

                if (args.PropertyName == nameof(ConfigurationViewModel.SelectedSection))
                {

                    OpenOrFocusSection(vm.SelectedSection);

                }

            };

            if (SectionList is not null)
            {

                SectionList.SelectionChanged += OnSectionSelectionChanged;

            }

            OpenOrFocusSection(vm.SelectedSection);

        }

    }

    private void OnSectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {

        if (DataContext is not ConfigurationViewModel vm)
        {

            return;

        }

        if (SectionList.SelectedItem is SectionDescriptor descriptor)
        {

            vm.SelectSectionCommand.Execute(descriptor);

            OpenOrFocusSection(descriptor.Section);

        }

    }

    private void OpenOrFocusSection(ConfigSection section)
    {

        if (_openTabs.TryGetValue(section, out TabItem? existing))
        {

            DocumentTabs.SelectedItem = existing;

            return;

        }

        Control content = CreateSectionContent(section);

        if (DataContext is not null)
        {

            content.DataContext = DataContext;

        }

        TabItem tab = new()
        {
            Header = SectionDescriptors.All.FirstOrDefault(s => s.Section == section)?.Title ?? section.ToString(),
            Content = content,
            Tag = section,
        };

        _openTabs[section] = tab;

        DocumentTabs.Items.Add(tab);

        DocumentTabs.SelectedItem = tab;

    }

    private static Control CreateSectionContent(ConfigSection section)
    {

        return section switch
        {
            ConfigSection.Host => new HostPage(),
            ConfigSection.Server => new ServerPage(),
            ConfigSection.Providers => new ProvidersPage(),
            ConfigSection.Intelligence => new IntelligencePage(),
            ConfigSection.Mcp => new McpPage(),
            ConfigSection.Orchestration => new OrchestrationPage(),
            ConfigSection.Security => new SecurityPage(),
            ConfigSection.CommLink => new CommLinkPage(),
            ConfigSection.Storage => new StoragePage(),
            ConfigSection.Forge => new TheForgePage(),
            ConfigSection.ProvingGrounds => new ProvingGroundsPage(),
            ConfigSection.Cli => new CliPage(),
            ConfigSection.Scrying => new ScryingPage(),
            _ => new GenericSettingsSectionView { Section = section },
        };

    }

    private void OnExitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {

        Close();

    }

}
