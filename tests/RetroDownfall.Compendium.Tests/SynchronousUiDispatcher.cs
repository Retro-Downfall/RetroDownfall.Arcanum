using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.Tests;

internal sealed class SynchronousUiDispatcher : IUiDispatcher
{

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {

        action();

        return Task.CompletedTask;

    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

}
