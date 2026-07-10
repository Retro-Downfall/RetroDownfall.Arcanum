namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>Resolves the platform default shell for The Hearth command runner.</summary>
public interface ITerminalShellResolver
{

    TerminalShellSpec Resolve();

}
