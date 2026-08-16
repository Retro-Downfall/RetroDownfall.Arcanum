using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using System.Collections.Immutable;
using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Covenant;
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

        // A per-element template names no single path Save can write. Offering a box here accepts the
        // operator's text and drops it while the save still reports success.
        if (field.CollectionTemplatePath is string collectionPath)
        {

            return CreateCollectionTemplateNotice(descriptor, collectionPath);

        }

        // Disclosure first, then the control it warns about. A warning rendered underneath the
        // switch is read after the decision, which makes it a receipt rather than a warning.
        if (descriptor.HelpRoute is SettingHelpRoute route)
        {

            return CreateDisclosedField(field, root, route);

        }

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

    private static Control CreateCollectionTemplateNotice(SettingDescriptor descriptor, string collectionPath)
    {

        string leaf = descriptor.Key[(collectionPath.Length + 1)..];

        return new StackPanel
        {
            Spacing = 4,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock
                {
                    Text = descriptor.Label,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = descriptor.Description,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "Compendium has no per-entry editor for this list. Set it from a terminal with"
                        + $" 'arcanum config set {collectionPath}.0.{leaf} <value>', or"
                        + $" 'arcanum config set {collectionPath} <json>' to create the entries.",
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyle.Italic,
                },
            },
        };

    }

    /// <summary>
    /// Renders one setting whose consequences reach outside this machine: the shared disclosure, then
    /// every resolved help action, then the control itself.
    /// </summary>
    /// <remarks>
    /// The order is the contract, and the test asserts construction order rather than the presence of
    /// the pieces. The help targets are resolved here rather than declared on the descriptor because
    /// the right retention page depends on which providers this installation actually has configured,
    /// and a URI baked into a static table is one nobody re-evaluates when that changes (§10.18).
    ///
    /// <para>Only a <see cref="CovenantRetentionHelpKind.ProviderRetentionDocumentation"/> target is
    /// handed to the URI launcher. The other two arms are an in-app page and a repository document;
    /// treating all three as links is how an internal route reaches a browser.</para>
    /// </remarks>
    private static Control CreateDisclosedField(
        GenericSettingFieldViewModel field,
        ConfigurationViewModel root,
        SettingHelpRoute route)
    {

        StackPanel panel = new()
        {
            Spacing = 6,
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
        };

        panel.Children.Add(new TextBlock
        {
            Text = field.Descriptor.Description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });

        foreach (CovenantRetentionHelpTarget target in ResolveHelpTargets(route, root))
        {

            panel.Children.Add(CreateHelpAction(target));

        }

        panel.Children.Add(CreateToggle(field));

        return panel;

    }

    private static ImmutableArray<CovenantRetentionHelpTarget> ResolveHelpTargets(
        SettingHelpRoute route,
        ConfigurationViewModel root) =>
        route switch
        {
            SettingHelpRoute.ConfiguredProviderRetention =>
                CovenantExternalRetentionDisclosure.ResolveHelpTargets(root.Providers.BuildProviders()),
            _ => [],
        };

    private static Control CreateHelpAction(CovenantRetentionHelpTarget target)
    {

        Button action = new()
        {
            Content = DescribeHelpTarget(target),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Tag = target,
        };

        action.Click += (sender, args) =>
        {

            if (target.Kind != CovenantRetentionHelpKind.ProviderRetentionDocumentation)
            {

                return;

            }

            if (sender is Button clicked
                && TopLevel.GetTopLevel(clicked)?.Launcher is { } launcher)
            {

                _ = launcher.LaunchUriAsync(new Uri(target.Uri));

            }

        };

        return action;

    }

    private static string DescribeHelpTarget(CovenantRetentionHelpTarget target) =>
        target.Kind switch
        {
            CovenantRetentionHelpKind.ProviderRetentionDocumentation =>
                $"Read {target.Provider}'s data retention policy",
            CovenantRetentionHelpKind.ConfiguredProvidersPage =>
                $"{target.Provider} is not a provider Arcanum recognizes — review it under Providers",
            _ => "What Arcanum can and cannot erase",
        };

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
