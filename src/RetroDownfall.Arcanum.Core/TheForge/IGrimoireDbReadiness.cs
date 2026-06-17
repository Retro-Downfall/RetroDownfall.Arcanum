namespace RetroDownfall.Arcanum.Core.TheForge;

public interface IGrimoireDbReadiness
{

    bool IsReady { get; }

    void MarkReady();

}
