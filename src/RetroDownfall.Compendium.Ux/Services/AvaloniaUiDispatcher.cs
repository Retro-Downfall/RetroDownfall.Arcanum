using Avalonia.Threading;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{

    public void Post(Action action)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            action();

            return;

        }

        Dispatcher.UIThread.Post(action);

    }

    public Task InvokeAsync(Action action)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            action();

            return Task.CompletedTask;

        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();

    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            return Task.FromResult(func());

        }

        return Dispatcher.UIThread.InvokeAsync(func).GetTask();

    }

}
