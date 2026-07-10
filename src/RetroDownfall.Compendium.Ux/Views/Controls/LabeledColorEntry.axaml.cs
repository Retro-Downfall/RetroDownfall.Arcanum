using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledColorEntry : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(Description));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(Text), defaultValue: string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(Placeholder));

    public static readonly StyledProperty<string> ValidationMessageProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(ValidationMessage));

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<LabeledColorEntry, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<LabeledColorEntry, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

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

    public string Text
    {

        get => GetValue(TextProperty);

        set => SetValue(TextProperty, value);

    }

    public string Placeholder
    {

        get => GetValue(PlaceholderProperty);

        set => SetValue(PlaceholderProperty, value);

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

    static LabeledColorEntry()
    {

        KeyProperty.Changed.AddClassHandler<LabeledColorEntry>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<LabeledColorEntry>((control, _) => control.RefreshValidation());

    }

    public LabeledColorEntry()
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
