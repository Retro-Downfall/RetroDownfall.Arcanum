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

    private bool _discardConfirmed;

    public MainWindow()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        Closing += OnWindowClosing;

    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {

        if (_discardConfirmed || DataContext is not ConfigurationViewModel vm || !vm.IsDirty)
        {

            return;

        }

        // Pending edits live only in memory, so closing must ask before throwing them away.
        e.Cancel = true;

        try
        {

            if (!await vm.ConfirmDiscardOnExitAsync().ConfigureAwait(true))
            {

                return;

            }

        }
        catch (Exception)
        {

            // The confirmation could not be shown; keep the window (and the unsaved edits) open.
            return;

        }

        _discardConfirmed = true;

        Close();

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
            ConfigSection.Presets => new PresetsPage(),
            ConfigSection.Host => new HostPage(),
            ConfigSection.Providers => new ProvidersPage(),
            ConfigSection.Daemon => new DaemonPage(),
            ConfigSection.Cli => new CliPage(),
            _ => new GenericSettingsSectionView { Section = section },
        };

    }

    private void OnExitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {

        Close();

    }

}
