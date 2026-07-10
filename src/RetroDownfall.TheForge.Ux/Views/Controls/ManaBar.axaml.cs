using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.TheForge.Ux.Views.Controls;

public partial class ManaBar : UserControl
{

    public static readonly StyledProperty<double> ManaPercentProperty =
        AvaloniaProperty.Register<ManaBar, double>(nameof(ManaPercent));

    public static readonly StyledProperty<int?> TokenCountProperty =
        AvaloniaProperty.Register<ManaBar, int?>(nameof(TokenCount));

    public ManaBar()
    {

        InitializeComponent();

        PropertyChanged += OnPropertyChanged;

        LayoutUpdated += (_, _) => UpdateFillWidth();

    }

    public double ManaPercent
    {

        get => GetValue(ManaPercentProperty);

        set => SetValue(ManaPercentProperty, value);

    }

    public int? TokenCount
    {

        get => GetValue(TokenCountProperty);

        set => SetValue(TokenCountProperty, value);

    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {

        if (e.Property == ManaPercentProperty || e.Property == BoundsProperty)
        {

            UpdateFillWidth();

        }

    }

    private void UpdateFillWidth()
    {

        if (Fill is null || Track is null)
        {

            return;

        }

        double trackWidth = Track.Bounds.Width;

        if (trackWidth <= 0)
        {

            Fill.Width = 0;

            return;

        }

        double percent = Math.Clamp(ManaPercent, 0, 100);

        Fill.Width = trackWidth * (percent / 100.0);

    }

}
