namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class ChipsEditor : ContentView
{

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label),
        typeof(string),
        typeof(ChipsEditor),
        string.Empty,
        propertyChanged: OnLabelChanged);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description),
        typeof(string),
        typeof(ChipsEditor),
        string.Empty,
        propertyChanged: OnDescriptionChanged);

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(ChipsEditor),
        string.Empty,
        BindingMode.TwoWay,
        propertyChanged: OnTextChanged);

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

    public string Text
    {

        get => (string)GetValue(TextProperty);

        set => SetValue(TextProperty, value);

    }

    public ChipsEditor()
    {

        InitializeComponent();

        LabelControl.Text = Label;

        DescriptionControl.Text = Description;

        RenderChips();

    }

    private void OnAddCompleted(object? sender, EventArgs e)

    {

        AddCurrentItem();

    }

    private void OnAddClicked(object? sender, EventArgs e)

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

        return Text.Split([','], StringSplitOptions.RemoveEmptyEntries)

            .Select(static s => s.Trim())

            .Where(static s => !string.IsNullOrWhiteSpace(s))

            .ToList();

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

                Text = item,

                Margin = new Thickness(0, 0, 6, 6),

            };

            chip.Clicked += (_, _) => RemoveItem(item);

            ChipsContainer.Children.Add(chip);

        }

    }

    private static void OnLabelChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is ChipsEditor control && control.LabelControl is not null)

        {

            control.LabelControl.Text = (string)newValue;

        }

    }

    private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is ChipsEditor control && control.DescriptionControl is not null)

        {

            string text = (string)newValue;

            control.DescriptionControl.Text = text;

            control.DescriptionControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

    private static void OnTextChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is ChipsEditor control)

        {

            control.RenderChips();

        }

    }

}
