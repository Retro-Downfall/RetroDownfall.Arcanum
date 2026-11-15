using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledStepper : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledStepper, string>(nameof(Description));

    public static readonly StyledProperty<decimal> ValueProperty =
        AvaloniaProperty.Register<LabeledStepper, decimal>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<decimal> MinimumProperty =
        AvaloniaProperty.Register<LabeledStepper, decimal>(nameof(Minimum));

    public static readonly StyledProperty<decimal> MaximumProperty =
        AvaloniaProperty.Register<LabeledStepper, decimal>(nameof(Maximum), defaultValue: 100m);

    public static readonly StyledProperty<decimal> IncrementProperty =
        AvaloniaProperty.Register<LabeledStepper, decimal>(nameof(Increment), defaultValue: 1m);

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

    public decimal Value
    {
        get => GetValue(ValueProperty);

        set => SetValue(ValueProperty, value);
    }

    public decimal Minimum
    {
        get => GetValue(MinimumProperty);

        set => SetValue(MinimumProperty, value);
    }

    public decimal Maximum
    {
        get => GetValue(MaximumProperty);

        set => SetValue(MaximumProperty, value);
    }

    public decimal Increment
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

        Value = e.NewValue ?? 0m;

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
            StepperControl.Value = Value;
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
