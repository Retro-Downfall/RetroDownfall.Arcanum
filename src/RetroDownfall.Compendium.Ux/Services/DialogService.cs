namespace RetroDownfall.Compendium.Ux.Services;

public sealed class DialogService : IDialogService
{

    public Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {

        Page? page = GetCurrentPage();

        if (page is null)
        {

            return Task.CompletedTask;

        }

        return page.DisplayAlertAsync(title, message, cancel);

    }

    public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
    {

        Page? page = GetCurrentPage();

        if (page is null)
        {

            return Task.FromResult(false);

        }

        return page.DisplayAlertAsync(title, message, accept, cancel);

    }

    private static Page? GetCurrentPage()
    {

        if (Shell.Current is not null && Shell.Current.CurrentPage is not null)
        {

            return Shell.Current.CurrentPage;

        }

        if (Application.Current?.Windows is not null && Application.Current.Windows.Count > 0)
        {

            return Application.Current.Windows[0].Page;

        }

        return null;

    }

}
