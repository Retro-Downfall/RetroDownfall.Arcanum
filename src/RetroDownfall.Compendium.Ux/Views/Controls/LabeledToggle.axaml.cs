using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class LabeledToggle : UserControl
{

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledToggle, string>(nameof(Label));

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<LabeledToggle, string>(nameof(Description));

    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<LabeledToggle, bool>(nameof(IsOn), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

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

    public bool IsOn
    {

        get => GetValue(IsOnProperty);

        set => SetValue(IsOnProperty, value);

    }

    public LabeledToggle()
    {

        InitializeComponent();

    }

}
