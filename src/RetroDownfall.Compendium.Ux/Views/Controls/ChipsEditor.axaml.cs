using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class ChipsEditor : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ChipsEditor, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<ChipsEditor, string>(nameof(Description));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ChipsEditor, string>(nameof(Text), defaultValue: string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string> ValidationMessageProperty =
        AvaloniaProperty.Register<ChipsEditor, string>(nameof(ValidationMessage));

    public static readonly StyledProperty<string> KeyProperty =
        AvaloniaProperty.Register<ChipsEditor, string>(nameof(Key));

    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> ValidationErrorsProperty =
        AvaloniaProperty.Register<ChipsEditor, IReadOnlyDictionary<string, string>?>(nameof(ValidationErrors));

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

    static ChipsEditor()
    {

        TextProperty.Changed.AddClassHandler<ChipsEditor>((control, _) => control.RenderChips());

        KeyProperty.Changed.AddClassHandler<ChipsEditor>((control, _) => control.RefreshValidation());

        ValidationErrorsProperty.Changed.AddClassHandler<ChipsEditor>((control, _) => control.RefreshValidation());

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

    public ChipsEditor()
    {

        InitializeComponent();

        NewItemEntry.PropertyChanged += OnNewItemPropertyChanged;

        RenderChips();

    }

    private void OnNewItemKeyDown(object? sender, KeyEventArgs e)
    {

        // Fully qualified: the descriptor Key property shadows the Avalonia.Input.Key enum here.
        if (e.Key == Avalonia.Input.Key.Enter)

        {

            AddCurrentItem();

            e.Handled = true;

        }

    }

    private void OnAddClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {

        AddCurrentItem();

    }

    /// <summary>
    /// Leaving the entry commits whatever is in it. Enter and Add are easy to miss on a box that looks
    /// like every other one in the editor, and without this the value is thrown away by the very
    /// actions an operator takes to finish: pressing Save (which moves focus first), opening another
    /// section, or closing the window.
    /// </summary>
    private void OnNewItemLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CommitPendingInput();

    /// <summary>
    /// Announces that the uncommitted entry changed. Text sitting there is an operator edit even
    /// before it becomes a chip, and the editor has to treat it as one or Save stays disabled
    /// underneath the typing and the close confirmation never asks. Observed through the property
    /// rather than the control's <c>TextChanged</c> event so that programmatic assignments — the ones
    /// a test drives, and the clear that follows a commit — are seen too.
    /// </summary>
    private void OnNewItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {

        if (args.Property == TextBox.TextProperty)
        {

            PendingInputChanged?.Invoke(this, EventArgs.Empty);

        }

    }

    /// <summary>Raised whenever the uncommitted entry's text changes.</summary>
    public event EventHandler? PendingInputChanged;

    /// <summary>
    /// Commits whatever is typed in the entry but not yet added. Safe to call at any time: an empty or
    /// whitespace-only entry, or one holding a value the list already has, changes nothing.
    /// </summary>
    public void CommitPendingInput() => AddCurrentItem();

    private void AddCurrentItem()
    {

        string item = NewItemEntry.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(item))

        {

            return;

        }

        List<string> values = ParseText();

        if (!values.Contains(item, StringComparer.OrdinalIgnoreCase))

        {

            values.Add(item);

            Text = string.Join(", ", values);

        }

        NewItemEntry.Text = string.Empty;

    }

    private void RemoveItem(string item)
    {

        List<string> values = ParseText();

        values.RemoveAll(v => v.Equals(item, StringComparison.OrdinalIgnoreCase));

        Text = string.Join(", ", values);

    }

    private List<string> ParseText()
    {

        if (string.IsNullOrWhiteSpace(Text))

        {

            return [];

        }

        List<string> unique = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string part in Text.Split([','], StringSplitOptions.RemoveEmptyEntries))
        {

            string item = part.Trim();

            if (string.IsNullOrWhiteSpace(item) || !seen.Add(item))
            {

                continue;

            }

            unique.Add(item);

        }

        return unique;

    }

    private void RenderChips()
    {

        if (ChipsContainer is null)

        {

            return;

        }

        ChipsContainer.Children.Clear();

        foreach (string item in ParseText())

        {

            Button chip = new()

            {

                Content = item,

                Margin = new Thickness(0, 0, 6, 6),

            };

            chip.Click += (_, _) => RemoveItem(item);

            ChipsContainer.Children.Add(chip);

        }

    }

}
