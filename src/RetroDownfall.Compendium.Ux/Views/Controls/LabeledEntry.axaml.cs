using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledEntry : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(Description));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(Text), defaultValue: string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(Placeholder));

    public static readonly StyledProperty<bool> IsPasswordProperty =
        AvaloniaProperty.Register<LabeledEntry, bool>(nameof(IsPassword));

    public static readonly StyledProperty<string> ValidationMessageProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(ValidationMessage));

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<LabeledEntry, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<LabeledEntry, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

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

    public bool IsPassword
    {

        get => GetValue(IsPasswordProperty);

        set => SetValue(IsPasswordProperty, value);

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

    static LabeledEntry()
    {

        IsPasswordProperty.Changed.AddClassHandler<LabeledEntry>((control, _) => control.UpdatePasswordChar());

        KeyProperty.Changed.AddClassHandler<LabeledEntry>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<LabeledEntry>((control, _) => control.RefreshValidation());

    }

    public LabeledEntry()
    {

        InitializeComponent();

        UpdatePasswordChar();

    }

    private void UpdatePasswordChar()
    {

        if (EntryControl is null)

        {

            return;

        }

        EntryControl.PasswordChar = IsPassword ? '*' : '\0';

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
