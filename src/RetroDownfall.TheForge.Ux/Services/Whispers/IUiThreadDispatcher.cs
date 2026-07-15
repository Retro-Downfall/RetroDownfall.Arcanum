namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public interface IUiThreadDispatcher
{

    void Post(Action action);

}
