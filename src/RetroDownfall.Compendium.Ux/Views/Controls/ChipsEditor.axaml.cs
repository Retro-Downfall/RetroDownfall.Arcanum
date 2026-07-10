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

    static ChipsEditor()
    {

        TextProperty.Changed.AddClassHandler<ChipsEditor>((control, _) => control.RenderChips());

    }

    public ChipsEditor()
    {

        InitializeComponent();

        RenderChips();

    }

    private void OnNewItemKeyDown(object? sender, KeyEventArgs e)
    {

        if (e.Key == Key.Enter)

        {

            AddCurrentItem();

            e.Handled = true;

        }

    }

    private void OnAddClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {

        AddCurrentItem();

    }

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
