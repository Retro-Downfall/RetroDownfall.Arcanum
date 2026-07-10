namespace RetroDownfall.Compendium.Ux.Services;

public interface IUiDispatcher
{

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> func);

}
