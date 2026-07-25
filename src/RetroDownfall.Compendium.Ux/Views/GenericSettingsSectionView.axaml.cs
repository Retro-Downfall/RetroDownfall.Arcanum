using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views.Controls;

namespace RetroDownfall.Compendium.Ux.Views;

public partial class GenericSettingsSectionView : UserControl
{

    public static readonly StyledProperty<ConfigSection> SectionProperty =
        AvaloniaProperty.Register<GenericSettingsSectionView, ConfigSection>(nameof(Section));

    public ConfigSection Section
    {

        get => GetValue(SectionProperty);

        set => SetValue(SectionProperty, value);

    }

    public GenericSettingsSectionView()
    {

        InitializeComponent();

        PropertyChanged += OnPropertyChangedHandler;

        DataContextChanged += (_, _) => Rebuild();

    }

    private void OnPropertyChangedHandler(object? sender, AvaloniaPropertyChangedEventArgs e)
    {

        if (e.Property == SectionProperty)
        {

            Rebuild();

        }

    }

    private void Rebuild()
    {

        if (RootPanel is null)
        {

            return;

        }

        RootPanel.Children.Clear();

        ConfigurationViewModel? root = null;

        if (DataContext is ConfigurationViewModel configurationViewModel)
        {

            root = configurationViewModel;

        }
        else if (DataContext is GenericSectionViewModel existing)
        {

            root = existing.Root;

        }

        if (root is null)
        {

            return;

        }

        GenericSectionViewModel sectionVm = root.GetOrCreateGenericSection(Section);

        DataContext = sectionVm;

        TextBlock title = new()
        {
            Text = SectionDescriptors.All.FirstOrDefault(s => s.Section == Section)?.Title ?? Section.ToString(),
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };

        RootPanel.Children.Add(title);

        foreach (IGrouping<string, GenericSettingFieldViewModel> group in sectionVm.Fields
                     .GroupBy(f => f.Group)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {

            TextBlock groupHeader = new()
            {
                Text = group.Key,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 12, 0, 8),
            };

            RootPanel.Children.Add(groupHeader);

            foreach (GenericSettingFieldViewModel field in group)
            {

                RootPanel.Children.Add(CreateFieldControl(field, root));

            }

        }

    }

    private static Control CreateFieldControl(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        SettingDescriptor descriptor = field.Descriptor;

        return descriptor.Kind switch
        {
            SettingKind.Bool => CreateToggle(field),
            SettingKind.Int or SettingKind.Long or SettingKind.Float => CreateStepper(field, root),
            SettingKind.Enum => CreatePicker(field, root),
            SettingKind.StringArray => CreateChips(field),
            SettingKind.Color => CreateColor(field, root),
            SettingKind.Secret => CreateEntry(field, root, isPassword: true),
            _ => CreateEntry(field, root, isPassword: false),
        };

    }

    private static Control CreateToggle(GenericSettingFieldViewModel field)
    {

        LabeledToggle toggle = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            DataContext = field,
        };

        toggle.Bind(LabeledToggle.IsOnProperty, new Binding(nameof(GenericSettingFieldViewModel.BoolValue)));

        return toggle;

    }

    private static Control CreateStepper(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        LabeledStepper stepper = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            Key = field.Descriptor.Key,
            Minimum = field.Descriptor.Min,
            Maximum = field.Descriptor.Max > 0 ? field.Descriptor.Max : 1_000_000,
            Increment = field.Descriptor.Increment > 0 ? field.Descriptor.Increment : 1,
            DataContext = field,
        };

        stepper.Bind(LabeledStepper.ValueProperty, new Binding(nameof(GenericSettingFieldViewModel.NumericValue)));

        stepper.Bind(LabeledStepper.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

        if (field.Descriptor.AllowUnset)
        {
            CheckBox enabled = new()
            {
                Content = "Set an explicit value",
                DataContext = field,
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
            };

            enabled.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(GenericSettingFieldViewModel.IsSet)));
            stepper.Bind(Control.IsEnabledProperty, new Binding(nameof(GenericSettingFieldViewModel.IsSet)));

            return new StackPanel
            {
                Children =
                {
                    enabled,
                    stepper,
                },
            };
        }

        return stepper;

    }

    private static Control CreatePicker(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        LabeledPicker picker = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            Key = field.Descriptor.Key,
            ItemsSource = field.EnumValues.ToList(),
            DataContext = field,
        };

        picker.Bind(LabeledPicker.SelectedItemProperty, new Binding(nameof(GenericSettingFieldViewModel.Value)));

        picker.Bind(LabeledPicker.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

        return picker;

    }

    private static Control CreateChips(GenericSettingFieldViewModel field)
    {

        ChipsEditor chips = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            DataContext = field,
        };

        chips.Bind(ChipsEditor.TextProperty, new Binding(nameof(GenericSettingFieldViewModel.StringValue)));

        return chips;

    }

    private static Control CreateColor(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        LabeledColorEntry entry = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            Key = field.Descriptor.Key,
            Placeholder = field.Descriptor.Placeholder,
            DataContext = field,
        };

        entry.Bind(LabeledColorEntry.TextProperty, new Binding(nameof(GenericSettingFieldViewModel.StringValue)));

        entry.Bind(LabeledColorEntry.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

        return entry;

    }

    private static Control CreateEntry(GenericSettingFieldViewModel field, ConfigurationViewModel root, bool isPassword)
    {

        LabeledEntry entry = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            Key = field.Descriptor.Key,
            Placeholder = field.Descriptor.Placeholder,
            IsPassword = isPassword,
            DataContext = field,
        };

        entry.Bind(LabeledEntry.TextProperty, new Binding(nameof(GenericSettingFieldViewModel.StringValue)));

        entry.Bind(LabeledEntry.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

        return entry;

    }

}
