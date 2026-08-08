using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using System.ComponentModel;
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

        DataContextChanged += OnDataContextChangedHandler;

        DetachedFromLogicalTree += (_, _) => ObserveSection(null);

        AttachedToLogicalTree += (_, _) =>
        {
            ObserveSection(DataContext as GenericSectionViewModel);
            _lastRebuiltSection = null;
            Rebuild();
        };

    }

    private void OnPropertyChangedHandler(object? sender, AvaloniaPropertyChangedEventArgs e)
    {

        if (e.Property == SectionProperty)
        {

            Rebuild();

        }

    }

    private ConfigSection? _lastRebuiltSection;

    private GenericSectionViewModel? _observedSection;

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        GenericSectionViewModel? next = DataContext as GenericSectionViewModel;

        ObserveSection(next);

        if (next is null)
        {
            _lastRebuiltSection = null;
        }

        Rebuild();
    }

    private void ObserveSection(GenericSectionViewModel? next)
    {
        if (!ReferenceEquals(_observedSection, next))
        {
            if (_observedSection is not null)
            {
                _observedSection.PropertyChanged -= OnSectionPropertyChanged;
            }

            _observedSection = next;

            if (_observedSection is not null)
            {
                _observedSection.PropertyChanged += OnSectionPropertyChanged;
            }
        }
    }

    private void OnSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GenericSectionViewModel.Fields))
        {
            return;
        }

        _lastRebuiltSection = null;

        Rebuild();
    }

    private void Rebuild()
    {
        if (RootPanel is null || Section == _lastRebuiltSection)
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

        // Mark the successful build before replacing DataContext because that assignment raises
        // DataContextChanged recursively. Do not cache an unsuccessful attempt: controls commonly
        // receive Section before their inherited DataContext during template construction.
        _lastRebuiltSection = Section;

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
            SettingKind.StringArray => CreateChips(field, root),
            SettingKind.Dictionary => CreateDictionaryEditor(field, root),
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

    private static Control CreateChips(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        ChipsEditor chips = new()
        {
            Label = field.Descriptor.Label,
            Description = field.Descriptor.Description,
            Key = field.Descriptor.Key,
            DataContext = field,
        };

        chips.Bind(ChipsEditor.TextProperty, new Binding(nameof(GenericSettingFieldViewModel.StringValue)));

        chips.Bind(ChipsEditor.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

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

    private static Control CreateDictionaryEditor(GenericSettingFieldViewModel field, ConfigurationViewModel root)
    {

        TextBox editor = new()
        {
            AcceptsReturn = true,
            MinHeight = 180,
            TextWrapping = TextWrapping.NoWrap,
            DataContext = field,
        };

        editor.Bind(
            TextBox.TextProperty,
            new Binding(nameof(GenericSettingFieldViewModel.StringValue)));

        // Free-text JSON needs its own error surface; an unattributed parse failure at save time names
        // neither the field nor the section.
        ValidationMessageBlock validation = new()
        {
            Key = field.Descriptor.Key,
        };

        validation.Bind(ValidationMessageBlock.ValidationErrorsProperty, new Binding(nameof(ConfigurationViewModel.ValidationErrorsByPointer))
        {
            Source = root,
        });

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = field.Descriptor.Label,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = field.Descriptor.Description,
                    TextWrapping = TextWrapping.Wrap,
                },
                editor,
                validation,
            },
        };

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
