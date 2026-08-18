namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public interface IUiThreadDispatcher
{

    /// <summary>
    /// <see langword="true"/> when the caller already owns the UI thread and may touch bound state
    /// directly. Anything raising change notifications from an arbitrary thread must consult this
    /// first, because <see cref="Post"/> always defers the action to a later dispatcher turn.
    /// </summary>
    bool CheckAccess();

    void Post(Action action);

}
