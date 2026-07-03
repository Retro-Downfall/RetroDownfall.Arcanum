namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledStepper : ContentView
{

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label),
        typeof(string),
        typeof(LabeledStepper),
        string.Empty,
        propertyChanged: OnLabelChanged);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description),
        typeof(string),
        typeof(LabeledStepper),
        string.Empty,
        propertyChanged: OnDescriptionChanged);

    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value),
        typeof(double),
        typeof(LabeledStepper),
        0.0,
        BindingMode.TwoWay,
        propertyChanged: OnValueChanged);

    public static readonly BindableProperty MinimumProperty = BindableProperty.Create(
        nameof(Minimum),
        typeof(double),
        typeof(LabeledStepper),
        0.0);

    public static readonly BindableProperty MaximumProperty = BindableProperty.Create(
        nameof(Maximum),
        typeof(double),
        typeof(LabeledStepper),
        100.0);

    public static readonly BindableProperty IncrementProperty = BindableProperty.Create(
        nameof(Increment),
        typeof(double),
        typeof(LabeledStepper),
        1.0);

    public static readonly BindableProperty ValidationMessageProperty = BindableProperty.Create(
        nameof(ValidationMessage),
        typeof(string),
        typeof(LabeledStepper),
        string.Empty,
        propertyChanged: OnValidationMessageChanged);

    public static readonly BindableProperty KeyProperty = BindableProperty.Create(
        nameof(Key),
        typeof(string),
        typeof(LabeledStepper),
        string.Empty,
        propertyChanged: OnKeyOrErrorsChanged);

    public static readonly BindableProperty ValidationErrorsProperty = BindableProperty.Create(
        nameof(ValidationErrors),
        typeof(IReadOnlyDictionary<string, string>),
        typeof(LabeledStepper),
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

    public double Value
    {

        get => (double)GetValue(ValueProperty);

        set => SetValue(ValueProperty, value);

    }

    public double Minimum
    {

        get => (double)GetValue(MinimumProperty);

        set => SetValue(MinimumProperty, value);

    }

    public double Maximum
    {

        get => (double)GetValue(MaximumProperty);

        set => SetValue(MaximumProperty, value);

    }

    public double Increment
    {

        get => (double)GetValue(IncrementProperty);

        set => SetValue(IncrementProperty, value);

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

    public LabeledStepper()
    {

        InitializeComponent();

        LabelControl.Text = Label;

        DescriptionControl.Text = Description;

        ValueControl.Text = Value.ToString(System.Globalization.CultureInfo.CurrentCulture);

        HintControl.IsVisible = false;

    }

    private static void OnLabelChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledStepper control && control.LabelControl is not null)
        {

            control.LabelControl.Text = (string)newValue;

        }

    }

    private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledStepper control && control.DescriptionControl is not null)
        {

            string text = (string)newValue;

            control.DescriptionControl.Text = text;

            control.DescriptionControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

    private static void OnValueChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledStepper control && control.ValueControl is not null)
        {

            control.ValueControl.Text = newValue?.ToString() ?? string.Empty;

        }

    }

    private static void OnValidationMessageChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledStepper control && control.HintControl is not null)
        {

            string text = (string)newValue;

            control.HintControl.Text = text;

            control.HintControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

    private static void OnKeyOrErrorsChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledStepper control)
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
