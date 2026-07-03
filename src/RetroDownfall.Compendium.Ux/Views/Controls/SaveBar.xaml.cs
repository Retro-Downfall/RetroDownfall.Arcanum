using System.Windows.Input;

namespace RetroDownfall.Compendium.Ux.Views.Controls;

public partial class SaveBar : ContentView
{

    public static readonly BindableProperty IsDirtyProperty = BindableProperty.Create(
        nameof(IsDirty),
        typeof(bool),
        typeof(SaveBar),
        false,
        propertyChanged: OnStatusChanged);

    public static readonly BindableProperty IsSavingProperty = BindableProperty.Create(
        nameof(IsSaving),
        typeof(bool),
        typeof(SaveBar),
        false,
        propertyChanged: OnStatusChanged);

    public static readonly BindableProperty StatusMessageProperty = BindableProperty.Create(
        nameof(StatusMessage),
        typeof(string),
        typeof(SaveBar),
        string.Empty,
        propertyChanged: OnStatusChanged);

    public static readonly BindableProperty LastSavedAtProperty = BindableProperty.Create(
        nameof(LastSavedAt),
        typeof(DateTimeOffset?),
        typeof(SaveBar),
        null,
        propertyChanged: OnStatusChanged);

    public static readonly BindableProperty HasExternalChangeProperty = BindableProperty.Create(
        nameof(HasExternalChange),
        typeof(bool),
        typeof(SaveBar),
        false);

    public static readonly BindableProperty SaveCommandProperty = BindableProperty.Create(
        nameof(SaveCommand),
        typeof(ICommand),
        typeof(SaveBar),
        null);

    public static readonly BindableProperty RefreshCommandProperty = BindableProperty.Create(
        nameof(RefreshCommand),
        typeof(ICommand),
        typeof(SaveBar),
        null);

    public static readonly BindableProperty CancelCommandProperty = BindableProperty.Create(
        nameof(CancelCommand),
        typeof(ICommand),
        typeof(SaveBar),
        null);

    public bool IsDirty
    {

        get => (bool)GetValue(IsDirtyProperty);

        set => SetValue(IsDirtyProperty, value);

    }

    public bool IsSaving
    {

        get => (bool)GetValue(IsSavingProperty);

        set => SetValue(IsSavingProperty, value);

    }

    public string StatusMessage
    {

        get => (string)GetValue(StatusMessageProperty);

        set => SetValue(StatusMessageProperty, value);

    }

    public DateTimeOffset? LastSavedAt
    {

        get => (DateTimeOffset?)GetValue(LastSavedAtProperty);

        set => SetValue(LastSavedAtProperty, value);

    }

    public bool HasExternalChange
    {

        get => (bool)GetValue(HasExternalChangeProperty);

        set => SetValue(HasExternalChangeProperty, value);

    }

    public ICommand? SaveCommand
    {

        get => (ICommand?)GetValue(SaveCommandProperty);

        set => SetValue(SaveCommandProperty, value);

    }

    public ICommand? RefreshCommand
    {

        get => (ICommand?)GetValue(RefreshCommandProperty);

        set => SetValue(RefreshCommandProperty, value);

    }

    public ICommand? CancelCommand
    {

        get => (ICommand?)GetValue(CancelCommandProperty);

        set => SetValue(CancelCommandProperty, value);

    }

    public SaveBar()
    {

        InitializeComponent();

        UpdateStatus();

    }

    private static void OnStatusChanged(BindableObject bindable, object oldValue, object newValue)
    {

        if (bindable is SaveBar bar)

        {

            bar.UpdateStatus();

        }

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
