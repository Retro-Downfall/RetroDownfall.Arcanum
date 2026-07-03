using System.Collections;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledPicker : ContentView
{

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label),
        typeof(string),
        typeof(LabeledPicker),
        string.Empty,
        propertyChanged: OnLabelChanged);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description),
        typeof(string),
        typeof(LabeledPicker),
        string.Empty,
        propertyChanged: OnDescriptionChanged);

    public static readonly BindableProperty PickerTitleProperty = BindableProperty.Create(
        nameof(PickerTitle),
        typeof(string),
        typeof(LabeledPicker),
        string.Empty);

    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource),
        typeof(IList),
        typeof(LabeledPicker),
        defaultValue: null);

    public static readonly BindableProperty SelectedItemProperty = BindableProperty.Create(
        nameof(SelectedItem),
        typeof(object),
        typeof(LabeledPicker),
        null,
        BindingMode.TwoWay);

    public static readonly BindableProperty ValidationMessageProperty = BindableProperty.Create(
        nameof(ValidationMessage),
        typeof(string),
        typeof(LabeledPicker),
        string.Empty,
        propertyChanged: OnValidationMessageChanged);

    public static readonly BindableProperty KeyProperty = BindableProperty.Create(
        nameof(Key),
        typeof(string),
        typeof(LabeledPicker),
        string.Empty,
        propertyChanged: OnKeyOrErrorsChanged);

    public static readonly BindableProperty ValidationErrorsProperty = BindableProperty.Create(
        nameof(ValidationErrors),
        typeof(IReadOnlyDictionary<string, string>),
        typeof(LabeledPicker),
        null,
        propertyChanged: OnKeyOrErrorsChanged);

    public string Label
    {

        get => (string)GetValue(LabelProperty);

        set => SetValue(LabelProperty, value);

    }

    public string Description
    {

        get => (string)GetValue(DescriptionProperty);

        set => SetValue(DescriptionProperty, value);

    }

    public string PickerTitle
    {

        get => (string)GetValue(PickerTitleProperty);

        set => SetValue(PickerTitleProperty, value);

    }

    public IList? ItemsSource
    {

        get => (IList?)GetValue(ItemsSourceProperty);

        set => SetValue(ItemsSourceProperty, value);

    }

    public object? SelectedItem
    {

        get => GetValue(SelectedItemProperty);

        set => SetValue(SelectedItemProperty, value);

    }

    public string ValidationMessage
    {

        get => (string)GetValue(ValidationMessageProperty);

        set => SetValue(ValidationMessageProperty, value);

    }

    public string Key
    {

        get => (string)GetValue(KeyProperty);

        set => SetValue(KeyProperty, value);

    }

    public IReadOnlyDictionary<string, string>? ValidationErrors
    {

        get => (IReadOnlyDictionary<string, string>?)GetValue(ValidationErrorsProperty);

        set => SetValue(ValidationErrorsProperty, value);

    }

    public LabeledPicker()
    {

        InitializeComponent();

        LabelControl.Text = Label;

        DescriptionControl.Text = Description;

        HintControl.IsVisible = false;

    }

    private static void OnLabelChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledPicker control && control.LabelControl is not null)
        {

            control.LabelControl.Text = (string)newValue;

        }

    }

    private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledPicker control && control.DescriptionControl is not null)
        {

            string text = (string)newValue;

            control.DescriptionControl.Text = text;

            control.DescriptionControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

    private static void OnValidationMessageChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledPicker control && control.HintControl is not null)
        {

            string text = (string)newValue;

            control.HintControl.Text = text;

            control.HintControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

    private static void OnKeyOrErrorsChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledPicker control)
        {

            control.RefreshValidation();

        }

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
