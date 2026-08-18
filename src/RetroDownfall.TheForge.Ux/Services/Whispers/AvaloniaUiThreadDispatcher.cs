using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public sealed class AvaloniaUiThreadDispatcher : IUiThreadDispatcher
{

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

}
