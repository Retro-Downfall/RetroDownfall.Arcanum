using System.Collections;

using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledPicker : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledPicker, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledPicker, string>(nameof(Description));

    public static readonly StyledProperty<string> PickerTitleProperty =
        AvaloniaProperty.Register<LabeledPicker, string>(nameof(PickerTitle));

    public static readonly StyledProperty<IList?> ItemsSourceProperty =
        AvaloniaProperty.Register<LabeledPicker, IList?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<LabeledPicker, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> ValidationMessageProperty =
        AvaloniaProperty.Register<LabeledPicker, string>(nameof(ValidationMessage));

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<LabeledPicker, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<LabeledPicker, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

    public string Label
    {

        get => GetValue(LabelProperty);

        set => SetValue(LabelProperty, value);

    }

    public string Description
    {

        get => GetValue(DescriptionProperty);

        set => SetValue(DescriptionProperty, value);

    }

    public string PickerTitle
    {

        get => GetValue(PickerTitleProperty);

        set => SetValue(PickerTitleProperty, value);

    }

    public IList? ItemsSource
    {

        get => GetValue(ItemsSourceProperty);

        set => SetValue(ItemsSourceProperty, value);

    }

    public object? SelectedItem
    {

        get => GetValue(SelectedItemProperty);

        set => SetValue(SelectedItemProperty, value);

    }

    public string ValidationMessage
    {

        get => GetValue(ValidationMessageProperty);

        set => SetValue(ValidationMessageProperty, value);

    }

    public string Key
    {

        get => GetValue(KeyProperty);

        set => SetValue(KeyProperty, value);

    }

    public IReadOnlyDictionary<string, string>? ValidationErrors
    {

        get => GetValue(ValidationErrorsProperty);

        set => SetValue(ValidationErrorsProperty, value);

    }

    static LabeledPicker()
    {

        KeyProperty.Changed.AddClassHandler<LabeledPicker>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<LabeledPicker>((control, _) => control.RefreshValidation());

    }

    public LabeledPicker()
    {

        InitializeComponent();

    }

    private void RefreshValidation()
    {

        if (string.IsNullOrEmpty(Key) || ValidationErrors is null)

        {

            ValidationMessage = string.Empty;

            return;

        }

        ValidationMessage = ValidationErrors.TryGetValue(Key, out string? message) ? message : string.Empty;

    }

}
