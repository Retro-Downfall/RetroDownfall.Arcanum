namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledToggle : ContentView
{

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label),
        typeof(string),
        typeof(LabeledToggle),
        string.Empty,
        propertyChanged: OnLabelChanged);

    public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
        nameof(Description),
        typeof(string),
        typeof(LabeledToggle),
        string.Empty,
        propertyChanged: OnDescriptionChanged);

    public static readonly BindableProperty IsOnProperty = BindableProperty.Create(
        nameof(IsOn),
        typeof(bool),
        typeof(LabeledToggle),
        false,
        BindingMode.TwoWay);

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

    public bool IsOn
    {

        get => (bool)GetValue(IsOnProperty);

        set => SetValue(IsOnProperty, value);

    }

    public LabeledToggle()
    {

        InitializeComponent();

        LabelControl.Text = Label;

        DescriptionControl.Text = Description;

    }

    private static void OnLabelChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledToggle control && control.LabelControl is not null)
        {

            control.LabelControl.Text = (string)newValue;

        }

    }

    private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is LabeledToggle control && control.DescriptionControl is not null)
        {

            string text = (string)newValue;

            control.DescriptionControl.Text = text;

            control.DescriptionControl.IsVisible = !string.IsNullOrWhiteSpace(text);

        }

    }

}
