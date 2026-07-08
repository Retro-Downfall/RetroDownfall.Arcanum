namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Simple leaf used for static campaign children like CODEX.md and Sanctum until their feature panels land.</summary>
public sealed class StaticAtelierNodeViewModel : AtelierNodeViewModel
{

    public StaticAtelierNodeViewModel(string label, string icon)
    {

        Label = label;

        Icon = icon;

    }

    public override bool HasChildren => false;

}
