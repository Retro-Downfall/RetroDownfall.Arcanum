using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledStepper : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(Description));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<LabeledStepper, double>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<LabeledStepper, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<LabeledStepper, double>(nameof(Maximum), defaultValue: 100.0);

    public static readonly StyledProperty<double> IncrementProperty =
        AvaloniaProperty.Register<LabeledStepper, double>(nameof(Increment), defaultValue: 1.0);

    public static readonly StyledProperty<string> ValidationMessageProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(ValidationMessage));

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<LabeledStepper, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

    private bool _syncingValue;

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

    public double Value
    {

        get => GetValue(ValueProperty);

        set => SetValue(ValueProperty, value);

    }

    public double Minimum
    {

        get => GetValue(MinimumProperty);

        set => SetValue(MinimumProperty, value);

    }

    public double Maximum
    {

        get => GetValue(MaximumProperty);

        set => SetValue(MaximumProperty, value);

    }

    public double Increment
    {

        get => GetValue(IncrementProperty);

        set => SetValue(IncrementProperty, value);

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

    static LabeledStepper()
    {

        ValueProperty.Changed.AddClassHandler<LabeledStepper>((control, _) => control.SyncStepperFromValue());

        KeyProperty.Changed.AddClassHandler<LabeledStepper>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<LabeledStepper>((control, _) => control.RefreshValidation());

    }

    public LabeledStepper()
    {

        InitializeComponent();

        SyncStepperFromValue();

        UpdateValueDisplay();

    }

    private void OnStepperValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {

        if (_syncingValue)

        {

            return;

        }

        Value = (double)(e.NewValue ?? 0m);

        UpdateValueDisplay();

    }

    private void SyncStepperFromValue()
    {

        if (StepperControl is null)

        {

            return;

        }

        _syncingValue = true;

        try

        {

            StepperControl.Value = (decimal)Value;

        }
        finally

        {

            _syncingValue = false;

        }

        UpdateValueDisplay();

    }

    private void UpdateValueDisplay()
    {

        if (ValueControl is not null)

        {

            ValueControl.Text = Value.ToString(System.Globalization.CultureInfo.CurrentCulture);

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
