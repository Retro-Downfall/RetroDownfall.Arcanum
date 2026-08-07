using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

/// <summary>
/// Renders the pointer-keyed validation error for one descriptor key. Used by the code-built editors
/// that have no <c>Labeled*</c> wrapper of their own, so every generated field can show its error.
/// </summary>
public sealed class ValidationMessageBlock : TextBlock
{

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<ValidationMessageBlock, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<ValidationMessageBlock, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

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

    static ValidationMessageBlock()
    {

        KeyProperty.Changed.AddClassHandler<ValidationMessageBlock>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<ValidationMessageBlock>((control, _) => control.RefreshValidation());

    }

    public ValidationMessageBlock()
    {

        TextWrapping = TextWrapping.Wrap;

        IsVisible = false;

        this[!ForegroundProperty] = new DynamicResourceExtension("ForgeErrorBrush");

    }

    private void RefreshValidation()
    {

        string message = string.Empty;

        if (!string.IsNullOrEmpty(Key)
            && ValidationErrors is not null
            && ValidationErrors.TryGetValue(Key, out string? found))
        {

            message = found;

        }

        Text = message;

        IsVisible = !string.IsNullOrEmpty(message);

    }

}
