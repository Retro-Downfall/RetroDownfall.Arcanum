using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class SaveBar : UserControl
{

    public static readonly StyledProperty<bool> IsDirtyProperty =
        AvaloniaProperty.Register<SaveBar, bool>(nameof(IsDirty));

    public static readonly StyledProperty<bool> IsSavingProperty =
        AvaloniaProperty.Register<SaveBar, bool>(nameof(IsSaving));

    public static readonly StyledProperty<string> StatusMessageProperty =
        AvaloniaProperty.Register<SaveBar, string>(nameof(StatusMessage));

    public static readonly StyledProperty<DateTimeOffset?> LastSavedAtProperty =
        AvaloniaProperty.Register<SaveBar, DateTimeOffset?>(nameof(LastSavedAt));

    public static readonly StyledProperty<bool> HasExternalChangeProperty =
        AvaloniaProperty.Register<SaveBar, bool>(nameof(HasExternalChange));

    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<SaveBar, ICommand?>(nameof(SaveCommand));

    public static readonly StyledProperty<ICommand?> RefreshCommandProperty =
        AvaloniaProperty.Register<SaveBar, ICommand?>(nameof(RefreshCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<SaveBar, ICommand?>(nameof(CancelCommand));

    public bool IsDirty
    {

        get => GetValue(IsDirtyProperty);

        set => SetValue(IsDirtyProperty, value);

    }

    public bool IsSaving
    {

        get => GetValue(IsSavingProperty);

        set => SetValue(IsSavingProperty, value);

    }

    public string StatusMessage
    {

        get => GetValue(StatusMessageProperty);

        set => SetValue(StatusMessageProperty, value);

    }

    public DateTimeOffset? LastSavedAt
    {

        get => GetValue(LastSavedAtProperty);

        set => SetValue(LastSavedAtProperty, value);

    }

    public bool HasExternalChange
    {

        get => GetValue(HasExternalChangeProperty);

        set => SetValue(HasExternalChangeProperty, value);

    }

    public ICommand? SaveCommand
    {

        get => GetValue(SaveCommandProperty);

        set => SetValue(SaveCommandProperty, value);

    }

    public ICommand? RefreshCommand
    {

        get => GetValue(RefreshCommandProperty);

        set => SetValue(RefreshCommandProperty, value);

    }

    public ICommand? CancelCommand
    {

        get => GetValue(CancelCommandProperty);

        set => SetValue(CancelCommandProperty, value);

    }

    static SaveBar()
    {

        IsDirtyProperty.Changed.AddClassHandler<SaveBar>((control, _) => control.UpdateStatus());

        IsSavingProperty.Changed.AddClassHandler<SaveBar>((control, _) => control.UpdateStatus());

        StatusMessageProperty.Changed.AddClassHandler<SaveBar>((control, _) => control.UpdateStatus());

        LastSavedAtProperty.Changed.AddClassHandler<SaveBar>((control, _) => control.UpdateStatus());

        HasExternalChangeProperty.Changed.AddClassHandler<SaveBar>((control, _) => control.UpdateStatus());

    }

    public SaveBar()
    {

        InitializeComponent();

        UpdateStatus();

    }

    private void UpdateStatus()
    {

        if (StatusLabel is null)

        {

            return;

        }

        if (IsSaving)

        {

            StatusLabel.Text = "Saving...";

            return;

        }

        if (HasExternalChange)

        {

            StatusLabel.Text = "File changed on disk.";

            return;

        }

        if (IsDirty)

        {

            StatusLabel.Text = "Unsaved changes";

            return;

        }

        if (LastSavedAt.HasValue)

        {

            StatusLabel.Text = $"Saved at {LastSavedAt.Value:HH:mm:ss}";

            return;

        }

        StatusLabel.Text = StatusMessage;

    }

}
